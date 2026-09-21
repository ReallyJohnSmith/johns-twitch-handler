using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StreamSidekick.Services;

public sealed record WowAddonOverview(
    bool Found,
    bool GameRunning,
    DateTimeOffset ScannedAt,
    IReadOnlyList<string> Insights,
    IReadOnlyList<WowEditionInfo> Editions);

public sealed record WowEditionInfo(
    string Id,
    string Name,
    string Path,
    int AddonFolderCount,
    double TotalAddonSizeMb,
    bool ProfilerInstalled,
    DateTimeOffset? ProfileCapturedAt,
    IReadOnlyList<WowCharacterProfile> CharacterProfiles,
    IReadOnlyList<WowAddonInfo> Addons);

public sealed record WowCharacterProfile(
    string Id,
    string Realm,
    string Character,
    int EnabledCount,
    int DisabledCount,
    IReadOnlyDictionary<string, bool> AddonStates);

public sealed record WowAddonToggleRequest(string EditionId, string ProfileId, string AddonName, bool Enabled);
public sealed record WowAddonPresetRequest(string EditionId, string ProfileId, string Name);
public sealed record WowAddonPresetApplyRequest(string EditionId, string ProfileId, string Name);
public sealed record WowAddonPresetDeleteRequest(string EditionId, string Name);
public sealed record WowAddonPresetSummary(string EditionId, string Name, int EnabledCount, int DisabledCount, DateTimeOffset SavedAt);
public sealed record WowEditionLocation(string Id, string Name, string Path);

public sealed record WowAddonInfo(
    string FolderName,
    string Title,
    string? Version,
    double FolderSizeMb,
    double SavedVariablesSizeMb,
    DateTimeOffset LastModified,
    double? RecentAverageMs,
    double? SessionAverageMs,
    double? PeakMs,
    int? SpikesOver10Ms,
    int? SpikesOver50Ms);

