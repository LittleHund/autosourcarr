using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair;

/// <summary>Literal base addresses from the exact corresponding maintainer file; arbitrary URL strings are not candidates.</summary>
public static class AddressResearchEvidence
{
    public const string Note = "Search leads are unverified. TLS/availability alone does not prove ownership; ambiguous moves require review.";
    private static readonly Regex Base = new("\\bbaseUrl\\s*(?::\\s*[A-Za-z0-9_?.<>]+\\s*)?=\\s*[\"'](https?://[^\"'\\s]+)[\"']", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Getter = new("\\bgetBaseUrl\\s*\\(\\s*\\)\\s*\\{[^{}]{0,1000}?\\breturn\\s*[\"'](https?://[^\"'\\s]+)[\"']\\s*;", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    public static IReadOnlyList<string> BaseAddresses(string text)
    {
        if (text.Length > WebsiteObservationLimits.MaximumBody) throw new InvalidDataException("Source address evidence exceeds its byte budget");
        var addresses = new List<string>();
        foreach (Match match in Base.Matches(text).Cast<Match>().Concat(Getter.Matches(text).Cast<Match>()))
        {
            try { var url = WebsiteAddress.PublicUrl(match.Groups[1].Value); if (!addresses.Contains(url, StringComparer.Ordinal)) addresses.Add(url); }
            catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { }
        }
        return addresses.Take(8).ToArray();
    }
}
