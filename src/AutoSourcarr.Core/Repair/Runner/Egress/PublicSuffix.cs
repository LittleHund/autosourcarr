namespace AutoSourcarr.Core.Repair.Runner.Egress;

/// <summary>
/// Phase 13 cookie-domain check for the egress proxy. A cookie whose Domain attribute is a public suffix (a top-level
/// domain, "co.uk", "github.io", ...) would be sent to every site below it, so it is refused; so is a Domain the request
/// host does not domain-match. The list is a compact built-in subset of the Public Suffix List (every single label, the
/// common second-level registries and shared hosting suffixes), not the full list: a rarer suffix is treated as an
/// ordinary domain (recorded as a limitation).
/// </summary>
public static class PublicSuffix
{
    private static readonly HashSet<string> MultiLabel = new(StringComparer.Ordinal)
    {
        // Second-level registries.
        "co.uk", "org.uk", "me.uk", "ac.uk", "gov.uk", "ltd.uk", "plc.uk", "net.uk", "sch.uk", "nhs.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "id.au", "asn.au",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp", "gr.jp", "ad.jp", "ed.jp", "lg.jp",
        "co.kr", "or.kr", "ne.kr", "re.kr", "go.kr", "ac.kr",
        "com.cn", "net.cn", "org.cn", "gov.cn", "edu.cn", "com.tw", "net.tw", "org.tw", "idv.tw", "com.hk", "net.hk", "org.hk",
        "com.sg", "net.sg", "org.sg", "com.my", "net.my", "org.my", "com.ph", "net.ph", "com.vn", "net.vn", "co.th", "in.th", "or.th",
        "co.id", "or.id", "web.id", "my.id", "co.in", "net.in", "org.in", "firm.in", "gen.in", "ind.in", "co.nz", "net.nz", "org.nz",
        "com.br", "net.br", "org.br", "com.ar", "net.ar", "org.ar", "com.mx", "net.mx", "org.mx", "com.co", "net.co", "com.pe", "com.ve",
        "co.za", "org.za", "net.za", "com.ng", "co.ke", "com.eg", "com.tr", "net.tr", "org.tr", "com.ru", "net.ru", "org.ru", "com.ua",
        "net.ua", "org.ua", "co.il", "org.il", "com.pl", "net.pl", "org.pl", "com.es", "org.es", "co.at", "or.at", "com.pt", "com.gr",
        "com.sa", "com.pk", "com.bd", "com.np", "com.lk", "co.ve", "eu.org", "us.com", "uk.com", "eu.com", "co.com",
        // Shared hosting (private section of the list): every customer is its own site.
        "github.io", "gitlab.io", "bitbucket.io", "codeberg.page", "pages.dev", "workers.dev", "netlify.app", "vercel.app", "web.app",
        "firebaseapp.com", "appspot.com", "herokuapp.com", "blogspot.com", "wordpress.com", "tumblr.com", "azurewebsites.net",
        "cloudfront.net", "cloudapp.net", "s3.amazonaws.com", "elasticbeanstalk.com", "fly.dev", "onrender.com", "glitch.me",
        "repl.co", "ngrok.io", "ngrok-free.app", "duckdns.org", "no-ip.org", "dyndns.org", "myshopify.com", "wixsite.com",
        "weebly.com", "neocities.org", "surge.sh", "translate.goog", "r2.dev", "b-cdn.net", "readthedocs.io",
    };

    /// <summary>Whether <paramref name="domain"/> (normalized ASCII, no leading dot) is a public suffix.</summary>
    public static bool IsPublicSuffix(string domain)
    {
        var value = Normalize(domain);
        if (value.Length == 0) return true;
        return !value.Contains('.', StringComparison.Ordinal) || MultiLabel.Contains(value);
    }

    /// <summary>
    /// RFC 6265 §5.3 steps 5–6 with a public-suffix check: a Domain attribute is allowed only when the request host
    /// domain-matches it and it is not a public suffix (unless it is the host itself, which then becomes host-only).
    /// An empty Domain (host-only cookie) is always allowed.
    /// </summary>
    public static bool CookieDomainAllowed(string requestHost, string? domainAttribute)
    {
        var host = Normalize(requestHost);
        if (domainAttribute is null) return true; // no Domain attribute: a host-only cookie
        var domain = Normalize(domainAttribute);
        // A Domain attribute that is present but empty, quoted or not a host name is refused, not taken as host-only.
        if (domain.Length == 0) return false;
        if (host.Length == 0) return false;
        var matches = host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);
        if (!matches) return false;
        return host == domain || !IsPublicSuffix(domain);
    }

    /// <summary>The Domain attribute of one Set-Cookie value (last one wins, as in RFC 6265), or null.</summary>
    public static string? CookieDomain(string setCookie)
    {
        string? found = null;
        foreach (var part in setCookie.Split(';').Skip(1))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals < 0) continue;
            if (part[..equals].Trim().Equals("Domain", StringComparison.OrdinalIgnoreCase)) found = part[(equals + 1)..].Trim();
        }
        return found;
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().TrimStart('.').TrimEnd('.');
        return text.Length == 0 ? "" : EgressProxy.NormalizeHost(text) ?? "";
    }
}
