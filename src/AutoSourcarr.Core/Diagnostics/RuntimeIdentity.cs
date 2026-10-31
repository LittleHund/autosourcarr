using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Privacy;

namespace AutoSourcarr.Core.Diagnostics;

public sealed record RuntimeIdentity(string Version, string InstanceId, string StartedAt)
{
    public static RuntimeIdentity Create(string version) => new(version, Guid.NewGuid().ToString("N")[..12], DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
}

public static class DiagnosticContext
{
    public static JsonArray ExceptionChain(Exception? error, SupportPrivacy privacy)
    {
        var chain = new JsonArray(); var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        while (error is not null && chain.Count < 6 && seen.Add(error))
        {
            var frames = new StackTrace(error, fNeedFileInfo: true).GetFrames().Reverse().ToArray();
            chain.Add(new JsonObject { ["type"] = error.GetType().Name, ["message"] = privacy.Text(error.Message.Length > 0 ? error.Message : error.GetType().Name),
                ["frames"] = new JsonArray(frames.TakeLast(24).Select(frame => (JsonNode?)new JsonObject { ["file"] = privacy.Text(frame.GetFileName() ?? ""),
                    ["line"] = frame.GetFileLineNumber(), ["function"] = frame.GetMethod()?.Name ?? "" }).ToArray()), ["frames_omitted"] = Math.Max(0, frames.Length - 24) });
            error = error.InnerException;
        }
        return privacy.Scrub(chain)!.AsArray();
    }
}
