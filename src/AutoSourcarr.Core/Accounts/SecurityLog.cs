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
/// Failed sign-ins slow down (AUTH-8): from the 5th failure in 15 minutes for one address or one name, each further
/// attempt waits 2, 4, 8 … up to 30 s; from the 20th the address or name is refused for the rest of the window.
/// A burst (10 failures within 5 minutes across all callers) is reported once per window.
/// </summary>
public sealed class LoginThrottle(Func<DateTimeOffset>? clock = null)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _all = [];
    private DateTimeOffset _burstReported = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    /// <summary>Delay before checking this attempt, or null when it is refused outright.</summary>
    public TimeSpan? Before(string address, string name)
    {
        lock (_gate)
        {
            var count = Math.Max(Count("a:" + address), Count("n:" + name.ToLowerInvariant()));
            if (count >= 20) return null;
            return count < 5 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, count - 4)));
        }
    }

    /// <summary>Records a failure; true when this failure starts a burst admins should hear about.</summary>
    public bool Failed(string address, string name)
    {
        var now = _clock();
        lock (_gate)
        {
            foreach (var key in new[] { "a:" + address, "n:" + name.ToLowerInvariant() })
            {
                if (!_failures.TryGetValue(key, out var list)) _failures[key] = list = [];
                list.Add(now);
            }
            _all.Add(now);
            _all.RemoveAll(at => now - at > TimeSpan.FromMinutes(5));
            if (_failures.Count > 10_000) Prune(now);
            if (_all.Count >= 10 && now - _burstReported > Window) { _burstReported = now; return true; }
            return false;
        }
    }

    public void Succeeded(string address, string name)
    {
        lock (_gate) { _failures.Remove("a:" + address); _failures.Remove("n:" + name.ToLowerInvariant()); }
    }

    private int Count(string key)
    {
        if (!_failures.TryGetValue(key, out var list)) return 0;
        var now = _clock();
        list.RemoveAll(at => now - at > Window);
        return list.Count;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var key in _failures.Keys.ToArray())
        {
            _failures[key].RemoveAll(at => now - at > Window);
            if (_failures[key].Count == 0) _failures.Remove(key);
        }
    }
}
