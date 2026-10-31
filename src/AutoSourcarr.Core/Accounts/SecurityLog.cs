using System.Text;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Accounts;

/// <summary>
/// AUTH-8 security view: sign-ins, failed sign-ins, slow-downs, key, token and owner events. Kept in
/// <c>data/security-events.jsonl</c> (the newest 5,000), newest first when read, filterable by kind. No passwords,
/// key secrets or tokens are ever written; names and addresses only.
/// </summary>
public sealed class SecurityLog
{
    public const int Keep = 5000;
    public static readonly IReadOnlyList<string> Kinds =
    [
        "login", "login-failed", "login-slowed", "logout", "owner-created", "owner-reset", "user-created", "user-deleted",
        "password-changed", "key-created", "key-revoked", "admin-token-regenerated", "access-denied"
    ];

    private readonly string _path;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly List<JsonObject> _events;

    public SecurityLog(string dataDirectory, Func<DateTimeOffset>? clock = null)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "security-events.jsonl");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _events = File.Exists(_path)
            ? File.ReadLines(_path).TakeLast(Keep).Select(line => { try { return JsonNode.Parse(line) as JsonObject; } catch (System.Text.Json.JsonException) { return null; } })
                .OfType<JsonObject>().ToList()
            : [];
    }

    /// <summary>Raised for each new event (the event stream pushes it to open pages).</summary>
    public event Action<JsonObject>? Recorded;

    public void Record(string kind, string user, string address, string detail = "")
    {
        if (!Kinds.Contains(kind)) throw new ArgumentException("Unknown security event kind " + kind, nameof(kind));
        var row = new JsonObject
        {
            ["at"] = _clock().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = kind, ["user"] = Clip(user, 64), ["address"] = Clip(address, 64), ["detail"] = Clip(detail, 300)
        };
        lock (_gate)
        {
            _events.Add(row);
            if (_events.Count > Keep * 2)
            {
                _events.RemoveRange(0, _events.Count - Keep);
                File.WriteAllLines(_path, _events.Select(item => item.ToJsonString()), new UTF8Encoding(false));
            }
            else File.AppendAllText(_path, row.ToJsonString() + "\n", new UTF8Encoding(false));
        }
        Recorded?.Invoke((JsonObject)row.DeepClone());
    }

    public JsonObject Snapshot(string? kind = null, int limit = 200)
    {
        limit = Math.Clamp(limit, 1, 1000);
        JsonObject[] rows;
        lock (_gate)
            rows = _events.AsEnumerable().Reverse().Where(row => string.IsNullOrEmpty(kind) || row["kind"]?.GetValue<string>() == kind)
                .Take(limit).Select(row => (JsonObject)row.DeepClone()).ToArray();
        return new JsonObject
        {
            ["items"] = new JsonArray(rows.Cast<JsonNode>().ToArray()),
            ["kinds"] = new JsonArray(Kinds.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray())
        };
    }

    private static string Clip(string? value, int length)
    {
        value = new string((value ?? "").Where(character => !char.IsControl(character)).ToArray());
        return value.Length > length ? value[..length] : value;
    }
}

/// <summary>
/// Failed sign-ins slow down (AUTH-8). Every attempt is counted before the password is checked (so parallel
/// attempts cannot skip the delay) and a successful sign-in clears it. From the 5th attempt in 15 minutes for one
/// address or one name, each further attempt waits 2, 4, 8 … up to 30 s. Only the pair (name, address) is refused
/// outright after 20 attempts, so nobody can lock the owner out from elsewhere. Names are trimmed and compared
/// without case. A burst (10 failures within 5 minutes across all callers) is reported once per window.
/// </summary>
public sealed class LoginThrottle(Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, List<DateTimeOffset>> _attempts = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _failures = [];
    private DateTimeOffset _burstReported = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public static string NameKey(string? name) => (name ?? "").Trim().ToLowerInvariant();

    /// <summary>Counts this attempt and returns the delay before checking it, or null when it is refused outright.</summary>
    public TimeSpan? Before(string address, string name)
    {
        var now = _clock();
        var key = NameKey(name);
        lock (_gate)
        {
            if (Count("p:" + key + "|" + address) >= 20) return null;
            var count = Math.Max(Count("a:" + address), Count("n:" + key));
            foreach (var bucket in new[] { "a:" + address, "n:" + key, "p:" + key + "|" + address })
            {
                if (!_attempts.TryGetValue(bucket, out var list)) _attempts[bucket] = list = [];
                list.Add(now);
            }
            if (_attempts.Count > 10_000) Prune(now);
            return count < 4 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, count - 3)));
        }
    }

    /// <summary>The attempt failed; true when this failure starts a burst admins should hear about.</summary>
    public bool Failed()
    {
        var now = _clock();
        lock (_gate)
        {
            _failures.Add(now);
            _failures.RemoveAll(at => now - at > TimeSpan.FromMinutes(5));
            if (_failures.Count >= 10 && now - _burstReported > Window) { _burstReported = now; return true; }
            return false;
        }
    }

    public void Succeeded(string address, string name)
    {
        var key = NameKey(name);
        lock (_gate) { _attempts.Remove("a:" + address); _attempts.Remove("n:" + key); _attempts.Remove("p:" + key + "|" + address); }
    }

    private int Count(string key)
    {
        if (!_attempts.TryGetValue(key, out var list)) return 0;
        var now = _clock();
        list.RemoveAll(at => now - at > Window);
        return list.Count;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _attempts.Keys.ToArray())
        {
            _attempts[key].RemoveAll(at => now - at > Window);
            if (_attempts[key].Count == 0) _attempts.Remove(key);
        }
    }
}
