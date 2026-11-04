using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace StreamSidekick.Services;

// Only the exact configured website can pair. Approval happens on a local-only page;
// short-lived codes are single-use and never appear in URLs or server logs.
public sealed class WebsiteBridge
{
    private sealed record Pairing(string Id, string Origin, string TokenHash, DateTimeOffset CreatedAt);
    private readonly object _gate = new();
    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly TimeProvider _clock;
    private readonly List<Pairing> _pairings;
    private string? _code;
    private DateTimeOffset _codeExpiry;
    private int _attempts;
    public string? WebsiteOrigin { get; }
    public const string TokenHeader = "X-Handler-Pairing";

    public WebsiteBridge(IConfiguration config, IDataProtectionProvider protection, TimeProvider clock)
    {
        _clock = clock;
        var configured = config["WebsiteBridge:WebsiteOrigin"]?.Trim();
        if (!string.IsNullOrEmpty(configured))
        {
            if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.UserInfo.Length > 0)
                throw new InvalidOperationException("WebsiteBridge:WebsiteOrigin must be an HTTPS origin without a path.");
            WebsiteOrigin = uri.GetLeftPart(UriPartial.Authority);
        }
        _protector = protection.CreateProtector("StreamSidekick.WebsitePairings.v1");
        var directory = config["WebsiteBridge:DataDirectory"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSidekick");
        _path = Path.Combine(directory, "website-pairings.protected");
        if (File.Exists(_path))
        {
            // Fail closed rather than silently discarding saved trust on a corrupt file.
            _pairings = JsonSerializer.Deserialize<List<Pairing>>(_protector.Unprotect(File.ReadAllText(_path))) ?? [];
        }
        else _pairings = [];
    }

    public object CreateCode()
    {
        if (WebsiteOrigin is null) throw new InvalidOperationException("No website is configured in this helper build.");
        lock (_gate)
        {
            _code = Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
            _codeExpiry = _clock.GetUtcNow().AddMinutes(5);
            _attempts = 0;
            return new { code = $"{_code[..4]}-{_code[4..8]}-{_code[8..]}", expiresAt = _codeExpiry, websiteOrigin = WebsiteOrigin };
        }
    }

    public string? Connect(string origin, string? code)
    {
        lock (_gate)
        {
            if (origin != WebsiteOrigin || _code is null || _clock.GetUtcNow() >= _codeExpiry || _attempts >= 5) return null;
            _attempts++;
            var normalized = (code ?? "").Replace("-", "").Trim().ToUpperInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(_code), Encoding.UTF8.GetBytes(normalized))) return null;
            _code = null;
            if (_pairings.Count >= 20) throw new InvalidOperationException("Remove an old browser connection before pairing another.");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var pairing = new Pairing(Guid.NewGuid().ToString("N"), origin, Hash(token), _clock.GetUtcNow());
            _pairings.Add(pairing);
            try { Save(); }
            catch { _pairings.Remove(pairing); throw; }
            return token;
        }
    }

    public bool IsPaired(string origin, string? token)
    {
        if (origin != WebsiteOrigin || token is null || token.Length != 64) return false;
        var hash = Hash(token);
        lock (_gate) return _pairings.Any(p => p.Origin == origin && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(p.TokenHash)));
    }

    public object[] ListPairings()
    {
        lock (_gate) return _pairings.Select(p => (object)new { p.Id, p.Origin, p.CreatedAt }).ToArray();
    }

    public void Revoke(string id)
    {
        lock (_gate) { _pairings.RemoveAll(p => p.Id == id); Save(); }
    }

    public void Disconnect(string token)
    {
        lock (_gate) { _pairings.RemoveAll(p => p.TokenHash == Hash(token)); Save(); }
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, _protector.Protect(JsonSerializer.Serialize(_pairings)));
        File.Move(temporary, _path, true);
    }

    public static bool IsLocalBrowser(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (origin.Length > 0) return origin == $"{context.Request.Scheme}://{context.Request.Host}";
        return context.Request.Headers["Sec-Fetch-Site"] != "cross-site";
    }
}

public sealed record WebsitePairRequest(string Code);

public static class WebsiteBridgeEndpoints
{
    public static void UseWebsiteBridge(this WebApplication app)
    {
        var bridge = app.Services.GetRequiredService<WebsiteBridge>();
        app.Use(async (context, next) =>
        {
            // The helper is never a LAN or internet server, even if --urls is overridden.
            var ip = context.Connection.RemoteIpAddress;
            if (ip is null || !IPAddress.IsLoopback(ip) ||
                context.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/pair.html") || context.Request.Path.StartsWithSegments("/bridge"))
            {
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers.ContentSecurityPolicy = "frame-ancestors 'none'";
                context.Response.Headers.CacheControl = "no-store";
            }
            if (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/bridge"))
            { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (WebsiteBridge.IsLocalBrowser(context)) { await next(context); return; }
            var origin = context.Request.Headers.Origin.ToString();
            if (bridge.WebsiteOrigin is null || origin != bridge.WebsiteOrigin)
            { context.Response.StatusCode = 403; return; }
            // Local approval and revocation management are never accessible to the hosted UI.
            if (context.Request.Path.StartsWithSegments("/bridge/manage"))
            { context.Response.StatusCode = 403; return; }
            context.Response.Headers.AccessControlAllowOrigin = origin;
            context.Response.Headers.Vary = "Origin";
            if (HttpMethods.IsOptions(context.Request.Method))
            {
                context.Response.Headers.AccessControlAllowMethods = "GET, POST, PUT, DELETE, OPTIONS";
                context.Response.Headers.AccessControlAllowHeaders = $"Content-Type, {WebsiteBridge.TokenHeader}";
                context.Response.StatusCode = 204;
                return;
            }
            var publicEndpoint = context.Request.Path == "/bridge/status" && HttpMethods.IsGet(context.Request.Method)
                || context.Request.Path == "/bridge/connect" && HttpMethods.IsPost(context.Request.Method);
            if (!publicEndpoint && !bridge.IsPaired(origin, context.Request.Headers[WebsiteBridge.TokenHeader]))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "Pair this browser with your Windows helper." }); return; }
            await next(context);
        });
    }

    public static void MapWebsiteBridge(this WebApplication app)
    {
        app.MapGet("/bridge/status", () => new { protocol = 1, product = "John's Twitch Handler" });
        app.MapGet("/bridge/manage", (WebsiteBridge bridge) => new { bridge.WebsiteOrigin, pairings = bridge.ListPairings() });
        app.MapPost("/bridge/manage/code", (WebsiteBridge bridge) => Results.Ok(bridge.CreateCode()));
        app.MapDelete("/bridge/manage/{id}", (string id, WebsiteBridge bridge) => { bridge.Revoke(id); return Results.NoContent(); });
        app.MapPost("/bridge/connect", (HttpContext context, WebsitePairRequest request, WebsiteBridge bridge) =>
        {
            if (request.Code is null || request.Code.Length > 32) return Results.BadRequest(new { error = "Enter the code shown by your helper." });
            var token = bridge.Connect(context.Request.Headers.Origin.ToString(), request.Code);
            return token is null ? Results.BadRequest(new { error = "Code invalid, expired, or locked. Create a fresh code in your helper." })
                : Results.Ok(new { token, protocol = 1 });
        });
        app.MapPost("/bridge/disconnect", (HttpContext context, WebsiteBridge bridge) =>
        {
            bridge.Disconnect(context.Request.Headers[WebsiteBridge.TokenHeader].ToString());
            return Results.NoContent();
        });
    }
}
