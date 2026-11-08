using System.Text;

namespace AutoSourcarr.Core.Diagnostics;

/// <summary>
/// 0.5.3: a failure as one log line that says what went wrong and where: "Type: message (at Class.Method)" for the
/// error and each inner error (an AggregateException is listed per inner error). Callers record it through the support
/// log, which redacts secrets, addresses and private paths; <see cref="Types"/> is the variant without messages for the
/// console before that redaction exists.
/// </summary>
public static class ErrorText
{
    private const int MaxLength = 1500;

    public static string Of(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var text = new StringBuilder();
        foreach (var item in Chain(error))
        {
            if (text.Length > 0) text.Append(" <- ");
            text.Append(item.GetType().Name).Append(": ").Append(OneLine(item.Message));
            if (Where(item) is { } where) text.Append(" (at ").Append(where).Append(')');
            if (text.Length >= MaxLength) break;
        }
        return text.Length > MaxLength ? text.ToString(0, MaxLength) + "…" : text.ToString();
    }

    /// <summary>The error types only, outermost first ("AggregateException <- SqliteException").</summary>
    public static string Types(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return string.Join(" <- ", Chain(error).Select(item => item.GetType().Name));
    }

    private static IEnumerable<Exception> Chain(Exception error)
    {
        var seen = 0;
        var queue = new Queue<Exception>([error]);
        while (queue.Count > 0 && seen++ < 8)
        {
            var item = queue.Dequeue();
            yield return item;
            if (item is AggregateException aggregate) foreach (var inner in aggregate.InnerExceptions) queue.Enqueue(inner);
            else if (item.InnerException is { } inner) queue.Enqueue(inner);
        }
    }

    /// <summary>The first AutoSourcarr frame of the stack ("SqliteDatabase.Run"), else the first frame.</summary>
    private static string? Where(Exception error)
    {
        if (error.StackTrace is not { Length: > 0 } stack) return null;
        var frames = stack.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("at ", StringComparison.Ordinal)).ToList();
        var frame = frames.FirstOrDefault(line => line.Contains("AutoSourcarr.", StringComparison.Ordinal)) ?? frames.FirstOrDefault();
        if (frame is null) return null;
        var method = frame[3..];
        var cut = method.IndexOf('(', StringComparison.Ordinal);
        if (cut > 0) method = method[..cut];
        var parts = method.Split('.');
        return parts.Length >= 2 ? parts[^2] + "." + parts[^1] : method;
    }

    private static string OneLine(string message) => string.Join(' ', (message ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
}
