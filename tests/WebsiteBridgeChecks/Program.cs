using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using StreamSidekick.Services;

var directory = Path.Combine(Path.GetTempPath(), "handler-bridge-checks-" + Guid.NewGuid().ToString("N"));
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
    ["WebsiteBridge:WebsiteOrigin"] = "https://dashboard.example",
    ["WebsiteBridge:DataDirectory"] = directory
}).Build();
var protection = new EphemeralDataProtectionProvider();
var clock = new TestClock();
var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls(args.Contains("--browser-fixture") ? "http://127.0.0.1:5274" : "http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Configuration.AddConfiguration(configuration);
builder.Services.AddSingleton<IDataProtectionProvider>(protection);
builder.Services.AddSingleton<TimeProvider>(clock);
builder.Services.AddSingleton<WebsiteBridge>();
var app = builder.Build();
app.UseWebsiteBridge(); app.MapWebsiteBridge();
if (args.Contains("--browser-fixture"))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/StreamSidekick/wwwroot"))) });
var mutations = 0;
app.MapGet("/api/health", () => new { ok = true });
app.MapPost("/api/action", () => { mutations++; return Results.Ok(); });
await app.StartAsync();
if (args.Contains("--browser-fixture"))
{
    Console.WriteLine("Pairing-only test helper on http://127.0.0.1:5274. No OBS, Twitch, or monitoring services are loaded.");
    try { await app.WaitForShutdownAsync(); }
    finally { await app.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    return;
}
using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
var bridge = app.Services.GetRequiredService<WebsiteBridge>();
var passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS: " + name); }
async Task<HttpResponseMessage> Request(string method, string path, string? origin = null, string? token = null, object? body = null) {
    var request = new HttpRequestMessage(new HttpMethod(method), path);
    if (origin is not null) request.Headers.Add("Origin", origin);
    if (token is not null) request.Headers.Add(WebsiteBridge.TokenHeader, token);
    if (body is not null) request.Content = JsonContent.Create(body);
    return await client.SendAsync(request);
}
string Code() => JsonSerializer.SerializeToElement(bridge.CreateCode()).GetProperty("code").GetString()!;
try {
    Check((await Request("GET", "/api/health")).IsSuccessStatusCode, "local dashboard remains compatible");
    Check((await Request("GET", "/api/health", "https://evil.example")).StatusCode == HttpStatusCode.Forbidden, "unapproved origin denied");
    Check((await Request("POST", "/api/action", "null", body: new { })).StatusCode == HttpStatusCode.Forbidden, "opaque origin cannot mutate");
    var preflight = await Request("OPTIONS", "/api/action", "https://dashboard.example");
    Check(preflight.StatusCode == HttpStatusCode.NoContent && preflight.Headers.GetValues("Access-Control-Allow-Origin").Single() == "https://dashboard.example", "exact-origin preflight");
    Check(!(await Request("OPTIONS", "/api/action", "https://evil.example")).Headers.Contains("Access-Control-Allow-Origin"), "no CORS for other websites");
    Check((await Request("POST", "/api/action", "https://dashboard.example", body: new { })).StatusCode == HttpStatusCode.Unauthorized && mutations == 0, "pairing required before actions");
    Check((await Request("POST", "/bridge/manage/code", "https://dashboard.example", body: new { })).StatusCode == HttpStatusCode.Forbidden, "website cannot approve itself");
    var code = Code();
    var connected = await Request("POST", "/bridge/connect", "https://dashboard.example", body: new { code });
    var token = (await connected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    Check(connected.IsSuccessStatusCode && token.Length == 64, "local approval pairs browser");
    Check((await Request("POST", "/bridge/connect", "https://dashboard.example", body: new { code })).StatusCode == HttpStatusCode.BadRequest, "code cannot be replayed");
    Check((await Request("POST", "/api/action", "https://dashboard.example", token, new { })).IsSuccessStatusCode && mutations == 1, "paired website can use API");
    Check((await Request("POST", "/api/action", "https://evil.example", token, new { })).StatusCode == HttpStatusCode.Forbidden && mutations == 1, "credential bound to website origin");
    Check(new WebsiteBridge(configuration, protection, clock).IsPaired("https://dashboard.example", token), "pairing survives helper restart");
    Check(!File.ReadAllText(Path.Combine(directory, "website-pairings.protected")).Contains(token), "credential not stored in plaintext");
    var expiring = Code(); clock.Advance(TimeSpan.FromMinutes(6));
    Check(bridge.Connect("https://dashboard.example", expiring) is null, "code expires");
    var locked = Code(); for (var i = 0; i < 5; i++) bridge.Connect("https://dashboard.example", "WRONG");
    Check(bridge.Connect("https://dashboard.example", locked) is null, "five failed attempts lock code");
    await Request("POST", "/bridge/disconnect", "https://dashboard.example", token);
    Check((await Request("GET", "/api/health", "https://dashboard.example", token)).StatusCode == HttpStatusCode.Unauthorized, "revocation takes effect immediately");
    Check(!new WebsiteBridge(configuration, protection, clock).IsPaired("https://dashboard.example", token), "revocation persists");
    using var crossSite = new HttpRequestMessage(HttpMethod.Post, "/api/action"); crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
    Check((await client.SendAsync(crossSite)).StatusCode == HttpStatusCode.Forbidden, "cross-site browser request without Origin denied");
    using var dnsRebinding = new HttpRequestMessage(HttpMethod.Get, "/api/health"); dnsRebinding.Headers.Host = "evil.example";
    Check((await client.SendAsync(dnsRebinding)).StatusCode == HttpStatusCode.Forbidden, "foreign Host rejected");
    Console.WriteLine($"{passed} website bridge checks passed.");
} finally { await app.StopAsync(); await app.DisposeAsync(); Directory.Delete(directory, true); }

sealed class TestClock : TimeProvider {
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}
