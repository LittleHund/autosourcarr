using System.Text.RegularExpressions;

namespace AutoSourcarr.Core.Connections;

/// <summary>
/// The connection types this build knows (CON-1), looked up by <see cref="IConnectionType.Type"/>. Built once at
/// start from every registered type; a duplicate or malformed type id or field key is a programming error and
/// stops the start instead of letting one type silently hide another.
/// </summary>
public sealed partial class ConnectionTypes
{
    private readonly Dictionary<string, IConnectionType> _byType = new(StringComparer.Ordinal);
    private readonly List<IConnectionType> _all = [];

    public ConnectionTypes(IEnumerable<IConnectionType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            ArgumentNullException.ThrowIfNull(type);
            if (type.Type is null || !TypeId().IsMatch(type.Type))
                throw new ArgumentException("Connection type ids are 2-32 lowercase letters, digits, hyphens or underscores: " + type.Type, nameof(types));
            if (!_byType.TryAdd(type.Type, type))
                throw new ArgumentException("Connection type registered twice: " + type.Type, nameof(types));
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in type.Fields ?? [])
            {
                if (field is null || field.Key is null || !FieldKey().IsMatch(field.Key))
                    throw new ArgumentException("Connection field keys are 1-48 letters, digits or underscores: " + type.Type + "." + field?.Key, nameof(types));
                if (!keys.Add(field.Key))
                    throw new ArgumentException("Connection field defined twice: " + type.Type + "." + field.Key, nameof(types));
            }
            _all.Add(type);
        }
    }

    /// <summary>Every registered type in registration order (the order the Connections menu shows).</summary>
    public IReadOnlyList<IConnectionType> All => _all;

    public IConnectionType? Find(string? type) => type is not null && _byType.TryGetValue(type, out var found) ? found : null;

    public bool Contains(string? type) => Find(type) is not null;

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{1,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypeId();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,47}\z", RegexOptions.CultureInvariant)]
    private static partial Regex FieldKey();
}
