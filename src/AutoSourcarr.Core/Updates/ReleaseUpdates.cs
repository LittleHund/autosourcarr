using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Updates;

/// <summary>One CHANGELOG section of a newer version.</summary>
public sealed record ReleaseNote(string Version, string Date, string Text);

/// <summary>
/// 0.5.4: tells the owner when a newer AutoSourcarr image is published. Versions come from the image registry (a tag
/// exists only after the owner approved its release), the changes from the CHANGELOG on the main branch. Nothing about
/// this instance is sent: the requests are an anonymous registry token, the tag list and, only when a new version
/// appears, the CHANGELOG file. The container cannot see which tag it was started with, so UPDATE_IMAGE_TAG says
/// whether the Compose file uses <c>latest</c> (restart to update) or a fixed version (change the tag).
/// </summary>
public sealed partial class ReleaseUpdates : IDisposable
{
    public const string Registry = "https://ghcr.io";
    public const string Repository = "littlehund/autosourcarr";
    public const string ChangelogUrl = "https://raw.githubusercontent.com/LittleHund/autosourcarr/main/CHANGELOG.md";
    private const int MaxTagBytes = 256 * 1024, MaxChangelogBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _current, _statePath;
    private readonly Lock _gate = new();
    private string _latest = "", _error = "", _changelogFor = "";
    private IReadOnlyList<ReleaseNote> _notes = [];
    private DateTimeOffset? _checkedAt;
    private string _dismissed;

