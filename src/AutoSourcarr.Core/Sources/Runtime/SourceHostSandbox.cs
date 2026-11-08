using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources.Runtime;

/// <summary>
/// Phase 13 (SRC-5, ARC-8) contract of <c>native/source-host-sandbox</c>: the launcher the app starts the source host
/// JVM through. <see cref="LauncherPath"/> is AUTOSOURCARR_SOURCE_HOST_SANDBOX (default
/// <c>/opt/autosourcarr-native/source-host-sandbox</c>); <see cref="UserId"/>/<see cref="GroupId"/> are
/// AUTOSOURCARR_SOURCE_HOST_UID/GID (the low-rights user, 569:569 in the image; used only when the launcher can switch);
/// <see cref="ReadWrite"/>/<see cref="ReadOnly"/> are extra paths (AUTOSOURCARR_SOURCE_HOST_RW / _RO, colon-separated).
/// <see cref="Signals"/> (0.5.3) is AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE: "auto" (default: only when the kernel has the
/// Landlock signal fix, erratum 2), "on" or "off".
/// </summary>
public sealed record SourceHostSandboxOptions(string LauncherPath, uint? UserId = null, uint? GroupId = null,
    IReadOnlyList<string>? ReadWrite = null, IReadOnlyList<string>? ReadOnly = null, string Signals = "auto")
{
    public const string DefaultLauncher = "/opt/autosourcarr-native/source-host-sandbox";

    public static SourceHostSandboxOptions FromEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        static uint? Id(string? text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 0x80000000 ? value : null;
        static string[] Paths(string? text) => (text ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => path.StartsWith('/') && !path.Contains("/../", StringComparison.Ordinal)).Take(16).ToArray();
        var launcher = environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_SANDBOX") is { Length: > 0 } configured ? configured : DefaultLauncher;
        var signals = environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE")?.Trim().ToLowerInvariant() switch
        {
            "on" or "true" => "on",
            "off" or "false" => "off",
            _ => "auto",
        };
        return new(launcher, Id(environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_UID")), Id(environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_GID")),
            Paths(environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_RW")), Paths(environment.GetValueOrDefault("AUTOSOURCARR_SOURCE_HOST_RO")), signals);
    }

    /// <summary>The launcher's arguments before "--" and the host command.</summary>
    public IReadOnlyList<string> Arguments(string dataDirectory, string java, string jar)
    {
        var arguments = new List<string>();
        if (Signals != "auto") arguments.AddRange(["--signals", Signals]);
        if (UserId is { } uid && GroupId is { } gid)
        {
            arguments.AddRange(["--uid", uid.ToString(CultureInfo.InvariantCulture), "--gid", gid.ToString(CultureInfo.InvariantCulture)]);
        }
        arguments.AddRange(["--data", dataDirectory]);
        foreach (var path in ReadWrite ?? []) arguments.AddRange(["--rw", path]);
        // The JVM and the jar may live outside /usr and /opt (development): read-only, with execute.
        foreach (var path in new[] { Parent(Parent(java)), Parent(jar) }.Concat(ReadOnly ?? []).Where(path => path.Length > 1).Distinct(StringComparer.Ordinal))
            arguments.AddRange(["--ro", path]);
        arguments.Add("--");
        return arguments;
    }

    /// <summary>The parent of an absolute Linux path ("" for a relative one or the root); Linux paths on every platform.</summary>
    private static string Parent(string path)
    {
        if (!path.StartsWith('/')) return "";
        var slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "" : path[..slash];
    }
}

/// <summary>What the source host runs under, from the launcher's first stderr line (or why there is no sandbox).</summary>
/// <param name="Signals">0.5.3, from the proof: "scoped", "off", "kernel-erratum" (the kernel lacks the Landlock signal fix)
/// or "unavailable"; "" from an older launcher.</param>
public sealed record SourceHostSandboxState(string State, int LandlockAbi, bool SignalsScoped, bool SeparateUser, uint? UserId, string Reason, string Signals = "")
{
    public const string ProofPrefix = "AUTOSOURCARR_SOURCE_SANDBOX_V1 ";
    public static SourceHostSandboxState Unknown { get; } = new("unknown", 0, false, false, null, "not-started");

    public bool Sandboxed => State == "sandboxed";

    /// <summary>Parses the launcher's proof line; null when the line is not one.</summary>
    public static SourceHostSandboxState? Parse(string line)
    {
        if (!line.StartsWith(ProofPrefix, StringComparison.Ordinal) || line.Length > 1024) return null;
        try
        {
            if (JsonNode.Parse(line[ProofPrefix.Length..]) is not JsonObject proof) return null;
            static long Number(JsonObject value, string name) => value[name] is JsonValue field && field.TryGetValue<long>(out var result) ? result : 0;
            var abi = (int)Math.Clamp(Number(proof, "landlock"), 0, 1024);
            var scoped = (Number(proof, "scoped") & 2) != 0;
            var reason = proof["reason"] is JsonValue text && text.TryGetValue<string>(out var value) ? value : "";
            var signals = proof["signals"] is JsonValue signalText && signalText.TryGetValue<string>(out var signalValue) && signalValue.Length <= 32 ? signalValue : "";
            var state = abi >= 1 ? "sandboxed" : "unsandboxed";
            if (abi < 1 && reason.Length == 0) reason = "landlock-unavailable";
            return new(state, abi, scoped, Number(proof, "separated") == 1, (uint)Math.Clamp(Number(proof, "uid"), 0, uint.MaxValue), reason, signals);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>The health line (Status → internal services).</summary>
    public string Message()
    {
        var user = SeparateUser ? "as its own low-rights user (uid " + UserId?.ToString(CultureInfo.InvariantCulture) + ")"
            : "as the app's user" + (Reason == "identity-unavailable" ? " (a separate user needs CAP_SETUID/CAP_SETGID, which this container does not grant)" : "");
        return State switch
        {
            "sandboxed" => "The source host and its WebView run in the Landlock sandbox (ABI " + LandlockAbi.ToString(CultureInfo.InvariantCulture) + "): it can write only its own data folder and cannot read the app's configuration, databases or library files"
                + (SignalsScoped ? ", and it cannot signal the app" : Signals switch
                {
                    "kernel-erratum" => "; signal scoping is off because this kernel's Landlock has a signal bug that crashes the WebView (fixed in Linux 6.15)",
                    "off" => "; signal scoping is off (AUTOSOURCARR_SOURCE_HOST_SIGNAL_SCOPE=off)",
                    _ => "; signal scoping needs Landlock ABI 6",
                })
                + ". Not covered: the network is open (sources browse the web, and the host can reach the app's HTTP port, which is why identity headers are never trusted from this container), /dev/shm is shared; it runs " + user + ".",
            "unsandboxed" => "The source host runs WITHOUT a sandbox (" + Describe(Reason) + "); it runs " + user + ". Sources still work.",
            _ => "The source host sandbox is not known yet (the host has not started).",
        };
    }

    private static string Describe(string reason) => reason switch
    {
        "sandbox-missing" => "the source-host-sandbox launcher is not installed",
        "landlock-unavailable" => "the kernel has no Landlock",
        "landlock-rules-failed" => "the Landlock rules could not be applied",
        "platform" => "sandboxing needs Linux",
        "no-proof" => "the launcher did not report its restrictions",
        _ => reason,
    };

    public JsonObject Json() => new()
    {
        ["state"] = State, ["landlock_abi"] = LandlockAbi, ["signals_scoped"] = SignalsScoped, ["separate_user"] = SeparateUser,
        ["uid"] = UserId, ["reason"] = Reason, ["signals"] = Signals, ["message"] = Message(),
    };
}
