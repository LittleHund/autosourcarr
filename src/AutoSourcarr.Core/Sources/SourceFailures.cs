using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Sources;

public sealed class HumanAssistanceRequiredException : SourceWorkDeferredException
{
    public string AttentionId { get; }
    public int RetryAfter { get; }
    public JsonObject SourceContext { get; }
    public HumanAssistanceRequiredException(JsonObject record, int retryAfter)
        : base($"{(SourceResponseContract.Text(record["name"]) is { Length: > 0 } name ? name : "Source")}: {SourceResponseContract.Text(record["reason"])}. Open Needs attention → WebView. Automatic retry is limited to once per hour.")
    {
        AttentionId = SourceResponseContract.Text(record["id"]);
        RetryAfter = retryAfter;
        SourceContext = new() { ["source_id"] = record["source_id"]?.DeepClone() ?? JsonValue.Create(""),
            ["source_name"] = record["name"]?.DeepClone() ?? JsonValue.Create("Source"), ["source_chapter_url"] = record["base_url"]?.DeepClone() ?? JsonValue.Create("") };
    }
}

public static class SourceFailures
{
    public static int? HttpStatus(Exception error) => HttpStatus(error.Message) ?? (error is HttpRequestException { StatusCode: { } status } ? (int)status : null);
    public static int? HttpStatus(string text)
    {
        var match = Regex.Match(text, @"(?:HTTP(?: error)?|status(?: code)?)\s*:?\s*([45]\d\d)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
    public static string AssistanceReason(Exception error)
    {
        if (error is SourceWorkDeferredException) return "";
        var text = error.Message.ToLowerInvariant();
        if (Has(text, "captcha_required", "/@waf/", "shape-selecting captcha", "waf challenge", "waf block", "web application firewall")) return "WAF verification needs your help";
        if (Has(text, "cloudflarebypassexception", "failed to bypass cloudflare", "cloudflare challenge", "cloudflare protection", "cf-chl-", "challenge-platform")) return "Cloudflare verification needs your help";
        return Has(text, "captcha", "human verification") ? "CAPTCHA needs your help" : "";
    }
    public static string Kind(Exception error) => Kind(error.Message, error is SourceWorkDeferredException, error is TimeoutException, HttpStatus(error));
    public static string Kind(string text) => Kind(text, false, false, HttpStatus(text));
    public static string Summary(Exception error, string sourceName = "Source") => error is HumanAssistanceRequiredException ? error.Message
        : SummaryText(error.Message, sourceName, Kind(error), HttpStatus(error));
    public static string Summary(string text, string sourceName = "Source") => SummaryText(text, sourceName, Kind(text), HttpStatus(text));
    public static string CompactImportReason(string reason, string sourceName = "Source") => reason.Length > 600 || StackTrace().IsMatch(reason) ? Summary(reason, sourceName) : reason;
    public static int ChapterRetryDelay(Exception error, string retryAfter = "", DateTimeOffset? now = null)
    {
        if (Kind(error) == "captcha") return 30;
        var delay = HttpStatus(error) switch { 403 or 521 or 522 or 523 or 524 => 30, 429 => 60, _ => 0 };
        if (delay == 0 || retryAfter.Length == 0) return delay;
        if (int.TryParse(retryAfter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)) return Math.Max(delay, seconds);
        if (DateTimeOffset.TryParse(retryAfter, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var deadline))
            return (int)Math.Min(int.MaxValue, Math.Max(delay, Math.Truncate((deadline - (now ?? DateTimeOffset.UtcNow)).TotalSeconds) + 1));
        return delay;
    }
    private static string Kind(string original, bool deferred, bool timeout, int? status)
    {
        var text = original.ToLowerInvariant();
        if (deferred || Has(text, "capacity busy", "request deferred before upstream")) return "deferred";
        if (Has(text, "captcha", "cloudflare challenge", "cloudflare protection", "cf-chl-", "challenge-platform", "cloudflarebypassexception", "failed to bypass cloudflare", "cloudflare bypass currently disabled", "human verification")) return "captcha";
        if (status == 429 || Has(text, "too many requests", "rate limit")) return "rate_limit";
        if (status == 403 || Has(text, "web application firewall", "waf block")) return "access_denied";
        if (status is 521 or 522 or 523 or 524) return "origin_unavailable";
        if (timeout || Has(text, "timeout", "timed out")) return "timeout";
        return "source_error";
    }
    private static string SummaryText(string text, string name, string kind, int? status)
    {
        name = Regex.Replace(string.IsNullOrEmpty(name) ? "Source" : name, @"\s+", " ").Trim();
        return kind switch
        {
            "deferred" => $"{name} search deferred by local capacity or a shared search time limit; retry is available.",
            "captcha" => $"{name} is blocked by a CAPTCHA; its lookup is deferred, not a 'no results' response.",
            "rate_limit" => $"{name} is rate-limited; its lookup is deferred.",
            "access_denied" => $"{name} denied access. Inspect this source in WebView; a WAF or access restriction may require manual action.",
            "origin_unavailable" => $"{name} website origin is unavailable (HTTP {status}); retry later or use another source.",
            "timeout" => $"{name} lookup timed out; other enabled sources can still be searched.",
            _ => $"{name} lookup failed: {ShortError(text)}"
        };
    }
    private static string ShortError(string text)
    {
        text = Regex.Replace(string.IsNullOrEmpty(text) ? "Source request failed" : text, @"\s+", " ").Trim();
        text = StackTrace().Split(text, 2)[0];
        return text[..Math.Min(280, text.Length)].TrimEnd();
    }
    private static Regex StackTrace() => new(@"\s+at\s+(?:[\w$]+\.)+[\w$<>]+\(", RegexOptions.CultureInvariant);
    private static bool Has(string text, params string[] values) => values.Any(value => text.Contains(value, StringComparison.Ordinal));
}