    /// <param name="statePath">Small JSON file that remembers the dismissed version across restarts.</param>
    public ReleaseUpdates(string currentVersion, string statePath, HttpClient? http = null)
    {
        _current = currentVersion;
        _statePath = statePath;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3, AutomaticDecompression = System.Net.DecompressionMethods.All })
        { Timeout = TimeSpan.FromSeconds(20) };
        _dismissed = ReadDismissed(statePath);
    }

    /// <summary>Raised when a check finds a version that was not known before (for an event to the UI).</summary>
    public Action<string>? NewVersion { get; set; }

    public string Current => _current;

    /// <summary>One check: the registry's newest version tag and, when it changed, the CHANGELOG sections since this version.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        string latest;
        try
        {
            using var tokenResponse = await _http.GetAsync(Registry + "/token?scope=repository:" + Repository + ":pull", cancellationToken).ConfigureAwait(false);
            tokenResponse.EnsureSuccessStatusCode();
            var token = JsonNode.Parse(await ReadLimited(tokenResponse, 16 * 1024, cancellationToken).ConfigureAwait(false))?["token"]?.GetValue<string>()
                ?? throw new InvalidDataException("The registry gave no token.");
            using var request = new HttpRequestMessage(HttpMethod.Get, Registry + "/v2/" + Repository + "/tags/list?n=1000");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var tags = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            tags.EnsureSuccessStatusCode();
            latest = Newest(ParseTags(await ReadLimited(tags, MaxTagBytes, cancellationToken).ConfigureAwait(false))) ?? "";
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or InvalidOperationException
            && !cancellationToken.IsCancellationRequested)
        {
            lock (_gate) { _error = "The update check could not reach ghcr.io: " + error.Message; _checkedAt = DateTimeOffset.UtcNow; }
            return;
        }

        string previous, changelogFor;
        lock (_gate) { previous = _latest; changelogFor = _changelogFor; }
        var notes = (IReadOnlyList<ReleaseNote>?)null;
        var error2 = "";
        if (Newer(latest, _current) && changelogFor != latest)
        {
            try
            {
                using var response = await _http.GetAsync(ChangelogUrl, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                notes = ParseChangelog(await ReadLimited(response, MaxChangelogBytes, cancellationToken).ConfigureAwait(false), _current, latest);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException && !cancellationToken.IsCancellationRequested)
            {
                error2 = "The list of changes could not be read from GitHub: " + error.Message;
            }
        }
        lock (_gate)
        {
            _latest = latest;
            _checkedAt = DateTimeOffset.UtcNow;
            _error = error2;
            if (notes is not null) { _notes = notes; _changelogFor = latest; }
            if (!Newer(latest, _current)) { _notes = []; _changelogFor = ""; }
        }
        if (latest.Length > 0 && latest != previous && Newer(latest, _current)) NewVersion?.Invoke(latest);
    }

    /// <summary>Runs checks until <paramref name="stop"/>; the interval and on/off are read before every check.</summary>
    public async Task RunAsync(Func<(bool Enabled, int Minutes)> options, Action<Exception> report, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var (enabled, minutes) = options();
            if (enabled)
            {
                try { await CheckAsync(stop).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                catch (Exception error) when (error is not OutOfMemoryException) { report(error); }
            }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 7 * 24 * 60)), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Hides the notice for this version; a later version shows it again.</summary>
    public void Dismiss(string version)
    {
        lock (_gate)
        {
            _dismissed = version;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var temp = _statePath + ".tmp";
                File.WriteAllText(temp, new JsonObject { ["dismissed"] = version }.ToJsonString());
                File.Move(temp, _statePath, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { _error = "The dismissed version could not be saved: " + error.Message; }
        }
    }

    /// <summary>The state for the UI. <paramref name="imageTag"/> is UPDATE_IMAGE_TAG: auto, latest or pinned.</summary>
    public JsonObject Json(bool enabled, string imageTag)
    {
        lock (_gate)
        {
            var available = enabled && Newer(_latest, _current);
            var mode = imageTag is "latest" or "pinned" ? imageTag : "auto";
            return new JsonObject
            {
                ["enabled"] = enabled,
                ["current"] = _current,
                ["latest"] = _latest.Length > 0 ? _latest : null,
                ["available"] = available,
                ["dismissed"] = available && _dismissed == _latest,
                ["image_tag"] = mode,
                ["instructions"] = available ? new JsonArray(Instructions(_latest, mode).Select(line => (JsonNode?)line).ToArray()) : new JsonArray(),
                ["notes"] = available ? new JsonArray(_notes.Select(note => (JsonNode?)new JsonObject { ["version"] = note.Version, ["date"] = note.Date, ["text"] = note.Text }).ToArray()) : new JsonArray(),
                ["changes_url"] = "https://github.com/LittleHund/autosourcarr/blob/main/CHANGELOG.md",
                ["checked_at"] = _checkedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["error"] = _error.Length > 0 ? _error : null,
            };
        }
    }

    /// <summary>What to do, by how the Compose file names the image.</summary>
    public static IReadOnlyList<string> Instructions(string latest, string mode)
    {
        var restart = "If your Compose file uses ghcr.io/littlehund/autosourcarr:latest: restart AutoSourcarr so it pulls the new image (on TrueNAS: Edit → Save, or Pull images).";
        var pinned = "If your Compose file names a version: change the image to ghcr.io/littlehund/autosourcarr:" + latest + " and redeploy.";
        return mode switch { "latest" => [restart], "pinned" => [pinned], _ => [restart, pinned] };
    }

    /// <summary>Release tags only ("0.5.3"), from a registry tag list.</summary>
    public static IReadOnlyList<string> ParseTags(string json)
    {
        if (JsonNode.Parse(json)?["tags"] is not JsonArray tags) return [];
        return tags.Select(tag => tag is JsonValue value && value.TryGetValue<string>(out var text) ? text : "")
            .Where(tag => ReleaseTag().IsMatch(tag)).ToArray();
    }

    public static string? Newest(IEnumerable<string> versions) =>
        versions.Select(text => (Text: text, Version: Version.TryParse(text, out var parsed) ? parsed : null))
            .Where(item => item.Version is not null).OrderByDescending(item => item.Version).Select(item => item.Text).FirstOrDefault();

    /// <summary>True when <paramref name="candidate"/> is a release newer than <paramref name="current"/>.</summary>
    public static bool Newer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    /// <summary>The CHANGELOG sections "## X.Y.Z - date" above <paramref name="current"/> up to <paramref name="latest"/>, newest first.</summary>
    public static IReadOnlyList<ReleaseNote> ParseChangelog(string changelog, string current, string latest)
    {
        var notes = new List<ReleaseNote>();
        string? version = null, date = null;
        var body = new StringBuilder();
        void Flush()
        {
            if (version is not null && Newer(version, current) && !Newer(version, latest)) notes.Add(new(version, date ?? "", body.ToString().Trim()));
            body.Clear();
        }
        foreach (var line in changelog.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var heading = SectionHeading().Match(line);
            if (heading.Success) { Flush(); version = heading.Groups[1].Value; date = heading.Groups[2].Value; continue; }
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("# ", StringComparison.Ordinal)) { Flush(); version = null; continue; }
            if (version is not null) body.Append(line).Append('\n');
        }
        Flush();
        return notes.OrderByDescending(note => Version.Parse(note.Version)).ToArray();
    }

    private static string ReadDismissed(string path)
    {
        try { return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path))?["dismissed"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }

    private static async Task<string> ReadLimited(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("The response is larger than " + limit.ToString(CultureInfo.InvariantCulture) + " bytes.");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [GeneratedRegex(@"^\d{1,4}\.\d{1,4}\.\d{1,6}$")]
    private static partial Regex ReleaseTag();

    [GeneratedRegex(@"^## (\d{1,4}\.\d{1,4}\.\d{1,6})(?: - (\S+))?\s*$")]
    private static partial Regex SectionHeading();

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
