using System.Security.Cryptography;
using System.Text;
using AutoSourcarr.Api.Http;
using AutoSourcarr.Core.Accounts;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Security;

/// <summary>What the access check needs from the running app.</summary>
public sealed class AccessControlServices
{
    public required AccountStore Accounts { get; init; }
    public required SecurityLog Log { get; init; }
    public required Func<AutoSourcarrSettings> Settings { get; init; }
    public LoginThrottle Throttle { get; init; } = new();
    /// <summary>Adds a new secret (a regenerated admin token) to the redaction list.</summary>
    public Action<string>? RegisterSecret { get; init; }
    /// <summary>Called once per burst of failed sign-ins (admins are told: error registry now, notifications in SET-11).</summary>
    public Action<string>? Burst { get; init; }
}

/// <summary>The level a route needs (AUTH-6), decided in one place for every page, API call, live update and asset.</summary>
public enum AccessLevel { Public, Guest, Admin, Owner }

public static class AccessPolicy
{
    private static readonly string[] PublicPaths =
        ["/login", "/api/v1/live", "/api/v1/auth/status", "/api/v1/auth/login", "/api/v1/auth/setup", "/api/v1/auth/logout", "/favicon.ico",
            // The WebView WebSocket: a browser cannot send the admin token there; its one-time ticket from the
            // admin-only POST /api/v1/source-webview/session is checked by the route (SourceWebViewEndpoints).
            "/api/v1/source-webview/socket"];
    private static readonly string[] GuestReadPrefixes =
    [
        "/api/v1/sources", "/api/v1/browse/", "/api/v1/search", "/api/v1/entries/", "/api/v1/chapters/", "/api/v1/pages/",
        "/api/v1/covers/", "/api/v1/source-covers/", "/api/v1/source-icon/", "/api/v1/source-extension-icon/",
        "/api/v1/status/stats", "/api/v1/health"
    ];

    /// <summary>Entry reads that are matching work, not catalogue reads: the manual source search queries every source (phase 8).</summary>
    // The entry history (phase 10) is the matching decision log of one entry.
    private static readonly string[] AdminEntryReads = ["/manual-search", "/history"];

    /// <summary>Routing ignores case and a trailing slash, so the policy compares the same canonical form.</summary>
    public static string Canonical(string path)
    {
        var lower = path.ToLowerInvariant();
        return lower.Length > 1 ? lower.TrimEnd('/') : lower;
    }

    public static AccessLevel Required(string method, string path)
    {
        path = Canonical(path);
        if (PublicPaths.Contains(path) || path.StartsWith("/static/", StringComparison.Ordinal)) return AccessLevel.Public;
        // READ-1: Mihon adds the reader extension repository by URL without headers, so its files are public reads.
        // It is not the reader API: /api/reader/v1 keeps its key check (ReaderAuthorizationMiddleware).
        if (method is "GET" or "HEAD" && path.StartsWith("/api/reader/repo/", StringComparison.Ordinal)) return AccessLevel.Public;
        if (path == "/api/v1/auth/me" || path == "/api/v1/auth/password") return AccessLevel.Guest;
        if (path.StartsWith("/api/v1/accounts", StringComparison.Ordinal) || path == "/api/v1/keys/admin-token/regenerate") return AccessLevel.Owner;
        if (!path.StartsWith("/api/", StringComparison.Ordinal)) return AccessLevel.Guest;
        if (method is "GET" or "HEAD" && GuestReadPrefixes.Any(prefix => prefix.EndsWith('/') ? path.StartsWith(prefix, StringComparison.Ordinal) : path == prefix)
            && !AdminEntryReads.Any(suffix => path.StartsWith("/api/v1/entries/", StringComparison.Ordinal) && path.EndsWith(suffix, StringComparison.Ordinal)))
            return AccessLevel.Guest;
        return AccessLevel.Admin;
    }

    public static bool Allows(Caller caller, AccessLevel level) => level switch
    {
        AccessLevel.Public => true,
        // Reader devices read the catalogue too (the same reads the reader API serves).
        AccessLevel.Guest => caller.Role is Role.Owner or Role.Admin or Role.Guest or Role.Script or Role.Reader,
        AccessLevel.Admin => caller.AdminReach,
        AccessLevel.Owner => caller.Role == Role.Owner,
        _ => false
    };
}

/// <summary>
/// Works out who is calling (AUTH-1): admin token, API key, external sign-in headers, Basic, or the forms session
/// cookie; then applies <see cref="AccessPolicy"/>. The reader API (/api/reader/v1) keeps its own key check in
/// ReaderAuthorizationMiddleware and is not handled here.
/// </summary>
public sealed class AccessControlMiddleware(RequestDelegate next, AccessControlServices services)
{
    public const string CallerItem = "AutoSourcarr.Caller";
    public const string SessionCookie = "AutoSourcarr.Session";

