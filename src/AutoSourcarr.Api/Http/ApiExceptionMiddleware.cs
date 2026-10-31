using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Repair;
using AutoSourcarr.Core.Concurrency;
using Microsoft.AspNetCore.Http;

namespace AutoSourcarr.Api.Http;

/// <summary>An exact API response failure whose physical cleanup also failed; never unwrap unrelated aggregates.</summary>
internal sealed class ApiCleanupFailure : AggregateException
{
    public Exception? OperationFailure { get; }
    public Exception CleanupFailure { get; }
    public Exception PrimaryForResponse { get; }
    public ApiCleanupFailure(Exception? operationFailure, Exception cleanupFailure, Exception? responseFailure = null)
        : base("API response cleanup failed", Errors(operationFailure, cleanupFailure, responseFailure))
    {
        OperationFailure = operationFailure; CleanupFailure = cleanupFailure;
        PrimaryForResponse = responseFailure ?? operationFailure ?? cleanupFailure;
    }
    private static IEnumerable<Exception> Errors(Exception? operationFailure, Exception cleanupFailure, Exception? responseFailure)
    {
        // Keep the safe response classification AND the actual operation exception.
        if (responseFailure is not null && !ReferenceEquals(responseFailure, operationFailure)) yield return responseFailure;
        if (operationFailure is not null) yield return operationFailure;
        yield return cleanupFailure;
    }
}

public sealed class ApiExceptionMiddleware(RequestDelegate next, Func<HttpContext, Exception, string> recordUnexpected)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context).ConfigureAwait(false); }
        catch (ApiCleanupFailure failure)
        {
            string identifier;
            try { identifier = recordUnexpected(context, failure); }
            catch (Exception recordingFailure)
            { throw new AggregateException("API failure recording also failed", failure, recordingFailure); }
            if (context.Response.HasStarted || context.RequestAborted.IsCancellationRequested) throw;
            await WriteFailureAsync(context, failure.PrimaryForResponse, identifier).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error) when (!context.Response.HasStarted)
        { await WriteFailureAsync(context, error).ConfigureAwait(false); }
    }
    private async Task WriteFailureAsync(HttpContext context, Exception error, string? recordedIdentifier = null)
    {
        switch (error)
        {
                case ApiProblem problem:
                    if (problem.StatusCode is 413 or 408) context.Response.Headers.CacheControl = "no-store";
                    await ApiJson.ProblemAsync(context, problem.StatusCode, problem.Message).ConfigureAwait(false); break;
                case BadHttpRequestException request when (request.StatusCode is 413 or 408):
                    context.Response.Headers.CacheControl = "no-store";
                    await ApiJson.ProblemAsync(context, request.StatusCode, request.StatusCode == 413 ? "Request exceeds its byte limit" : "Request body timed out; retry the upload").ConfigureAwait(false); break;
                case HumanAssistanceRequiredException assistance:
                    context.Response.Headers.RetryAfter = assistance.RetryAfter.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.Headers.CacheControl = "no-store";
                    await ApiJson.WriteAsync(context, 503, new() { ["detail"] = assistance.Message, ["needs_attention"] = true, ["attention_id"] = assistance.AttentionId }).ConfigureAwait(false); break;
                case SourceCheckConflictException conflict:
                    await ApiJson.ProblemAsync(context, 409, conflict.Message).ConfigureAwait(false); break;
                case RepairHistoryConflictException history:
                    await ApiJson.ProblemAsync(context, 409, history.Message).ConfigureAwait(false); break;
                case SourceRawConflictException raw:
                    await ApiJson.ProblemAsync(context, 409, raw.Message).ConfigureAwait(false); break;
                case SourceWorkDeferredException deferred:
                    context.Response.Headers.RetryAfter = (deferred is SourceEngineUnavailableException unavailable ? unavailable.RetryAfter : 5).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    context.Response.Headers.CacheControl = "no-store";
                    await ApiJson.WriteAsync(context, 503, new() { ["detail"] = deferred.Message, ["deferred"] = true }).ConfigureAwait(false); break;
                case WorkDeferredException capacity:
                    context.Response.Headers.RetryAfter = "5"; context.Response.Headers.CacheControl = "no-store";
                    await ApiJson.WriteAsync(context, 503, new() { ["detail"] = capacity.Message, ["deferred"] = true }).ConfigureAwait(false); break;
                default:
                    var identifier = recordedIdentifier ?? recordUnexpected(context, error);
                    await ApiJson.WriteAsync(context, 500, new() { ["detail"] = "Unexpected AutoSourcarr error", ["error_id"] = identifier }).ConfigureAwait(false); break;
        }
    }
}
