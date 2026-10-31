using System.Net;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Repair;

/// <summary>Bounded, non-executing HTML start-tag observations; script/style/comment contents are excluded.</summary>
public static class WebsiteMarkup
{
    public static IReadOnlyList<string> ApiLinks(string text) => Tags(text).Where(tag => tag.Name == "link" && tag.Attributes.GetValueOrDefault("rel") == "https://api.w.org/" && tag.Attributes.TryGetValue("href", out var href) && href != "\0")
        .Select(tag => tag.Attributes.GetValueOrDefault("href", "")).Take(4).ToArray();
    public static IReadOnlyList<string> SearchLinks(string text)
    {
        var links = new List<string>();
        foreach (var tag in Tags(text))
        {
            if (links.Count >= 8) break;
            if (tag.Name != "a" || !tag.Attributes.GetValueOrDefault("class", "").Contains("result__a", StringComparison.Ordinal)) continue;
            var href = tag.Attributes.GetValueOrDefault("href", "");
            // The released parser uses a temporary absolute URL only to inspect a
            // DuckDuckGo wrapper. A direct scheme-relative result remains untrusted.
            var parsedHref = href.StartsWith("//", StringComparison.Ordinal) ? "https:" + href : href;
            if (Uri.TryCreate(parsedHref, UriKind.Absolute, out var parsed) && parsed.IdnHost.EndsWith("duckduckgo.com", StringComparison.Ordinal))
            {
                href = "";
                foreach (var item in parsed.Query.TrimStart('?').Split('&'))
                {
                    var pair = item.Split('=', 2);
                    if (Uri.UnescapeDataString(pair[0].Replace('+', ' ')) == "uddg") { href = pair.Length == 2 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : ""; break; }
                }
            }
            try { var url = WebsiteAddress.PublicUrl(href); if (!links.Contains(url, StringComparer.Ordinal)) links.Add(url); }
            catch (Exception error) when (error is InvalidDataException or ArgumentException or IOException) { }
        }
        return links;
    }
    private sealed record Tag(string Name, Dictionary<string, string> Attributes);
    private static IEnumerable<Tag> Tags(string text)
    {
        if (text.Length > WebsiteObservationLimits.MaximumBody) text = text[..WebsiteObservationLimits.MaximumBody];
        var position = 0;
        while ((position = text.IndexOf('<', position)) >= 0)
        {
            if (text.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            { var stop = text.IndexOf("-->", position + 4, StringComparison.Ordinal); if (stop < 0) yield break; position = stop + 3; continue; }
            var cursor = position + 1;
            if (cursor >= text.Length) yield break;
            if (!char.IsAsciiLetter(text[cursor])) { position++; continue; }
            var start = cursor; while (cursor < text.Length && (char.IsAsciiLetterOrDigit(text[cursor]) || text[cursor] is ':' or '-' or '_')) cursor++;
            var name = text[start..cursor].ToLowerInvariant(); var attrs = new Dictionary<string, string>(StringComparer.Ordinal); var complete = false;
            while (cursor < text.Length)
            {
                while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                if (cursor >= text.Length) break;
                if (text[cursor] == '>') { cursor++; complete = true; break; }
                if (text[cursor] == '/' && cursor + 1 < text.Length && text[cursor + 1] == '>') { cursor += 2; complete = true; break; }
                start = cursor;
                while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] is not ('=' or '>' or '/')) cursor++;
                if (cursor == start) { cursor++; continue; }
                var key = text[start..cursor].ToLowerInvariant(); while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                var value = "\0";
                if (cursor < text.Length && text[cursor] == '=')
                {
                    cursor++; while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
                    if (cursor < text.Length && text[cursor] is '\'' or '"')
                    {
                        var quote = text[cursor++]; start = cursor; var end = text.IndexOf(quote, cursor); if (end < 0) break;
                        value = text[start..end]; cursor = end + 1;
                    }
                    else { start = cursor; while (cursor < text.Length && !char.IsWhiteSpace(text[cursor]) && text[cursor] != '>') cursor++; value = text[start..cursor]; }
                }
                attrs[key] = WebUtility.HtmlDecode(value);
            }
            if (!complete) yield break;
            position = cursor; yield return new(name, attrs);
            if (name is "script" or "style")
            { var end = text.IndexOf("</" + name, position, StringComparison.OrdinalIgnoreCase); if (end < 0) yield break; position = end; }
        }
    }
}