    public async Task InvokeAsync(HttpContext context)
    {
        var path = AccessPolicy.Canonical(context.Request.Path.Value ?? "");
        if (context.Items.ContainsKey(ApiAuthorization.ReaderRequestItem)) { await next(context).ConfigureAwait(false); return; }
        var caller = await ResolveAsync(context).ConfigureAwait(false);
        context.Items[CallerItem] = caller;
        var level = AccessPolicy.Required(context.Request.Method, path);
        if (AccessPolicy.Allows(caller, level)) { await next(context).ConfigureAwait(false); return; }
        var settings = services.Settings();
        if (caller.Role != Role.None)
        {
            services.Log.Record("access-denied", caller.Name, Address(context), context.Request.Method + " " + path);
            await ApiJson.ProblemAsync(context, 403, "Your account (" + Roles.Name(caller.Role) + ") cannot use this.").ConfigureAwait(false);
            return;
        }
        if (settings.AuthMethod == "basic") context.Response.Headers.WWWAuthenticate = "Basic realm=\"AutoSourcarr\", charset=\"UTF-8\"";
        // The login page also creates the first owner, with Basic sign-in too (no pop-up account exists yet).
        if (!path.StartsWith("/api/", StringComparison.Ordinal) && HttpMethods.IsGet(context.Request.Method)
            && (settings.AuthMethod == "forms" || settings.AuthMethod == "basic" && services.Accounts.SetupRequired))
        {
            context.Response.Redirect("/login?next=" + Uri.EscapeDataString(path + context.Request.QueryString));
            return;
        }
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        var detail = settings.AuthMethod == "external" ? "Sign in through your sign-in portal." : "Sign in to AutoSourcarr.";
        await context.Response.WriteAsync("{\"detail\":\"" + detail + "\",\"login\":\"" + (settings.AuthMethod == "forms" ? "/login" : "") + "\"}").ConfigureAwait(false);
    }

    public static Caller CallerOf(HttpContext context) =>
        context.Items.TryGetValue(CallerItem, out var value) && value is Caller caller ? caller : Caller.Anonymous;

