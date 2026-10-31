using System.Text.Json.Nodes;

namespace AutoSourcarr.Core.Sources;

public class SuwayomiException(string message) : Exception(message);
public sealed class ExtensionInstallUnavailableException(string message) : SuwayomiException(message);
public class SourceWorkDeferredException(string message) : Exception(message)
{
    public bool LocalSourceDeferral => true;
}
public sealed class SourceEngineUnavailableException(string message, int retryAfter = 5) : SourceWorkDeferredException(message)
{
    public int RetryAfter => retryAfter;
}
public sealed class SourceBudgetExceededException() : SourceWorkDeferredException("The local source-work budget expired; source availability remains unknown.");

public interface IManagedSourceRuntime
{
    bool BrowserMaintenance { get; }
    Task EnsureAvailableAsync(string requestPath, CancellationToken cancellationToken);
}

public interface ISourceAttention
{
    Task<JsonObject> ContextAsync(string baseUrl, JsonObject operation, CancellationToken cancellationToken);
    Task CheckAsync(JsonObject context, CancellationToken cancellationToken);
    Task FailedAsync(JsonObject context, JsonObject operation, Exception error, CancellationToken cancellationToken);
    Task CheckWebsiteAsync(string baseUrl, string host, CancellationToken cancellationToken);
    void Learn(JsonObject value);
}

public interface ISourceVerificationProbe
{
    TimeSpan RequestTimeout { get; }
    Task<JsonObject> RetryVerificationAsync(JsonObject operation, JsonObject context, CancellationToken cancellationToken = default);
}

