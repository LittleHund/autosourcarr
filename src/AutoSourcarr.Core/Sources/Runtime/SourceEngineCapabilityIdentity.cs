using System.Net.Http.Headers;
using System.Text;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>An explicit configured engine identity, not a URL supplied by a capability request.</summary>
public sealed class SourceEngineCapabilityIdentity
{
    private readonly string _username;
    private readonly string _password;
    private SourceEngineCapabilityIdentity(string baseUrl, string username, string password)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https") || address.UserInfo.Length > 0
            || address.Query.Length > 0 || address.Fragment.Length > 0 || baseUrl.Contains('\\') || baseUrl.Any(c => c <= 32 || c == 127))
            throw new ArgumentException("Configure an HTTP(S) engine identity without embedded credentials, query or fragment");
        BaseUrl = baseUrl.TrimEnd('/'); Address = new Uri(BaseUrl + "/"); _username = username; _password = password;
    }
    public string BaseUrl { get; }
    public Uri Address { get; }
    public static SourceEngineCapabilityIdentity IntegratedSource() => new("http://127.0.0.1:4567", "", "");
    public static SourceEngineCapabilityIdentity ConfiguredExternal(string baseUrl, string username = "", string password = "") => new(baseUrl, username, password);
    internal AuthenticationHeaderValue? Authorization(SupportPrivacy privacy)
    {
        if (_username.Length == 0) return null;
        var header = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(_username + ":" + _password)));
        privacy.Register(new Dictionary<string, string?> { ["username"] = _username, ["password"] = _password, ["authorization"] = header.ToString() });
        return header;
    }
    internal bool SameOrigin(Uri target) => target.IsAbsoluteUri && target.Scheme == Address.Scheme
        && target.IdnHost.Equals(Address.IdnHost, StringComparison.OrdinalIgnoreCase) && target.Port == Address.Port && target.UserInfo.Length == 0;
    internal bool Matches(string baseUrl) => Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var value) && value == Address;
}