/// <summary>
/// Discovers WoW installations, inventories addon cost signals, and safely edits per-character addon profiles.
/// Expensive disk scans are cached because the dashboard polls much more often than addons normally change.
/// </summary>
public sealed class WowAddonService(IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ProfilerRow = new(
        "\\[\"(?<name>(?:\\\\.|[^\"])*)\"\\]\\s*=\\s*\\{\\s*recent\\s*=\\s*(?<recent>[0-9.]+),\\s*session\\s*=\\s*(?<session>[0-9.]+),\\s*peak\\s*=\\s*(?<peak>[0-9.]+),\\s*over10\\s*=\\s*(?<over10>[0-9]+),\\s*over50\\s*=\\s*(?<over50>[0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _sync = new();
    private WowAddonOverview? _cached;

    private string PresetPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "addon-presets.json");

    public IReadOnlyList<WowEditionLocation> GetEditionLocations() =>
        DiscoverEditionRoots().Select(item => new WowEditionLocation(item.Id, item.Name, item.Path)).ToList();

    public IReadOnlyList<WowAddonPresetSummary> GetPresets()
    {
        lock (_sync)
        {
            return ReadPresets().Select(preset => new WowAddonPresetSummary(
                preset.EditionId,
                preset.Name,
                preset.AddonStates.Count(item => item.Value),
                preset.AddonStates.Count(item => !item.Value),
                preset.SavedAt)).OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public IReadOnlyList<WowAddonPresetSummary> SavePreset(WowAddonPresetRequest request)
    {
        var name = ValidatePresetName(request.Name);
        lock (_sync)
        {
            var overview = GetOverview(true);
            var edition = overview.Editions.FirstOrDefault(item => item.Id == request.EditionId)
                ?? throw new DirectoryNotFoundException("The selected WoW edition was not found.");
            var profile = edition.CharacterProfiles.FirstOrDefault(item => item.Id == request.ProfileId)
                ?? throw new FileNotFoundException("The selected character addon profile was not found.");
            var states = edition.Addons.ToDictionary(
                addon => addon.FolderName,
                addon => !profile.AddonStates.TryGetValue(addon.FolderName, out var enabled) || enabled,
                StringComparer.OrdinalIgnoreCase);

            var presets = ReadPresets();
            presets.RemoveAll(item => item.EditionId.Equals(request.EditionId, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            presets.Add(new WowAddonPreset(request.EditionId, name, DateTimeOffset.UtcNow, states));
            WritePresets(presets);
            return GetPresets();
        }
    }

    public WowAddonOverview ApplyPreset(WowAddonPresetApplyRequest request)
    {
        if (IsGameRunning()) throw new InvalidOperationException("Close World of Warcraft before applying an addon preset.");
        var name = ValidatePresetName(request.Name);
        lock (_sync)
        {
            var edition = DiscoverEditionRoots().FirstOrDefault(item => item.Id == request.EditionId);
            if (edition == default) throw new DirectoryNotFoundException("The selected WoW edition was not found.");
            var profilePath = FindProfilePaths(edition.Path)
                .FirstOrDefault(item => ProfileId(item.Path) == request.ProfileId).Path;
            if (string.IsNullOrWhiteSpace(profilePath)) throw new FileNotFoundException("The selected character addon profile was not found.");
            var preset = ReadPresets().FirstOrDefault(item => item.EditionId.Equals(request.EditionId, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new FileNotFoundException("The selected addon preset was not found.");

            var lines = File.ReadAllLines(profilePath).ToList();
            foreach (var state in preset.AddonStates)
            {
                if (!Directory.Exists(Path.Combine(edition.Path, "Interface", "AddOns", state.Key))) continue;
                var replacement = $"{state.Key}: {(state.Value ? "enabled" : "disabled")}";
                var index = lines.FindIndex(line => line.StartsWith(state.Key + ":", StringComparison.OrdinalIgnoreCase));
                if (index >= 0) lines[index] = replacement;
                else lines.Add(replacement);
            }

            WriteProfileWithBackup(profilePath, lines);
            _cached = null;
            return GetOverview(true);
        }
    }

    public IReadOnlyList<WowAddonPresetSummary> DeletePreset(WowAddonPresetDeleteRequest request)
    {
        var name = ValidatePresetName(request.Name);
        lock (_sync)
        {
            var presets = ReadPresets();
            presets.RemoveAll(item => item.EditionId.Equals(request.EditionId, StringComparison.OrdinalIgnoreCase)
                && item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            WritePresets(presets);
            return GetPresets();
        }
    }

    public WowAddonOverview GetOverview(bool refresh = false)
    {
        lock (_sync)
        {
            if (!refresh && _cached is not null && DateTimeOffset.UtcNow - _cached.ScannedAt < TimeSpan.FromMinutes(2))
            {
                return _cached;
            }

            var roots = DiscoverEditionRoots();
            var editions = roots.Select(BuildEdition).ToList();
            var insights = BuildInsights(editions);
            _cached = new WowAddonOverview(
                editions.Count > 0,
                IsGameRunning(),
                DateTimeOffset.UtcNow,
                insights,
                editions);
            return _cached;
        }
    }

    public WowAddonOverview InstallProfiler()
    {
        if (IsGameRunning())
        {
            throw new InvalidOperationException("Close World of Warcraft before installing or updating the profiler addon.");
        }

        var source = Path.Combine(environment.ContentRootPath, "WowAddon", "StreamSidekickProfiler");
        var tocSource = Path.Combine(source, "StreamSidekickProfiler.toc");
        var luaSource = Path.Combine(source, "StreamSidekickProfiler.lua");
        if (!File.Exists(tocSource) || !File.Exists(luaSource))
        {
            throw new FileNotFoundException("The bundled StreamSidekickProfiler addon files are missing.");
        }

        var roots = DiscoverEditionRoots();
        if (roots.Count == 0)
        {
            throw new DirectoryNotFoundException("No World of Warcraft installation was found.");
        }

        foreach (var edition in roots)
        {
            var addonsRoot = Path.GetFullPath(Path.Combine(edition.Path, "Interface", "AddOns"));
            var editionRoot = Path.GetFullPath(edition.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!addonsRoot.StartsWith(editionRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Refusing to install outside the detected World of Warcraft edition.");
            }

            var destination = Path.Combine(addonsRoot, "StreamSidekickProfiler");
            Directory.CreateDirectory(destination);
            File.Copy(tocSource, Path.Combine(destination, Path.GetFileName(tocSource)), true);
            File.Copy(luaSource, Path.Combine(destination, Path.GetFileName(luaSource)), true);
        }

        lock (_sync) _cached = null;
        return GetOverview(true);
    }

    public WowAddonOverview SetAddonEnabled(WowAddonToggleRequest request)
    {
        if (IsGameRunning())
        {
            throw new InvalidOperationException("Close World of Warcraft before changing addon states.");
        }
        if (string.IsNullOrWhiteSpace(request.AddonName)
            || request.AddonName.IndexOfAny(new[] { ':', '\r', '\n' }) >= 0)
        {
            throw new ArgumentException("Invalid addon name.");
        }

        var edition = DiscoverEditionRoots().FirstOrDefault(item => item.Id == request.EditionId);
        if (edition == default)
        {
            throw new DirectoryNotFoundException("The selected WoW edition was not found.");
        }
        if (!Directory.Exists(Path.Combine(edition.Path, "Interface", "AddOns", request.AddonName)))
        {
            throw new DirectoryNotFoundException("The selected addon folder was not found.");
        }

        var profilePath = FindProfilePaths(edition.Path)
            .FirstOrDefault(item => ProfileId(item.Path) == request.ProfileId).Path;
        if (string.IsNullOrWhiteSpace(profilePath))
        {
            throw new FileNotFoundException("The selected character addon profile was not found.");
        }

        var lines = File.ReadAllLines(profilePath).ToList();
        var replacement = $"{request.AddonName}: {(request.Enabled ? "enabled" : "disabled")}";
        var index = lines.FindIndex(line => line.StartsWith(request.AddonName + ":", StringComparison.OrdinalIgnoreCase));
        if (index >= 0) lines[index] = replacement;
        else lines.Add(replacement);

        WriteProfileWithBackup(profilePath, lines);

        lock (_sync) _cached = null;
        return GetOverview(true);
    }

    private static List<(string Id, string Name, string Path)> DiscoverEditionRoots()
    {
        var wowRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\World of Warcraft",
            @"C:\Program Files (x86)\World of Warcraft",
            @"C:\Program Files\World of Warcraft"
        };

        var installPath = OperatingSystem.IsWindows()
            ? Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Blizzard Entertainment\World of Warcraft",
                "InstallPath",
                null) as string
            : null;
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            var directory = new DirectoryInfo(installPath.Trim());
            wowRoots.Add(directory.Parent?.FullName ?? directory.FullName);
        }

        var editions = new (string Id, string Name, string Folder)[]
        {
            ("retail", "Retail", "_retail_"),
            ("classic", "Classic", "_classic_"),
            ("classic-era", "Classic Era", "_classic_era_"),
            ("ptr", "Public Test Realm", "_ptr_")
        };

        return wowRoots.Where(Directory.Exists)
            .SelectMany(root => editions.Select(edition =>
                (edition.Id, edition.Name, Path: Path.Combine(root, edition.Folder))))
            .Where(candidate => Directory.Exists(Path.Combine(candidate.Path, "Interface", "AddOns")))
            .DistinctBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static WowEditionInfo BuildEdition((string Id, string Name, string Path) edition)
    {
        var addonsRoot = Path.Combine(edition.Path, "Interface", "AddOns");
        var savedVariableSizes = FindSavedVariableSizes(edition.Path);
        var profiler = ReadProfilerData(edition.Path);
        var addons = new List<WowAddonInfo>();

        foreach (var directory in Directory.EnumerateDirectories(addonsRoot))
        {
            try
            {
                var info = new DirectoryInfo(directory);
                var metadata = ReadMetadata(directory, info.Name);
                var sizeBytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
                profiler.Rows.TryGetValue(info.Name, out var metrics);
                savedVariableSizes.TryGetValue(info.Name, out var savedBytes);
                addons.Add(new WowAddonInfo(
                    info.Name,
                    metadata.Title ?? info.Name,
                    metadata.Version,
                    Math.Round(sizeBytes / 1024d / 1024d, 2),
                    Math.Round(savedBytes / 1024d / 1024d, 2),
                    info.LastWriteTimeUtc,
                    metrics?.Recent,
                    metrics?.Session,
                    metrics?.Peak,
                    metrics?.Over10,
                    metrics?.Over50));
            }
            catch
            {
                // A single locked or malformed addon should not prevent the rest of the inventory.
            }
        }

        var sorted = addons
            .OrderByDescending(addon => addon.RecentAverageMs.HasValue)
            .ThenByDescending(addon => addon.RecentAverageMs ?? 0)
            .ThenByDescending(addon => addon.FolderSizeMb)
            .ToList();

        return new WowEditionInfo(
            edition.Id,
            edition.Name,
            edition.Path,
            addons.Count,
            Math.Round(addons.Sum(addon => addon.FolderSizeMb), 1),
            Directory.Exists(Path.Combine(addonsRoot, "StreamSidekickProfiler")),
            profiler.CapturedAt,
            ReadCharacterProfiles(edition.Path),
            sorted);
    }

    private static List<WowCharacterProfile> ReadCharacterProfiles(string editionPath)
    {
        return FindProfilePaths(editionPath).Select(item =>
        {
            var states = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadLines(item.Path))
            {
                var separator = line.LastIndexOf(':');
                if (separator <= 0) continue;
                states[line[..separator].Trim()] = line[(separator + 1)..].Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase);
            }
            return new WowCharacterProfile(
                ProfileId(item.Path),
                item.Realm,
                item.Character,
                states.Count(entry => entry.Value),
                states.Count(entry => !entry.Value),
                states);
        }).OrderBy(profile => profile.Realm).ThenBy(profile => profile.Character).ToList();
    }

    private static List<(string Path, string Realm, string Character)> FindProfilePaths(string editionPath)
    {
        var accounts = Path.Combine(editionPath, "WTF", "Account");
        if (!Directory.Exists(accounts)) return [];
        try
        {
            return Directory.EnumerateFiles(accounts, "AddOns.txt", SearchOption.AllDirectories)
                .Select(path =>
                {
                    var relative = Path.GetRelativePath(accounts, path).Split(Path.DirectorySeparatorChar);
                    return relative.Length >= 4
                        ? (Path: path, Realm: relative[^3], Character: relative[^2])
                        : (Path: "", Realm: "", Character: "");
                })
                .Where(item => item.Path.Length > 0)
                .ToList();
        }
        catch { return []; }
    }

    private static string ProfileId(string path) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())))[..16];

    private static void WriteProfileWithBackup(string profilePath, IReadOnlyList<string> lines)
    {
        // WoW owns this file, so retain a simple recovery copy and atomically replace the edited version.
        var backup = profilePath + ".streamsidekick.bak";
        var temporary = profilePath + ".streamsidekick.tmp";
        File.Copy(profilePath, backup, true);
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, profilePath, true);
    }

    private static string ValidatePresetName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 40 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Preset names must be 1–40 characters and cannot contain file-name symbols.");
        return name;
    }

    private List<WowAddonPreset> ReadPresets()
    {
        if (!File.Exists(PresetPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<WowAddonPreset>>(File.ReadAllText(PresetPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WritePresets(IReadOnlyList<WowAddonPreset> presets)
    {
        var directory = Path.GetDirectoryName(PresetPath)!;
        Directory.CreateDirectory(directory);
        var temporary = PresetPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(presets, JsonOptions));
        File.Move(temporary, PresetPath, true);
    }

    private static Dictionary<string, long> FindSavedVariableSizes(string editionPath)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var accounts = Path.Combine(editionPath, "WTF", "Account");
        if (!Directory.Exists(accounts)) return result;
        try
        {
            foreach (var path in Directory.EnumerateFiles(accounts, "*.lua", SearchOption.AllDirectories)
                         .Where(path => path.Contains($"{Path.DirectorySeparatorChar}SavedVariables{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                result[name] = result.GetValueOrDefault(name) + new FileInfo(path).Length;
            }
        }
        catch { }
        return result;
    }

    private static (string? Title, string? Version) ReadMetadata(string directory, string folderName)
    {
        var toc = Directory.EnumerateFiles(directory, "*.toc", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Equals(folderName, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (toc is null) return (null, null);

        string? title = null;
        string? version = null;
        foreach (var line in File.ReadLines(toc).Take(80))
        {
            if (line.StartsWith("## Title:", StringComparison.OrdinalIgnoreCase)) title = CleanMetadataText(line[9..]);
            if (line.StartsWith("## Version:", StringComparison.OrdinalIgnoreCase)) version = CleanMetadataText(line[11..]);
        }
        return (title, version);
    }

    private static string CleanMetadataText(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), @"\|c[0-9a-fA-F]{8}|\|r", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\|T.*?\|t", "", RegexOptions.IgnoreCase);
        return Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
    }

    private static ProfilerSnapshot ReadProfilerData(string editionPath)
    {
        var accounts = Path.Combine(editionPath, "WTF", "Account");
        if (!Directory.Exists(accounts)) return new(null, new(StringComparer.OrdinalIgnoreCase));
        try
        {
            var latest = Directory.EnumerateFiles(accounts, "StreamSidekickProfiler.lua", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latest is null) return new(null, new(StringComparer.OrdinalIgnoreCase));
            var content = File.ReadAllText(latest.FullName);
            var rows = new Dictionary<string, ProfilerMetrics>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in ProfilerRow.Matches(content))
            {
                rows[Regex.Unescape(match.Groups["name"].Value)] = new ProfilerMetrics(
                    ParseDouble(match, "recent"),
                    ParseDouble(match, "session"),
                    ParseDouble(match, "peak"),
                    ParseInt(match, "over10"),
                    ParseInt(match, "over50"));
            }
            return new(latest.LastWriteTimeUtc, rows);
        }
        catch { return new(null, new(StringComparer.OrdinalIgnoreCase)); }
    }

    private static List<string> BuildInsights(IReadOnlyList<WowEditionInfo> editions)
    {
        var insights = new List<string>();
        foreach (var edition in editions)
        {
            var names = edition.Addons.Select(addon => addon.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (names.Contains("BigWigs") && names.Contains("DBM-Core"))
                insights.Add($"{edition.Name}: BigWigs and DBM are both installed. A stream profile should normally load only one boss-mod family.");
            var regionDatabases = edition.Addons.Count(addon => addon.FolderName.StartsWith("RaiderIO_DB_", StringComparison.OrdinalIgnoreCase));
            if (regionDatabases > 3)
                insights.Add($"{edition.Name}: {regionDatabases} Raider.IO region database modules are installed; keep only the region/data sets you use.");
            if (edition.AddonFolderCount >= 50)
                insights.Add($"{edition.Name}: {edition.AddonFolderCount} addon folders were found. Use a lean stream profile and verify actual runtime cost with the profiler.");
        }
        return insights;
    }

    private static bool IsGameRunning() => ProcessUtilities.IsAnyRunning("Wow", "WowClassic");

    private static double ParseDouble(Match match, string group) =>
        double.TryParse(match.Groups[group].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Math.Round(value, 4) : 0;

    private static int ParseInt(Match match, string group) =>
        int.TryParse(match.Groups[group].Value, out var value) ? value : 0;

    private sealed record ProfilerSnapshot(DateTimeOffset? CapturedAt, Dictionary<string, ProfilerMetrics> Rows);
    private sealed record ProfilerMetrics(double Recent, double Session, double Peak, int Over10, int Over50);
    private sealed record WowAddonPreset(string EditionId, string Name, DateTimeOffset SavedAt, Dictionary<string, bool> AddonStates);
}
