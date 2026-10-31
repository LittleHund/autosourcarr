using System.Collections;
using System.Globalization;

namespace AutoSourcarr.Core.Persistence;

public sealed class DatabaseRow(IReadOnlyDictionary<string, object?> values) : IReadOnlyDictionary<string, object?>
{
    public object? this[string key] => values[key];
    public IEnumerable<string> Keys => values.Keys;
    public IEnumerable<object?> Values => values.Values;
    public int Count => values.Count;
    public bool ContainsKey(string key) => values.ContainsKey(key);
    public bool TryGetValue(string key, out object? value) => values.TryGetValue(key, out value);
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public string Text(string key, string fallback = "") => values.TryGetValue(key, out var value) && value is not null
        ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? fallback : fallback;
    public long Integer(string key, long fallback = 0) => values.TryGetValue(key, out var value) && value is not null
        ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : fallback;
    public double Number(string key, double fallback = 0) => values.TryGetValue(key, out var value) && value is not null
        ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : fallback;
    public bool Flag(string key) => Integer(key) != 0;
}
