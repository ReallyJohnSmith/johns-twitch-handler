using System.Text.Json;
using System.Text.Json.Nodes;

namespace StreamSidekick.Services;

/// <summary>
/// Reads the active OBS profile and applies a reversible, low-load Twitch preset while OBS is closed.
/// Direct file edits are used because OBS WebSocket does not expose all profile encoder settings.
/// </summary>
public sealed class ObsProfileAnalyzer
{
    private static readonly string ObsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "obs-studio");
    private static readonly string ProfilesRoot = Path.Combine(ObsRoot, "basic", "profiles");
    private static readonly string BackupRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StreamSidekick",
        "optimizer-backup");
    private static readonly string BackupManifestPath = Path.Combine(BackupRoot, "manifest.json");

    public ObsProfileAnalysis Analyze()
    {
        var recommendation = new BroadcastPreset(
            "1280×720",
            30,
            3500,
            160,
            "NVIDIA NVENC H.264",
            "CBR",
            2,
            "P4 · single pass · Look-ahead off",
            "Bicubic",
            "Reviewed against your stream data: 720p30 cuts OBS render cadence in half, while 3500 Kbps preserves WoW UI clarity. NVENC P4 single-pass keeps encoding off the CPU; Look-ahead stays off to avoid unnecessary GPU spikes.");

        try
        {
            var profileName = FindActiveProfile(ObsRoot, ProfilesRoot);
            if (profileName is null)
            {
                return new ObsProfileAnalysis(false, null, null, null, null, null, null, "OBS profile not found.", recommendation);
            }

            var profilePath = Path.Combine(ProfilesRoot, profileName);
            var values = ParseIni(Path.Combine(profilePath, "basic.ini"));
            var channelName = Read(values, "Twitch", "Name");
            var width = ReadInt(values, "Video", "OutputCX");
            var height = ReadInt(values, "Video", "OutputCY");
            var fps = ReadInt(values, "Video", "FPSCommon") ?? ReadInt(values, "Video", "FPSInt");
            var encoder = Read(values, "AdvOut", "Encoder") ?? Read(values, "SimpleOutput", "StreamEncoder");
            var bitrate = ReadEncoderBitrate(profilePath) ?? ReadInt(values, "SimpleOutput", "VBitrate");
            var currentResolution = width is not null && height is not null ? $"{width}×{height}" : null;
            var needsChange = width > 1280 || height > 720 || fps > 30 || bitrate > 4000;
            var verdict = needsChange
                ? "Your recorded stream had essentially zero render or encoding loss, so NVENC is working well. CPU and background memory were the tighter resources; the reviewed 720p30 preset reduces OBS render cadence while retaining enough bitrate for readable WoW text."
                : "Your current profile is already close to the reviewed CPU-first target.";

            return new ObsProfileAnalysis(
                true,
                profileName,
                channelName,
                currentResolution,
                fps,
                FriendlyEncoderName(encoder),
                bitrate,
                verdict,
                recommendation);
        }
        catch (Exception exception)
        {
            return new ObsProfileAnalysis(false, null, null, null, null, null, null, $"Could not analyze OBS: {exception.Message}", recommendation);
        }
    }

    public ObsOptimizerStatus GetOptimizerStatus()
    {
        var backup = ReadBackupManifest();
        var available = backup is not null && File.Exists(Path.Combine(BackupRoot, "basic.ini"));
        return new ObsOptimizerStatus(IsObsRunning(), available, available ? backup!.ProfileName : null, available ? backup!.CreatedAt : null);
    }

    public ObsOptimizerActionResult ApplyRecommendation()
    {
        EnsureObsClosed();
        var profileName = FindActiveProfile(ObsRoot, ProfilesRoot)
            ?? throw new InvalidOperationException("The active OBS profile could not be found.");
        var profilePath = SafeProfilePath(profileName);
        var basicPath = Path.Combine(profilePath, "basic.ini");
        var encoderPath = Path.Combine(profilePath, "streamEncoder.json");
        if (!File.Exists(basicPath)) throw new FileNotFoundException("The active OBS profile is missing basic.ini.");

        var existingBackup = ReadBackupManifest();
        if (existingBackup is not null && !existingBackup.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Restore the saved {existingBackup.ProfileName} settings before optimizing another OBS profile.");
        }

        if (existingBackup is null)
        {
            // Only capture the original once so repeated Apply clicks never overwrite the user's restore point.
            Directory.CreateDirectory(BackupRoot);
            File.Copy(basicPath, Path.Combine(BackupRoot, "basic.ini"), true);
            var hadEncoderFile = File.Exists(encoderPath);
            if (hadEncoderFile) File.Copy(encoderPath, Path.Combine(BackupRoot, "streamEncoder.json"), true);
            WriteBackupManifest(new OptimizerBackup(profileName, DateTimeOffset.UtcNow, hadEncoderFile));
        }

        var values = ParseIni(basicPath);
        var outputMode = Read(values, "Output", "Mode") ?? "Simple";
        SetIniValue(basicPath, "Video", "OutputCX", "1280");
        SetIniValue(basicPath, "Video", "OutputCY", "720");
        SetIniValue(basicPath, "Video", "FPSType", "0");
        SetIniValue(basicPath, "Video", "FPSCommon", "30");
        SetIniValue(basicPath, "Video", "ScaleType", "bicubic");

        if (outputMode.Equals("Advanced", StringComparison.OrdinalIgnoreCase))
        {
            SetIniValue(basicPath, "AdvOut", "Encoder", "obs_nvenc_h264_tex");
            SetIniValue(basicPath, "AdvOut", "Track1Bitrate", "160");
            WriteAdvancedEncoderPreset(encoderPath);
        }
        else
        {
            SetIniValue(basicPath, "SimpleOutput", "VBitrate", "3500");
            SetIniValue(basicPath, "SimpleOutput", "ABitrate", "160");
            SetIniValue(basicPath, "SimpleOutput", "StreamEncoder", "nvenc");
            SetIniValue(basicPath, "SimpleOutput", "NVENCPreset2", "p4");
        }

        return new ObsOptimizerActionResult(
            true,
            $"Applied the 720p30 performance preset to {profileName}. Open OBS to load the new settings.",
            Analyze(),
            GetOptimizerStatus());
    }

    public ObsOptimizerActionResult RestorePreviousSettings()
    {
        EnsureObsClosed();
        var backup = ReadBackupManifest()
            ?? throw new InvalidOperationException("There is no saved OBS configuration to restore.");
        var profilePath = SafeProfilePath(backup.ProfileName);
        var basicBackup = Path.Combine(BackupRoot, "basic.ini");
        if (!File.Exists(basicBackup)) throw new FileNotFoundException("The saved OBS backup is incomplete.");

        Directory.CreateDirectory(profilePath);
        File.Copy(basicBackup, Path.Combine(profilePath, "basic.ini"), true);
        var encoderPath = Path.Combine(profilePath, "streamEncoder.json");
        var encoderBackup = Path.Combine(BackupRoot, "streamEncoder.json");
        if (backup.HadEncoderFile)
        {
            if (!File.Exists(encoderBackup)) throw new FileNotFoundException("The saved encoder backup is incomplete.");
            File.Copy(encoderBackup, encoderPath, true);
        }
        else if (File.Exists(encoderPath))
        {
            File.Delete(encoderPath);
        }

        File.Move(BackupManifestPath, Path.Combine(BackupRoot, "last-restored.json"), true);
        return new ObsOptimizerActionResult(
            false,
            $"Restored the previous settings for {backup.ProfileName}. Open OBS to load them.",
            Analyze(),
            GetOptimizerStatus());
    }

    private static string? FindActiveProfile(string obsRoot, string profilesRoot)
    {
        var global = ParseIni(Path.Combine(obsRoot, "global.ini"));
        var active = Read(global, "Basic", "Profile");
        if (!string.IsNullOrWhiteSpace(active) && Directory.Exists(Path.Combine(profilesRoot, active)))
        {
            return active;
        }

        return Directory.Exists(profilesRoot)
            ? Directory.EnumerateDirectories(profilesRoot).Select(Path.GetFileName).FirstOrDefault()
            : null;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        var section = "";
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                if (!result.ContainsKey(section)) result[section] = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            var split = line.IndexOf('=');
            if (split <= 0 || section.Length == 0) continue;
            result[section][line[..split].Trim()] = line[(split + 1)..].Trim();
        }
        return result;
    }

    private static string? Read(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        ini.TryGetValue(section, out var values) && values.TryGetValue(key, out var value) ? value : null;

    private static int? ReadInt(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        int.TryParse(Read(ini, section, key), out var value) ? value : null;

    private static int? ReadEncoderBitrate(string profilePath)
    {
        var path = Path.Combine(profilePath, "streamEncoder.json");
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.TryGetProperty("bitrate", out var bitrate) ? bitrate.GetInt32() : null;
    }

    private static string SafeProfilePath(string profileName)
    {
        var root = Path.GetFullPath(ProfilesRoot) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(ProfilesRoot, profileName));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path))
        {
            throw new InvalidOperationException("The saved OBS profile path is not valid.");
        }
        return path;
    }

    private static void EnsureObsClosed()
    {
        if (IsObsRunning())
        {
            throw new InvalidOperationException("Close OBS before applying or restoring broadcast settings.");
        }
    }

    private static bool IsObsRunning() => ProcessUtilities.IsAnyRunning("obs64", "obs32");

    private static void SetIniValue(string path, string section, string key, string value)
    {
        var lines = File.ReadAllLines(path).ToList();
        var sectionHeader = $"[{section}]";
        var sectionIndex = lines.FindIndex(line => line.Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
        }
        else
        {
            var nextSection = lines.FindIndex(sectionIndex + 1, line => line.TrimStart().StartsWith('['));
            if (nextSection < 0) nextSection = lines.Count;
            var keyIndex = lines.FindIndex(sectionIndex + 1, nextSection - sectionIndex - 1, line =>
            {
                var separator = line.IndexOf('=');
                return separator > 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase);
            });
            if (keyIndex >= 0) lines[keyIndex] = $"{key}={value}";
            else lines.Insert(nextSection, $"{key}={value}");
        }

        var temporary = path + ".streamsidekick.tmp";
        // Replace only after the complete temporary file is written, reducing corruption risk on interruption.
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, path, true);
    }

    private static void WriteAdvancedEncoderPreset(string path)
    {
        JsonObject settings;
        try
        {
            settings = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch
        {
            settings = new JsonObject();
        }

        settings["rate_control"] = "CBR";
        settings["bitrate"] = 3500;
        settings["keyint_sec"] = 2;
        settings["preset"] = "p4";
        settings["multipass"] = "disabled";
        settings["lookahead"] = false;
        // The 3090 had ample GPU headroom, so retain these low-cost quality aids for WoW text and motion.
        settings["psycho_aq"] = true;
        settings["bf"] = 2;

        var temporary = path + ".streamsidekick.tmp";
        File.WriteAllText(temporary, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static OptimizerBackup? ReadBackupManifest()
    {
        try
        {
            return File.Exists(BackupManifestPath)
                ? JsonSerializer.Deserialize<OptimizerBackup>(File.ReadAllText(BackupManifestPath))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBackupManifest(OptimizerBackup backup)
    {
        Directory.CreateDirectory(BackupRoot);
        var temporary = BackupManifestPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, BackupManifestPath, true);
    }

    private static string? FriendlyEncoderName(string? value) => value switch
    {
        "obs_nvenc_h264_tex" or "nvenc" => "NVIDIA NVENC H.264",
        "obs_x264" or "x264" => "x264 (CPU)",
        null => null,
        _ => value
    };

    private sealed record OptimizerBackup(string ProfileName, DateTimeOffset CreatedAt, bool HadEncoderFile);
}