    /// <summary>The client address: the last X-Forwarded-For entry (the one the trusted proxy added) when the request comes from it.</summary>
    public static string Address(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "";
        if (context.Items.TryGetValue(TrustedProxyItem, out var trusted) && trusted is true)
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',')[^1].Trim();
            if (forwarded.Length is > 0 and < 64) return forwarded;
        }
        return remote;
    }

    public const string TrustedProxyItem = "AutoSourcarr.TrustedProxy";

    /// <summary>AUTH_TRUSTED_PROXIES (IPs or CIDR); empty trusts loopback and private networks only.</summary>
    public static bool FromTrustedProxy(System.Net.IPAddress? remote, string configured)
    {
        if (remote is null) return true; // in-process or Unix socket: no network peer at all
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var entries = configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length == 0)
        {
            if (System.Net.IPAddress.IsLoopback(remote)) return true;
            var bytes = remote.GetAddressBytes();
            return remote.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168
                : (bytes[0] & 0xfe) == 0xfc;
        }
        foreach (var entry in entries)
        {
            if (System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(remote)) return true;
            if (System.Net.IPAddress.TryParse(entry, out var single) && single.Equals(remote)) return true;
        }
        return false;
    }

    private async Task<Caller> ResolveAsync(HttpContext context)
    {
        var request = context.Request;
        var settings = services.Settings();
        var trusted = FromTrustedProxy(context.Connection.RemoteIpAddress, settings.AuthTrustedProxies);
        context.Items[TrustedProxyItem] = trusted;
        var bearer = request.Headers.Authorization.ToString();
        var token = request.Headers["X-Admin-Token"].ToString();
        if (bearer.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) token = token.Length > 0 ? token : bearer[7..].Trim();
        var apiKey = request.Headers["X-Api-Key"].ToString().Trim();
        var readerKey = request.Headers["X-AutoSourcarr-Api-Key"].ToString().Trim();
        // The strongest credential wins: admin token and keys before the reader token.
        foreach (var candidate in new[] { token.Trim(), apiKey, readerKey })
        {
            if (candidate.Length == 0) continue;
            if (Same(candidate, settings.AdminToken.Trim()) || Same(candidate, services.Accounts.AutomaticAdminToken))
                return new Caller(Role.Admin, "admin token", "admin-token");
            if (services.Accounts.FindKey(candidate) is { } key)
                return key.Kind switch
                {
                    "admin" => new Caller(Role.Admin, key.Name, "key", key.Id),
                    "script" => new Caller(Role.Script, key.Name, "key", key.Id, key.Role == Role.Admin),
                    _ => new Caller(Role.Reader, key.Name, "key", key.Id)
                };
        }
        // Proxy identity headers count only from a trusted proxy: anyone else could send them.
        if (readerKey.Length > 0 && Same(readerKey, settings.AutosourcarrReaderApiToken.Trim())) return new Caller(Role.Reader, "reader token", "reader-token");
        if (settings.AuthMethod == "external") return trusted ? External(request, settings) : Caller.Anonymous;
        if (settings.AuthMethod == "basic" && bearer.StartsWith("basic ", StringComparison.OrdinalIgnoreCase))
            return await BasicAsync(context, bearer[6..].Trim()).ConfigureAwait(false);
        if (settings.AuthMethod == "forms" && request.Cookies.TryGetValue(SessionCookie, out var session)
            && services.Accounts.ValidateSession(session) is { } valid)
        {
            if (valid.Renew) IssueCookie(context, services.Accounts.IssueSession(valid.Account.Id));
            return new Caller(valid.Account.Role, valid.Account.Name, "session", valid.Account.Id);
        }
        return Caller.Anonymous;
    }

    /// <summary>AUTH-2/3: the bound account is owner; provider groups map to admin or guest; groups never make an owner.</summary>
    private Caller External(HttpRequest request, AutoSourcarrSettings settings)
    {
        var header = settings.AutheliaUserHeader.Trim().Length > 0 ? settings.AutheliaUserHeader.Trim() : "Remote-User";
        var name = request.Headers[header].ToString().Split(',')[0].Trim();
        if (name.Length == 0) return Caller.Anonymous;
        var owner = services.Accounts.Accounts().FirstOrDefault(account => account.Role == Role.Owner);
        if (owner is { Source: "external" } && string.Equals(owner.Name, name, StringComparison.OrdinalIgnoreCase))
            return new Caller(Role.Owner, name, "external", owner.Id);
        var groups = request.Headers["Remote-Groups"].ToString().Split(',').Select(group => group.Trim()).Where(group => group.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        static IEnumerable<string> List(string value) => value.Split(',').Select(item => item.Trim()).Where(item => item.Length > 0);
        if (List(settings.AuthAdminGroups).Any(groups.Contains)) return new Caller(Role.Admin, name, "external");
        if (List(settings.AuthGuestGroups).Any(groups.Contains)) return new Caller(Role.Guest, name, "external");
        return new Caller(Role.None, name, "external");
    }

    // Basic sends the password with every request: a verified pair is remembered for a minute (by fingerprint only).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Caller Caller, DateTimeOffset Until, long Version)> _basic = new(StringComparer.Ordinal);

    private async Task<Caller> BasicAsync(HttpContext context, string encoded)
    {
        var fingerprint = PasswordHashing.Fingerprint("basic|" + encoded);
        // Any change to accounts (password, removal) drops the remembered pair.
        if (_basic.TryGetValue(fingerprint, out var cached) && cached.Until > DateTimeOffset.UtcNow && cached.Version == services.Accounts.Version) return cached.Caller;
        if (_basic.Count > 1000) _basic.Clear();
        string name, password;
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var split = text.IndexOf(':', StringComparison.Ordinal);
            if (split <= 0) return Caller.Anonymous;
            name = text[..split]; password = text[(split + 1)..];
        }
        catch (FormatException) { return Caller.Anonymous; }
        var account = await SignInAsync(context, name, password).ConfigureAwait(false);
        if (account is null) return Caller.Anonymous;
        var caller = new Caller(account.Role, account.Name, "basic", account.Id);
        _basic[fingerprint] = (caller, DateTimeOffset.UtcNow.AddMinutes(1), services.Accounts.Version);
        return caller;
    }

    /// <summary>Shared by forms and Basic: slow-down, check, security log. Null when refused.</summary>
    internal async Task<Account?> SignInAsync(HttpContext context, string name, string password) =>
        await SignInAsync(services, context, name, password).ConfigureAwait(false);

    internal static async Task<Account?> SignInAsync(AccessControlServices services, HttpContext context, string name, string password)
    {
        var address = Address(context);
        var wait = services.Throttle.Before(address, name);
        if (wait is null)
        {
            services.Log.Record("login-slowed", name, address, "refused: too many failed sign-ins in 15 minutes");
            return null;
        }
        if (wait > TimeSpan.Zero)
        {
            services.Log.Record("login-slowed", name, address, "waited " + wait.Value.TotalSeconds + " s");
            await Task.Delay(wait.Value, context.RequestAborted).ConfigureAwait(false);
        }
        var account = services.Accounts.VerifyPassword(name, password);
        if (account is null)
        {
            services.Log.Record("login-failed", name, address);
            if (services.Throttle.Failed()) services.Burst?.Invoke("Many failed sign-ins in the last minutes (latest from " + address + ").");
            return null;
        }
        services.Throttle.Succeeded(address, name);
        return account;
    }

    public static void IssueCookie(HttpContext context, string session) =>
        context.Response.Cookies.Append(SessionCookie, session, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = IsHttps(context), Path = "/",
            MaxAge = AccountStore.SessionLifetime, IsEssential = true
        });

    public static void ClearCookie(HttpContext context) =>
        context.Response.Cookies.Delete(SessionCookie, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict, Secure = IsHttps(context) });

    /// <summary>HTTPS directly, or reported by a trusted proxy that ends HTTPS (X-Forwarded-Proto).</summary>
    private static bool IsHttps(HttpContext context) => context.Request.IsHttps
        || context.Items.TryGetValue(TrustedProxyItem, out var trusted) && trusted is true
            && context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim().Equals("https", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string actual, string expected)
    {
        if (expected.Length == 0) return false;
        try { return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected)); }
        catch (EncoderFallbackException) { return false; }
    }
}
