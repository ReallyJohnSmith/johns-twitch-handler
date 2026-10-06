using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using StreamSidekick.Services;

// No accounts, monitoring services, or OBS connections are constructed by these checks.
var directory = Directory.CreateTempSubdirectory("handler-overlay-checks-").FullName;
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PreferencesDirectory"] = directory
}).Build();
MusicOverlaySettingsStore Open() => new(configuration, NullLogger<MusicOverlaySettingsStore>.Instance);
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    Console.WriteLine($"PASS: {message}");
}

var settings = Open();
Check(settings.Get().Visible, "Existing installs keep the overlay visible by default");
settings.Set(new(false));
Check(!Open().Get().Visible, "Hidden preference survives restart");
settings.Set(new(true));
Check(Open().Get().Visible, "Shown preference survives restart");

var path = Path.Combine(directory, "music-overlay.json");
File.WriteAllText(path, "{}");
Check(!Open().Get().Visible, "Missing visibility fails closed");
File.WriteAllText(path, "not json");
Check(!Open().Get().Visible, "Damaged settings fail closed");
File.Delete(path);
Directory.CreateDirectory(path);
try
{
    settings.Set(new(false));
    throw new InvalidOperationException("An unwritable destination should reject the change");
}
catch (IOException) { Check(settings.Get().Visible, "Save failure does not publish a false success"); }
catch (UnauthorizedAccessException) { Check(settings.Get().Visible, "Save failure does not publish a false success"); }
Directory.Delete(path);

if (args.Length == 2 && args[0] is "--serve" or "--serve-layout")
{
    // Optional isolated visual fixture. Serves the real overlay assets with sample music only.
    var webRoot = Path.GetFullPath(args[1]);
    if (!File.Exists(Path.Combine(webRoot, "music-overlay.html")))
        throw new ArgumentException("Pass the absolute path to the Handler wwwroot directory.");
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
    builder.WebHost.UseUrls("http://127.0.0.1:5287");
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    var app = builder.Build();
    if (args[0] == "--serve-layout")
    {
        // Exercise the real layout/UI code but omit boot(): no account loading or monitoring polls.
        var script = File.ReadAllText(Path.Combine(webRoot, "app.js"));
        if (!script.Contains("  await boot();")) throw new InvalidOperationException("Layout fixture startup marker changed.");
        script = script.Replace("  await boot();", "  document.title = 'Layout test only'; document.querySelector('#release-channel-pill').textContent = 'Layout test only';");
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/app.js")
            {
                context.Response.ContentType = "text/javascript";
                await context.Response.WriteAsync(script);
            }
            else await next();
        });
        JsonElement savedLayout = JsonSerializer.SerializeToElement(new { panelOrder = Array.Empty<string>(), panelPages = new { } });
        app.MapGet("/api/dashboard-layout", () => Results.Json(savedLayout));
        app.MapPut("/api/dashboard-layout", (JsonElement value) => { savedLayout = value.Clone(); return Results.Json(savedLayout); });
        app.MapGet("/fixture/narrow", () => Results.Content("<html><body style='margin:0;background:#222'><iframe title='Narrow layout test' src='/index.html' style='display:block;width:390px;height:760px;border:0'></iframe></body></html>", "text/html"));
    }
    app.UseDefaultFiles();
    app.UseStaticFiles();
    var bossFocus = false;
    var requests = 0;
    app.MapGet("/api/overlay/status", () => new { musicOverlayVisible = settings.Get().Visible, tangiaBossFocusActive = bossFocus });
    app.MapPut("/api/overlay/music", (MusicOverlaySettings options) => settings.Set(options));
    app.MapPut("/fixture/boss-focus/{active:bool}", (bool active) => bossFocus = active);
    app.MapGet("/fixture/requests", () => new { requests });
    app.MapGet("/api/spotify/status", () =>
    {
        requests++;
        return new { connected = true, playback = new { isPlaying = true, deviceName = "Test device", track = new { title = "Overlay test track", artist = "Sample artist" } }, queue = Array.Empty<object>() };
    });
    await app.RunAsync();
}
