using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Connections;

/// <summary>Kind of a connection setting; <see cref="Secret"/> values are encrypted in config.json and never returned (CON-5).</summary>
public enum ConnectionFieldKind { Text, Url, Secret, Integer, Boolean }

/// <summary>One setting of a connection type (shown in the Connections menu, CON-1).</summary>
public sealed record ConnectionField(string Key, string Label, ConnectionFieldKind Kind, bool Required, string? Default = null, string? Help = null);

/// <summary>A saved connection: its type, id, label, enabled switch and setting values (secrets already decrypted).</summary>
public sealed record ConnectionConfig(string Id, string Type, string Label, bool Enabled, IReadOnlyDictionary<string, string> Values)
{
    public string Value(string key, string fallback = "") => Values.TryGetValue(key, out var value) && value.Length > 0 ? value : fallback;
}

/// <summary>Result of the Connections menu's "Test" button: ok, a short plain message and optional details (version, account, limits).</summary>
public sealed record ConnectionTestResult(bool Ok, string Message, JsonObject? Details = null)
{
    public static ConnectionTestResult Pass(string message, JsonObject? details = null) => new(true, message, details);
    public static ConnectionTestResult Fail(string message, JsonObject? details = null) => new(false, message, details);
}

/// <summary>
/// One external service type (CON-1): FlareSolverr-compatible solvers, metadata providers, Komf, MalwareBazaar,
/// Suwayomi. Types are registered in <see cref="ConnectionTypes"/>; the store and routes are generic over them.
/// <see cref="TestAsync"/> must not change anything on the remote side, must honour cancellation and must be bounded
/// (the caller also applies a deadline). <paramref name="http"/> is a handler the caller owns (tests pass a stub).
/// </summary>
public interface IConnectionType
{
    /// <summary>Stable id in config.json, lowercase, e.g. "flaresolverr", "anilist".</summary>
    string Type { get; }
    string Label { get; }
    /// <summary>What the connection is for, one sentence for the menu.</summary>
    string Purpose { get; }
    /// <summary>Whether more than one connection of this type may exist (solvers: yes; Suwayomi main library: no).</summary>
    bool Multiple { get; }
    IReadOnlyList<ConnectionField> Fields { get; }
    Task<ConnectionTestResult> TestAsync(ConnectionConfig connection, HttpMessageHandler http, CancellationToken cancellationToken);
}
