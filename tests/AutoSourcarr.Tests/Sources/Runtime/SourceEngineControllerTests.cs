using System.Net;
using System.Text;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Sources;
using AutoSourcarr.Core.Sources.Runtime;

namespace AutoSourcarr.Tests;

/// <summary>
/// Phase 13: the source engine controller after the native PID 1 launcher was removed. External mode stays unmanaged
/// (no local readiness, no restart); integrated mode is only the app's own source host. The native-owner tests went
/// with the native launcher.
/// </summary>
public static class SourceEngineControllerTests
{
    private sealed class Transport : ISourceTransportFactory
    {
        public List<Uri> Calls { get; } = [];
        public HttpMessageHandler Create(SourceTransportPlane plane) => new Handler(this);
        private sealed class Handler(Transport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (owner.Calls) owner.Calls.Add(request.RequestUri!);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{\"data\":{\"__typename\":\"Query\"}}", Encoding.UTF8, "application/json") });
            }
        }
    }

    public static void Register(TestSuite suite)
    {
        suite.Add("source controller: external mode stays unmanaged (no local readiness, requests pass through, clean stop)", () => External().GetAwaiter().GetResult());
        suite.Add("source controller: integrated mode is refused without the app's own source host", () => IntegratedNeedsHost().GetAwaiter().GetResult());
    }

    private static async Task External()
    {
        using var temp = new Sandbox(); using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
        new SchemaMigrator(database).Initialize();
        var privacy = new SupportPrivacy(); var errors = new ErrorRegistry(new("controlled-version", "controlled-instance", "2026-11-02T00:00:00Z"), privacy);
        await using var io = new BoundedWorkScheduler(2, 8); using var admin = new SemaphoreSlim(1, 1); var transport = new Transport();
        var settings = new AutoSourcarrSettings { SourceEngineMode = "external", SourceSuwayomiUrl = "http://external-engine.invalid", DataDir = temp.Path };
        var environment = new SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "source-data"), privacy);
        await using var controller = await SourceEngineController.ControlledHarness.CreateExternalAsync(settings, environment, new Dictionary<string, string?>(),
            admin, io, new SettingsRepository(database), privacy, errors, TimeProvider.System, transport);
        await controller.StartAsync();
        Check.True(controller.Started, "started");
        Check.Equal("external", controller.Status.State);
        Check.True(!controller.Status.Ready, "external mode reports no local managed readiness");
        var response = await controller.Client.GraphQlAsync("query External { __typename }");
        Check.True(response is not null, "the external engine answers");
        Check.True(transport.Calls.Count == 1 && transport.Calls[0].Host == "external-engine.invalid", string.Join(",", transport.Calls));
        await controller.StopAsync();
        Check.Equal("external", controller.Status.State);
    }

    private static async Task IntegratedNeedsHost()
    {
        using var temp = new Sandbox(); using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
        new SchemaMigrator(database).Initialize();
        var privacy = new SupportPrivacy(); var errors = new ErrorRegistry(new("controlled-version", "controlled-instance", "2026-11-02T00:00:00Z"), privacy);
        await using var io = new BoundedWorkScheduler(2, 8); using var admin = new SemaphoreSlim(1, 1);
        var environment = new SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "source-data"), privacy);
        try
        {
            await using var controller = await SourceEngineController.ControlledHarness.CreateExternalAsync(new AutoSourcarrSettings { SourceEngineMode = "integrated", DataDir = temp.Path },
                environment, new Dictionary<string, string?>(), admin, io, new SettingsRepository(database), privacy, errors, TimeProvider.System, new Transport());
            throw new InvalidOperationException("integrated mode without a source host was accepted");
        }
        catch (ArgumentException error) { Check.True(error.Message.Contains("source host", StringComparison.Ordinal), error.Message); }
    }
}
