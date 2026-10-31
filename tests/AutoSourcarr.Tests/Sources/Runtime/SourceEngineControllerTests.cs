using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutoSourcarr.Core.Concurrency;
using AutoSourcarr.Core.Settings;
using AutoSourcarr.Core.Diagnostics;
using AutoSourcarr.Core.Privacy;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Sources.Runtime;
using AutoSourcarr.Core.Sources;

namespace AutoSourcarr.Tests;

/// <summary>
/// Controlled real managed actor/HTTP epoch tests. Synthetic boot, command replies and HTTP below
/// are not evidence of Linux credentials, a native waitpid, a JVM, a source observer or source operation.
/// </summary>
public static class SourceEngineControllerTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("source controller: construction is cheap, cached and initially fences source admission", () => InitialFence().GetAwaiter().GetResult());
        suite.Add("source controller: external mode preserves unmanaged requests without a local generation", () => External().GetAwaiter().GetResult());
        suite.Add("source controller: fresh actual about plus same-generation native status precede ready and API shape", () => Readiness().GetAwaiter().GetResult());
        suite.Add("source controller: occupied, malformed, status and ambiguous refusal observations never launch", () => Occupancy().GetAwaiter().GetResult());
        suite.Add("source controller: a preflight deadline and shutdown still join physically blocked HTTP", () => PreflightDeadline().GetAwaiter().GetResult());
        suite.Add("source controller: missing immutable launcher remains setup-required until explicit retry", () => MissingAssets().GetAwaiter().GetResult());
        suite.Add("source controller: original two-second doubling retry remains capped at sixty seconds", () => Backoff().GetAwaiter().GetResult());
        suite.Add("source controller: an attempt longer than 120 seconds resets the original retry delay", () => StableReset().GetAwaiter().GetResult());
        suite.Add("source controller: coalesced pause fences immediately and drains actual HTTP before native stop", () => PauseDrain().GetAwaiter().GetResult());
        suite.Add("source controller: automatic native exit fences owned HTTP before waiting for its held admin gate", () => AutomaticExitFence().GetAwaiter().GetResult());
        suite.Add("source controller: obsolete committed start cannot complete a later restart or publish ready", () => SupersededStart().GetAwaiter().GetResult());
        suite.Add("source controller: readiness loss and recovery stay within the actual engine generation", () => ReadinessRecovery().GetAwaiter().GetResult());
        suite.Add("source controller: owner retirement is fatal and terminal completion joins the actual actor thread", () => Retirement().GetAwaiter().GetResult());
        suite.Add("source controller: cleanup graphs survive physical HTTP and native stop failures", () => CleanupFailures().GetAwaiter().GetResult());
        suite.Add("source controller: a mismatched construction request joins its transferred native owner", () => ConstructionFailure().GetAwaiter().GetResult());
        suite.Add("source controller: unsigned native generation cannot truncate into an existing signed consumer", () => UnsignedGeneration().GetAwaiter().GetResult());
        suite.Add("source controller: configured work limit preserves background deferral and reserved foreground HTTP", () => WorkReservation().GetAwaiter().GetResult());
        suite.Add("source controller: a discarded capability observation retries on actual same-generation readiness recovery", () => CapabilityRecovery().GetAwaiter().GetResult());
        suite.Add("source controller: mismatched privacy owners refuse startup and join actual transferred ownership", () => PrivacyScope().GetAwaiter().GetResult());
        suite.Add("source controller: constructor authority and controlled harness do not mint native qualification", AuthorityBoundary);
    }

    private static async Task InitialFence()
    {
        await using var setup = await Setup.Create();
        Check.Equal("stopped", setup.Controller.Status.State); Check.True(!setup.Controller.Status.Ready);
        Check.Equal(0, setup.Peer.Commands.Count); Check.Equal(0, setup.Http.Calls.Count);
        Check.True(!setup.Controller.Status.SourceObserverAvailable); Check.Equal("waiting", setup.Controller.Status.ChallengeState);
        var request = setup.Controller.Client.GraphQlAsync("query Deferred { __typename }");
        Check.True(await Failure(request) is SourceWorkDeferredException or OperationCanceledException);
        Check.Equal(0, setup.Http.Calls.Count); Check.Equal(0, setup.Peer.Commands.Count);
        for (var index = 0; index < 20; index++) _ = setup.Controller.Status;
        Check.Equal(0, setup.Http.Calls.Count); Check.Equal(0, setup.Peer.Commands.Count);
        // A cancelled request before admission does not start the loop or consume a native command ID.
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        Check.Throws<OperationCanceledException>(() => setup.Controller.RestartAsync(cancel.Token));
        Check.Equal(0, setup.Peer.Commands.Count);
    }
    private static async Task External()
    {
        await using var setup = await Setup.Create(external: true);
        await setup.Controller.StartAsync(); Check.Equal("external", setup.Controller.Status.State);
        // The original external snapshot has no ready key. This new flag means local managed readiness,
        // not an assertion that the external server is unavailable; actual external HTTP still proceeds.
        Check.True(!setup.Controller.Status.Ready && setup.Controller.Status.NativeGeneration is null);
        var response = await setup.Controller.Client.GraphQlAsync("query External { __typename }");
        Check.True(response is not null); Check.Equal(0, setup.Peer.Commands.Count);
        Check.Equal(1, setup.Http.Calls.Count); Check.Equal("external-engine.invalid", setup.Http.Calls[0].Uri.Host);
        Check.Throws<NotSupportedException>(() => setup.Controller.RestartAsync());
        Check.Throws<NotSupportedException>(() => setup.Controller.PauseAsync());
        Check.Throws<SourceEngineUnavailableException>(() => setup.Controller.State());
        setup.Clock.Advance(TimeSpan.FromHours(1)); Check.Equal(1, setup.Http.Calls.Count);
    }
    private static async Task Readiness()
    {
        await using var setup = await Setup.Create();
        await setup.Controller.StartAsync(); await Until(() => setup.Controller.Status.Ready && setup.Controller.Status.CapabilityCurrent && setup.Clock.HasDelay(3));
        var status = setup.Controller.Status; Check.Equal("ready", status.State); Check.Equal(1UL, status.NativeGeneration!.Value);
        Check.Equal("2.3.2243", status.ObservedVersion); Check.True(!status.SourceObserverAvailable);
        Check.Equal("false", setup.Peer.Commands.Single(command => command.Opcode == NativeSourceOwnerOpcode.Start).Environment.Single(pair => pair.Key == "FLARESOLVERR_ENABLED").Value);
        Check.Equal("true", setup.Controller.ChallengeIntent(1)["FLARESOLVERR_ENABLED"]);
        Check.Equal("waiting", status.ChallengeState);
        Check.Throws<SourceEngineUnavailableException>(() => setup.Controller.ChallengeIntent(2));
        Check.True(!status.CapabilityReport!["native_qualified"]!.GetValue<bool>());
        var commands = setup.Peer.Commands;
        Check.Equal(NativeSourceOwnerOpcode.Stop, commands[0].Opcode);
        Check.Equal(NativeSourceOwnerOpcode.Start, commands[1].Opcode);
        Check.True(commands.Skip(2).All(command => command.Opcode == NativeSourceOwnerOpcode.Status));
        Check.True(setup.Trace.IndexOf("http:preflight") > setup.Trace.IndexOf("native:Stop"));
        Check.True(setup.Trace.IndexOf("native:Start") > setup.Trace.IndexOf("http:preflight"));
        Check.True(setup.Trace.IndexOf("http:readiness") > setup.Trace.IndexOf("native:Start"));
        Check.True(setup.Trace.IndexOf("http:capability-graphql") > setup.Trace.IndexOf("http:readiness"));
        var aboutEpochs = setup.Http.Calls.Where(call => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)).Select(call => call.HandlerId).Distinct().ToArray();
        Check.True(aboutEpochs.Length >= 2); Check.True(setup.Http.Handlers.First(handler => handler.Id == aboutEpochs[0]).Closed);
        Check.True(setup.Http.Handlers.Where(handler => handler.Plane == SourceTransportPlane.Engine).Count() >= 3);
        var count = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start);
        setup.Clock.Advance(TimeSpan.FromSeconds(3)); await Until(() => setup.Http.AboutCount >= 4);
        Check.Equal(count, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        // Returned reports are snapshots, not a mutable authority to update controller state.
        status.CapabilityReport!["native_qualified"] = true;
        Check.True(!setup.Controller.Status.CapabilityReport!["native_qualified"]!.GetValue<bool>());
    }
    private static async Task Occupancy()
    {
        var privateDetail = "synthetic-unregistered-upstream-private-detail";
        foreach (var mode in new[] { "ok", "unauthorized", "unavailable", "malformed", "wrapped-refusal", "wrong-socket", "message-only" })
        {
            await using var setup = await Setup.Create();
            setup.Http.Override = (call, _) => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal) ? mode switch
            {
                "ok" => Task.FromResult<HttpResponseMessage?>(Json("{\"version\":\"occupied\"}")),
                "unauthorized" => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.Unauthorized)),
                "unavailable" => Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable)),
                "malformed" => Task.FromResult<HttpResponseMessage?>(Json("{")),
                "wrapped-refusal" => Task.FromException<HttpResponseMessage?>(new AggregateException(Refusal())),
                "wrong-socket" => Task.FromException<HttpResponseMessage?>(new HttpRequestException(privateDetail, new SocketException((int)SocketError.HostUnreachable))),
                _ => Task.FromException<HttpResponseMessage?>(new HttpRequestException("Connection refused " + privateDetail))
            } : Task.FromResult<HttpResponseMessage?>(null);
            await setup.Controller.StartAsync(); await Until(() => setup.Controller.Status.State == "restarting" && setup.Clock.HasDelay(2));
            Check.Equal(0, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
            Check.True(!setup.Controller.Status.Ready); Check.True(!setup.Controller.Status.Error.Contains(privateDetail, StringComparison.Ordinal));
            Check.True(!setup.Errors.Snapshot().ToJsonString().Contains(privateDetail, StringComparison.Ordinal));
            Check.True(!SourceEngineController.DirectRefusal(new AggregateException(Refusal())));
            Check.True(!SourceEngineController.DirectRefusal(new HttpRequestException("refused", Refusal().InnerException, HttpStatusCode.ServiceUnavailable)));
        }
        Check.True(SourceEngineController.DirectRefusal(Refusal()));
    }
    private static async Task PreflightDeadline()
    {
        await using var setup = await Setup.Create(); var entered = Signal(); var release = Signal(); var physical = Signal();
        setup.Http.Override = async (call, _) =>
        {
            if (!call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)) return null;
            entered.TrySetResult(); await release.Task; physical.TrySetResult(); return Json("{\"version\":\"occupied\"}");
        };
        Task? stop = null;
        try
        {
            await setup.Controller.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var stops = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop);
            setup.Clock.Advance(TimeSpan.FromSeconds(1)); stop = setup.Controller.StopAsync();
            Check.True(!stop.IsCompleted && !physical.Task.IsCompleted);
            Check.Equal(stops, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop));
            Check.Equal(0, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
            release.TrySetResult(); await stop.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(physical.Task.IsCompleted);
            Check.True(setup.Peer.ClosedFinished); Check.True(setup.Http.Handlers.All(handler => handler.Closed));
        }
        finally { release.TrySetResult(); if (stop is not null) await Failure(stop); }
    }
    private static async Task MissingAssets()
    {
        await using var setup = await Setup.Create(); setup.Peer.UnavailableStart = true;
        await setup.Controller.StartAsync(); await Until(() => setup.Controller.Status.State == "setup-required");
        await Until(() => setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop) >= 2);
        Check.True(!setup.Controller.Status.Ready); Check.True(!setup.Controller.Status.SourceObserverAvailable);
        var starts = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start);
        setup.Clock.Advance(TimeSpan.FromHours(1)); Check.Equal(starts, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        Check.Equal("MissingIntegratedRuntime", setup.Errors.Snapshot()["items"]![0]!["error_type"]!.GetValue<string>());
        setup.Peer.UnavailableStart = false; var restart = setup.Controller.RestartAsync();
        var ready = await restart.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(ready.Status.Ready); Check.True(ready.NativeStop is null);
        Check.True(setup.Controller.Status.Ready); Check.Equal(starts + 1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
    }
    private static async Task Backoff()
    {
        await using var setup = await Setup.Create();
        setup.Http.Override = (call, _) => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal) ? Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable)) : Task.FromResult<HttpResponseMessage?>(null);
        await setup.Controller.StartAsync(); var previous = 0;
        foreach (var seconds in new[] { 2, 4, 8, 16, 32, 60, 60 })
        {
            await Until(() => setup.Controller.Status.State == "restarting" && setup.Clock.HasDelay(seconds));
            var current = setup.Http.AboutCount; Check.Equal(previous + 1, current); previous = current;
            setup.Clock.Advance(TimeSpan.FromSeconds(seconds - 1)); Check.Equal(current, setup.Http.AboutCount);
            setup.Clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => setup.Http.AboutCount == current + 1);
        }
        Check.True(setup.Controller.Status.Restarts >= 7); Check.Equal(0, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
    }
    private static async Task StableReset()
    {
        await using var setup = await Setup.Create();
        var failPreflight = true;
        setup.Http.Override = (call, _) => failPreflight && call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)
            ? Task.FromResult<HttpResponseMessage?>(new(HttpStatusCode.ServiceUnavailable)) : Task.FromResult<HttpResponseMessage?>(null);
        await setup.Controller.StartAsync(); await Until(() => setup.Clock.HasDelay(2));
        setup.Clock.Advance(TimeSpan.FromSeconds(2)); await Until(() => setup.Clock.HasDelay(4));
        failPreflight = false; setup.Peer.HoldStart = true; setup.Clock.Advance(TimeSpan.FromSeconds(4));
        await setup.Peer.StartSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        setup.Clock.Advance(TimeSpan.FromSeconds(121)); setup.Peer.ExitOnNextStatus = true; setup.Peer.ReleaseStart();
        await Until(() => setup.Controller.Status.State == "restarting" && setup.Clock.HasDelay(2));
        Check.True(!setup.Clock.HasDelay(8)); Check.True(!setup.Controller.Status.Ready);
    }
    private static async Task PauseDrain()
    {
        await using var setup = await Setup.Create(); await setup.Ready();
        var entered = Signal(); var release = Signal(); var physical = Signal(); using var cancel = new CancellationTokenSource();
        setup.Http.Override = async (call, _) =>
        {
            if (!call.Body.Contains("HoldOwned", StringComparison.Ordinal)) return null;
            entered.TrySetResult(); await release.Task; physical.TrySetResult(); return Json("{\"data\":{}}");
        };
        var work = setup.Controller.Client.GraphQlAsync("query HoldOwned { __typename }"); Task<SourceEngineController.EnginePause>? pause = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); var stops = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop);
            setup.Peer.HoldStop = true; pause = setup.Controller.PauseAsync(cancel.Token); var same = setup.Controller.PauseAsync();
            Check.True(ReferenceEquals(pause, same)); cancel.Cancel(); Check.True(!pause.IsCompleted);
            Check.True(!setup.Controller.Status.Ready); Check.Equal(stops, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop));
            Check.True(await Failure(setup.Controller.Client.GraphQlAsync("query Fenced { __typename }")) is SourceWorkDeferredException or OperationCanceledException);
            Check.Throws<SourceWorkDeferredException>(() => setup.Controller.RestartAsync());
            release.TrySetResult(); await Until(() => setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop) > stops);
            Check.True(physical.Task.IsCompleted && !pause.IsCompleted); setup.Peer.ReleaseStop();
            var result = await pause.WaitAsync(TimeSpan.FromSeconds(3)); Check.Equal("stopped", result.Status.State); Check.True(!result.Status.Ready);
            Check.True(result.NativeStop is not null); var starts = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start);
            setup.Clock.Advance(TimeSpan.FromHours(1)); Check.Equal(starts, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
            await Failure(work); Check.True(!setup.Controller.Status.Ready);
        }
        finally { release.TrySetResult(); setup.Peer.ReleaseStop(); await Failure(work); if (pause is not null) await Failure(pause); }
    }
    private static async Task SupersededStart()
    {
        await using var setup = await Setup.Create(); setup.Peer.HoldStart = true;
        await setup.Controller.StartAsync(); await setup.Peer.StartSeen.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var restart = setup.Controller.RestartAsync(); var same = setup.Controller.RestartAsync(); Check.True(ReferenceEquals(restart, same));
        Check.True(!restart.IsCompleted); Check.True(!setup.Controller.Status.Ready);
        setup.Peer.HoldStart = false; setup.Peer.ReleaseStart();
        var completed = await restart.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(completed.Status.Ready); Check.Equal(2UL, completed.Status.NativeGeneration!.Value);
        var sequence = setup.Peer.Commands.Select(command => command.Opcode).ToArray();
        var firstStart = Array.IndexOf(sequence, NativeSourceOwnerOpcode.Start); var secondStart = Array.LastIndexOf(sequence, NativeSourceOwnerOpcode.Start);
        Check.True(secondStart > firstStart + 1); Check.True(sequence[(firstStart + 1)..secondStart].Contains(NativeSourceOwnerOpcode.Stop));
        // No about observation from the first obsolete engine is allowed to grant the request's success.
        Check.True(setup.Http.Calls.Where(call => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal) && call.EngineRunning).All(call => call.Generation == 2));
        await Until(() => setup.Clock.HasDelay(3)); var abouts = setup.Http.AboutCount;
        setup.Clock.Advance(TimeSpan.FromSeconds(3)); await Until(() => setup.Http.AboutCount > abouts);
        Check.Equal(2, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
    }
    private static async Task AutomaticExitFence()
    {
        await using var setup = await Setup.Create(); await setup.Ready();
        var entered = Signal(); var cancelled = Signal(); var release = Signal(); var finished = Signal();
        setup.Http.Override = async (call, token) =>
        {
            if (!call.Body.Contains("OwnAdminPhysical", StringComparison.Ordinal)) return null;
            using var observation = token.Register(() => cancelled.TrySetResult());
            entered.TrySetResult(); await release.Task; finished.TrySetResult(); return Json("{\"data\":{}}");
        };
        // One controlled compound operation holds admin while awaiting a genuine owned HTTP call.
        await setup.Admin.WaitAsync(); var adminHeld = true;
        var work = setup.Controller.Client.GraphQlAsync("query OwnAdminPhysical { __typename }");
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var active = setup.Http.Calls.Single(call => call.Body.Contains("OwnAdminPhysical", StringComparison.Ordinal));
            var stops = setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop);
            setup.Peer.ExitOnNextStatus = true; setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!setup.Controller.Status.Ready); Check.True(!work.IsCompleted && !finished.Task.IsCompleted);
            Check.Equal(stops, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop));
            Check.True(await Failure(setup.Controller.Client.GraphQlAsync("query FencedAfterExit { __typename }")) is SourceWorkDeferredException or OperationCanceledException);
            release.TrySetResult(); Check.True(await Failure(work) is OperationCanceledException);
            await Until(() => setup.Http.Handlers.Single(handler => handler.Id == active.HandlerId).Closed);
            Check.True(finished.Task.IsCompleted); Check.Equal(stops, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop));
            setup.Admin.Release(); adminHeld = false;
            await Until(() => setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop) > stops && setup.Clock.HasDelay(2));
            Check.Equal("restarting", setup.Controller.Status.State); Check.True(!setup.Controller.Status.Ready);
            Check.Equal(1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        }
        finally
        {
            release.TrySetResult();
            if (adminHeld) setup.Admin.Release();
            await Failure(work);
        }
    }
    private static async Task ReadinessRecovery()
    {
        await using var setup = await Setup.Create(); setup.ExpectShutdownFailure = true; await setup.Ready();
        var generation = setup.Controller.Status.NativeGeneration; var abouts = setup.Http.AboutCount;
        setup.Http.Override = (call, _) => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)
            ? Task.FromException<HttpResponseMessage?>(Refusal()) : Task.FromResult<HttpResponseMessage?>(null);
        setup.Clock.Advance(TimeSpan.FromSeconds(3)); await Until(() => setup.Http.AboutCount > abouts && setup.Controller.Status.State == "unresponsive");
        Check.True(!setup.Controller.Status.Ready); Check.Equal(generation, setup.Controller.Status.NativeGeneration);
        Check.Equal(1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        await Until(() => setup.Clock.HasDelay(3)); abouts = setup.Http.AboutCount;
        setup.Clock.Advance(TimeSpan.FromSeconds(3)); await Until(() => setup.Http.AboutCount > abouts && setup.Clock.HasDelay(3));
        Check.Equal("unresponsive", setup.Controller.Status.State);
        setup.Http.Override = null; setup.Clock.Advance(TimeSpan.FromSeconds(3)); await Until(() => setup.Controller.Status.Ready && setup.Clock.HasDelay(3));
        Check.Equal(generation, setup.Controller.Status.NativeGeneration); Check.True(setup.Controller.State().Ready);
        // A syntactically valid native response with a different generation is fatal, not a readiness retry.
        setup.Peer.StatusGenerationDelta = 1; setup.Clock.Advance(TimeSpan.FromSeconds(3));
        await setup.Controller.FatalFailure.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!setup.Controller.Status.Ready);
        Check.Equal(1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        Check.True(await Failure(setup.Controller.StopAsync()) is not null);
        await using var changed = await Setup.Create(); changed.ExpectShutdownFailure = true;
        changed.Http.Override = (call, _) =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal) && call.EngineRunning)
            { changed.Peer.StatusGenerationDelta = 1; return Task.FromResult<HttpResponseMessage?>(Json("{\"version\":\"2.3.2243\"}")); }
            return Task.FromResult<HttpResponseMessage?>(null);
        };
        await changed.Controller.StartAsync(); await changed.Controller.FatalFailure.WaitAsync(TimeSpan.FromSeconds(3));
        Check.True(!changed.Controller.Status.Ready);
        Check.Equal(0, changed.Http.Calls.Count(call => call.Body.Contains("ReimaginedEngineContract", StringComparison.Ordinal)));
    }
    private static async Task Retirement()
    {
        await using var setup = await Setup.Create(); setup.ExpectShutdownFailure = true; await setup.Ready(); setup.Peer.CloseGate.Reset();
        try
        {
            setup.Peer.CorruptNextResponse = true; setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await setup.Controller.FatalFailure.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(setup.Peer.CloseEntered.Wait(TimeSpan.FromSeconds(3)));
            var stop = setup.Controller.StopAsync(); Check.True(!stop.IsCompleted); Check.True(!setup.Controller.Status.Ready);
            Check.Throws<OperationCanceledException>(() => setup.Controller.RestartAsync());
            Check.Equal(1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
            setup.Peer.CloseGate.Set(); var failure = await Failure(stop); Check.True(failure is not null);
            Check.True(Tree(failure!).Any(error => error is InvalidDataException)); Check.True(setup.Peer.ClosedFinished);
            Check.True(setup.Peer.Closed.Values.All(count => count == 1));
        }
        finally { setup.Peer.CloseGate.Set(); }
    }
    private static async Task CleanupFailures()
    {
        await using var setup = await Setup.Create(); setup.ExpectShutdownFailure = true; var primary = new IOException("controlled failed preflight read");
        var poolFailure = new IOException("controlled physical HTTP pool close failure"); var nativeFailure = new IOException("controlled native command close failure");
        setup.Http.Override = (call, _) => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)
            ? Task.FromException<HttpResponseMessage?>(primary) : Task.FromResult<HttpResponseMessage?>(null);
        setup.Http.CloseFailureAfterAbout = poolFailure; setup.Peer.CommandCloseFailure = nativeFailure;
        setup.Peer.BadStopAfterFirst = true;
        await setup.Controller.StartAsync(); await setup.Controller.FatalFailure.WaitAsync(TimeSpan.FromSeconds(3));
        var failure = await Failure(setup.Controller.StopAsync()); Check.True(failure is not null);
        var tree = Tree(failure!).ToArray();
        Check.True(tree.Any(error => ReferenceEquals(error, primary)));
        Check.True(tree.Any(error => ReferenceEquals(error, poolFailure)));
        Check.True(tree.Any(error => ReferenceEquals(error, nativeFailure)));
        Check.True(setup.Peer.ClosedFinished); Check.True(setup.Http.Handlers.All(handler => handler.Closed));
        Check.True(ReferenceEquals(setup.Controller.StopAsync(), setup.Controller.StopAsync()));
    }
    private static void AuthorityBoundary()
    {
        var type = typeof(SourceEngineController);
        Check.Equal(0, type.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Length);
        var integrated = type.GetMethod("CreateIntegratedAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Check.True(integrated.GetParameters().Any(parameter => parameter.ParameterType == typeof(NativeSourceOwnerConnection)));
        Check.True(!integrated.GetParameters().Any(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType)));
        Check.True(!typeof(SourceEngineController.ControlledHarness).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .Any(property => property.PropertyType == typeof(NativeSourceOwnerConnection)));
    }
    private static async Task ConstructionFailure()
    {
        using var temp = new Sandbox(); using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
        await using var io = new BoundedWorkScheduler(1, 2); using var admin = new SemaphoreSlim(1, 1);
        var privacy = new SupportPrivacy(); var trace = new Trace(); using var peer = new Peer(Boot(), trace);
        await using var native = new NativeSourceOwnerConnection.ControlledHarness(peer, peer.Boot.StartupRequest);
        var settings = new AutoSourcarrSettings { SourceEngineMode = "integrated", SourceEngineUid = peer.Boot.StartupRequest.Uid + 1, SourceEngineGid = peer.Boot.StartupRequest.Gid };
        var create = SourceEngineController.ControlledHarness.CreateAsync(settings, native, peer.Boot,
            new(temp.Path, Path.Combine(temp.Path, "source-data"), privacy), new Dictionary<string, string?>(), admin, io,
            new(database), privacy, new(new("fixture", "fixture", "now"), privacy), new StepClock(), new Factory(peer, trace));
        var error = await Failure(create); Check.True(error is ConfigurationException);
        Check.True(peer.ClosedFinished); Check.True(peer.Closed.Values.All(count => count == 1));
        Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Shutdown }));
    }
    private static async Task PrivacyScope()
    {
        foreach (var wrongEnvironment in new[] { false, true })
        {
            using var temp = new Sandbox(); using var database = new SqliteDatabase(new AutoSourcarrSettings { DataDir = temp.Path });
            await using var io = new BoundedWorkScheduler(1, 2); using var admin = new SemaphoreSlim(1, 1);
            using var logs = new SupportLogTests.Fixture(); var privacy = wrongEnvironment ? logs.Privacy : new SupportPrivacy();
            var trace = new Trace(); using var peer = new Peer(Boot(), trace);
            await using var native = new NativeSourceOwnerConnection.ControlledHarness(peer, peer.Boot.StartupRequest, diagnostics: logs.Store);
            var settings = new AutoSourcarrSettings { SourceEngineMode = "integrated", SourceEngineUid = peer.Boot.StartupRequest.Uid, SourceEngineGid = peer.Boot.StartupRequest.Gid };
            var environment = new SourceEngineEnvironment(temp.Path, Path.Combine(temp.Path, "source-data"), wrongEnvironment ? new SupportPrivacy() : privacy);
            var create = SourceEngineController.ControlledHarness.CreateAsync(settings, native, peer.Boot,
                environment, new Dictionary<string, string?>(), admin, io, new(database), privacy,
                new(new("fixture", "fixture", "now"), privacy), new StepClock(), new Factory(peer, trace));
            Check.True(await Failure(create) is ArgumentException); Check.True(peer.ClosedFinished);
            Check.True(peer.Commands.Select(command => command.Opcode).SequenceEqual(new[] { NativeSourceOwnerOpcode.Shutdown }));
            Check.True(peer.Closed.Values.All(count => count == 1));
        }
    }
    private static async Task UnsignedGeneration()
    {
        await using var setup = await Setup.Create(); setup.Peer.SeedGeneration(ulong.MaxValue - 1); await setup.Ready();
        Check.Equal(ulong.MaxValue, setup.Controller.Status.NativeGeneration!.Value);
        Check.Throws<SourceEngineUnavailableException>(() => setup.Controller.State());
        Check.True(setup.Controller.Status.Ready); // HTTP readiness is distinct from this narrower consumer's numeric range.
    }
    private static async Task WorkReservation()
    {
        await using var setup = await Setup.Create(workLimit: 2, backgroundLimit: 1); await setup.Ready();
        var backgroundEntered = Signal(); var foregroundEntered = Signal(); var releaseBackground = Signal(); var releaseForeground = Signal();
        var admitted = new List<string>();
        setup.Http.Override = async (call, _) =>
        {
            if (!call.Body.Contains("fetchSourceManga", StringComparison.Ordinal)) return null;
            var source = JsonNode.Parse(call.Body)!["variables"]!["input"]!["source"]!.GetValue<string>();
            lock (admitted) admitted.Add(source);
            if (source == "background-held") { backgroundEntered.TrySetResult(); await releaseBackground.Task; }
            if (source == "foreground-held") { foregroundEntered.TrySetResult(); await releaseForeground.Task; }
            return Json("{\"data\":{\"fetchSourceManga\":{\"hasNextPage\":false,\"mangas\":[]}}}");
        };
        Task<JsonObject> background;
        using (SourceWorkContext.Background()) background = setup.Controller.Client.FetchSourceMangaAsync("background-held", "POPULAR");
        Task<JsonObject>? foreground = null;
        try
        {
            await backgroundEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Task<JsonObject> overBackground;
            using (SourceWorkContext.Background())
            using (SourceWorkContext.WithBudget(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)))
                overBackground = setup.Controller.Client.FetchSourceMangaAsync("background-over-cap", "POPULAR");
            Check.True(await Failure(overBackground) is SourceWorkDeferredException);
            lock (admitted) Check.True(admitted.SequenceEqual(new[] { "background-held" }));
            foreground = setup.Controller.Client.FetchSourceMangaAsync("foreground-held", "POPULAR");
            await foregroundEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!background.IsCompleted && !foreground.IsCompleted);
            Task<JsonObject> overTotal;
            using (SourceWorkContext.WithBudget(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(100)))
                overTotal = setup.Controller.Client.FetchSourceMangaAsync("foreground-over-cap", "POPULAR");
            Check.True(await Failure(overTotal) is SourceWorkDeferredException);
            lock (admitted) Check.True(admitted.SequenceEqual(new[] { "background-held", "foreground-held" }));
            releaseForeground.TrySetResult(); await foreground.WaitAsync(TimeSpan.FromSeconds(3)); Check.True(!background.IsCompleted);
            releaseBackground.TrySetResult(); await background.WaitAsync(TimeSpan.FromSeconds(3));
            _ = await setup.Controller.Client.FetchSourceMangaAsync("after-release", "POPULAR").WaitAsync(TimeSpan.FromSeconds(3));
            lock (admitted) Check.True(admitted.SequenceEqual(new[] { "background-held", "foreground-held", "after-release" }));
            Check.True(setup.Controller.Status.Ready); Check.Equal(1, setup.Peer.Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Start));
        }
        finally
        {
            releaseBackground.TrySetResult(); releaseForeground.TrySetResult(); await Failure(background);
            if (foreground is not null) await Failure(foreground);
        }
    }
    private static async Task CapabilityRecovery()
    {
        await using var setup = await Setup.Create();
        var firstEntered = Signal(); var secondEntered = Signal(); var releaseFirst = Signal(); var releaseSecond = Signal();
        var readinessUnavailable = 0; var checks = 0; var active = 0; var maximumActive = 0; var checkGate = new object();
        setup.Http.Override = async (call, _) =>
        {
            if (call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal) && call.EngineRunning && Volatile.Read(ref readinessUnavailable) != 0)
                throw Refusal();
            if (!call.Body.Contains("ReimaginedEngineContract", StringComparison.Ordinal)) return null;
            var number = Interlocked.Increment(ref checks); var nowActive = Interlocked.Increment(ref active);
            lock (checkGate) maximumActive = Math.Max(maximumActive, nowActive);
            try
            {
                if (number == 1) { firstEntered.TrySetResult(); await releaseFirst.Task; }
                if (number == 2) { secondEntered.TrySetResult(); await releaseSecond.Task; }
                return Json(new JsonObject { ["data"] = Schema() }.ToJsonString());
            }
            finally { Interlocked.Decrement(ref active); }
        };
        try
        {
            await setup.Controller.StartAsync(); await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await Until(() => setup.Controller.Status.Ready && setup.Clock.HasDelay(3));
            var first = setup.Controller.CapabilityObservation; var generation = setup.Controller.Status.NativeGeneration;
            Check.True(!setup.Controller.Status.CapabilityCurrent && !first.IsCompleted);
            Volatile.Write(ref readinessUnavailable, 1); setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await Until(() => setup.Controller.Status.State == "unresponsive" && setup.Clock.HasDelay(3));
            releaseFirst.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(!setup.Controller.Status.Ready && !setup.Controller.Status.CapabilityCurrent);
            Check.True(setup.Controller.Status.CapabilityReport is null);
            var historical = setup.Store.Get(SourceEngineCapabilityContract.SettingKey)!.AsObject();
            Check.Equal("2.3.2243", historical["version"]!.GetValue<string>()); Check.True(!historical["native_qualified"]!.GetValue<bool>());
            Volatile.Write(ref readinessUnavailable, 0); setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await Until(() => setup.Controller.Status.Ready && setup.Clock.HasDelay(3));
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var second = setup.Controller.CapabilityObservation; Check.True(!second.IsCompleted && !setup.Controller.Status.CapabilityCurrent);
            for (var index = 0; index < 2; index++)
            {
                var abouts = setup.Http.AboutCount; setup.Clock.Advance(TimeSpan.FromSeconds(3));
                await Until(() => setup.Http.AboutCount > abouts && setup.Controller.Status.Ready && setup.Clock.HasDelay(3));
                Check.Equal(2, Volatile.Read(ref checks)); Check.Equal(1, Volatile.Read(ref active));
            }
            releaseSecond.TrySetResult(); await second.WaitAsync(TimeSpan.FromSeconds(3));
            Check.True(setup.Controller.Status.CapabilityCurrent); Check.Equal(generation, setup.Controller.Status.NativeGeneration);
            Check.True(!setup.Controller.Status.CapabilityReport!["native_qualified"]!.GetValue<bool>());
            var validSameGeneration = setup.Controller.Status.CapabilityReport!.ToJsonString();
            // A valid same-generation historical observation need not be reissued on every recovery.
            Volatile.Write(ref readinessUnavailable, 1); setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await Until(() => setup.Controller.Status.State == "unresponsive" && setup.Clock.HasDelay(3));
            Volatile.Write(ref readinessUnavailable, 0); setup.Clock.Advance(TimeSpan.FromSeconds(3));
            await Until(() => setup.Controller.Status.Ready && setup.Clock.HasDelay(3));
            await setup.Controller.CapabilityObservation.WaitAsync(TimeSpan.FromSeconds(3));
            Check.Equal(2, Volatile.Read(ref checks)); lock (checkGate) Check.Equal(1, maximumActive);
            Check.Equal(validSameGeneration, setup.Controller.Status.CapabilityReport!.ToJsonString());
        }
        finally
        {
            releaseFirst.TrySetResult(); releaseSecond.TrySetResult();
            await Failure(setup.Controller.CapabilityObservation);
        }
    }

    private sealed class Setup : IAsyncDisposable
    {
        private readonly Sandbox _temp = new(); private readonly SqliteDatabase _database;
        internal readonly BoundedWorkScheduler Io = new(2, 8); internal readonly SemaphoreSlim Admin = new(1, 1);
        internal readonly StepClock Clock = new(); internal readonly SupportPrivacy Privacy = new();
        internal readonly Trace Trace = new(); internal readonly Peer Peer; internal readonly Factory Http;
        internal readonly SettingsRepository Store; internal readonly ErrorRegistry Errors;
        internal bool ExpectShutdownFailure;
        internal SourceEngineController.ControlledHarness Controller { get; private set; } = null!;
        private NativeSourceOwnerConnection.ControlledHarness? _native;
        private Setup()
        {
            _database = new(new AutoSourcarrSettings { DataDir = _temp.Path }); new SchemaMigrator(_database).Initialize(); Store = new(_database);
            Errors = new(new("controlled-version", "controlled-instance", "2026-10-22T00:00:00Z"), Privacy);
            Peer = new(Boot(), Trace); Http = new(Peer, Trace);
        }
        internal static async Task<Setup> Create(bool external = false, int workLimit = 4, int backgroundLimit = 1)
        {
            var setup = new Setup();
            try
            {
                var boot = setup.Peer.Boot; var settings = new AutoSourcarrSettings
                {
                    SourceEngineMode = external ? "external" : "integrated", SourceEngineUid = boot.StartupRequest.Uid,
                    SourceEngineGid = boot.StartupRequest.Gid, SourceSuwayomiUrl = "http://external-engine.invalid",
                    DataDir = setup._temp.Path, AppServicesMode = "integrated", SourceHostWorkConcurrency = workLimit,
                    BackgroundSourceConcurrency = backgroundLimit
                };
                var environment = new SourceEngineEnvironment(setup._temp.Path, Path.Combine(setup._temp.Path, "source-data"), setup.Privacy);
                var parent = new Dictionary<string, string?> { ["HOME"] = "/home/suwayomi", ["PATH"] = "/usr/bin:/bin", ["JAVA_HOME"] = "/opt/java/openjdk", ["LANG"] = "C.UTF-8" };
                if (external) setup.Controller = await SourceEngineController.ControlledHarness.CreateExternalAsync(settings, environment,
                    parent, setup.Admin, setup.Io, setup.Store, setup.Privacy, setup.Errors, setup.Clock, setup.Http);
                else
                {
                    setup._native = new(setup.Peer, boot.StartupRequest);
                    setup.Controller = await SourceEngineController.ControlledHarness.CreateAsync(settings, setup._native, boot, environment,
                        parent, setup.Admin, setup.Io, setup.Store, setup.Privacy, setup.Errors, setup.Clock, setup.Http);
                }
                return setup;
            }
            catch { await setup.DisposeAsync(); throw; }
        }
        internal async Task Ready()
        { await Controller.StartAsync(); await Until(() => Controller.Status.Ready && Controller.Status.CapabilityCurrent && Clock.HasDelay(3)); }
        public async ValueTask DisposeAsync()
        {
            var errors = new List<Exception>(); Peer.ReleaseAll();
            if (Controller is not null) try { await Controller.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
            if (_native is not null) try { await _native.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
            try { await Io.DisposeAsync(); } catch (Exception error) { errors.Add(error); }
            try { Admin.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { Peer.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { _database.Dispose(); } catch (Exception error) { errors.Add(error); }
            try { _temp.Dispose(); } catch (Exception error) { errors.Add(error); }
            if (!ExpectShutdownFailure && errors.Count > 0) throw new AggregateException("Controlled fixture cleanup failed", errors);
        }
    }
    private sealed class Trace
    {
        private readonly object _gate = new(); private readonly List<string> _items = [];
        internal void Add(string item) { lock (_gate) _items.Add(item); }
        internal int IndexOf(string item) { lock (_gate) return _items.IndexOf(item); }
    }
    private sealed record Call(SourceTransportPlane Plane, int HandlerId, Uri Uri, string Body, bool EngineRunning, ulong Generation);
    private sealed class Factory(Peer peer, Trace trace) : ISourceTransportFactory
    {
        private readonly object _gate = new(); private readonly List<Handler> _handlers = []; private readonly List<Call> _calls = [];
        internal Func<Call, CancellationToken, Task<HttpResponseMessage?>>? Override;
        internal IOException? CloseFailureAfterAbout;
        internal IReadOnlyList<Handler> Handlers { get { lock (_gate) return _handlers.ToArray(); } }
        internal IReadOnlyList<Call> Calls { get { lock (_gate) return _calls.ToArray(); } }
        internal int AboutCount => Calls.Count(call => call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal));
        public HttpMessageHandler Create(SourceTransportPlane plane)
        {
            lock (_gate) { var handler = new Handler(this, plane, _handlers.Count + 1); _handlers.Add(handler); return handler; }
        }
        private async Task<HttpResponseMessage> Send(Handler handler, HttpRequestMessage request, CancellationToken token)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(token);
            var call = new Call(handler.Plane, handler.Id, request.RequestUri!, body, peer.Running, peer.Generation);
            lock (_gate) _calls.Add(call);
            if (call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal))
            { handler.UsedAbout = true; trace.Add(peer.Running ? "http:readiness" : "http:preflight"); }
            if (call.Body.Contains("ReimaginedEngineContract", StringComparison.Ordinal)) trace.Add("http:capability-graphql");
            if (Override is { } replacement)
            { var result = await replacement(call, token); if (result is not null) return result; }
            if (call.Uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal))
            {
                if (!peer.Running) throw Refusal();
                return Json("{\"version\":\"2.3.2243\"}");
            }
            return Json(new JsonObject { ["data"] = Schema() }.ToJsonString());
        }
        internal sealed class Handler(Factory owner, SourceTransportPlane plane, int id) : HttpMessageHandler
        {
            internal SourceTransportPlane Plane => plane; internal int Id => id;
            internal bool Closed, UsedAbout;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => owner.Send(this, request, cancellationToken);
            protected override void Dispose(bool disposing)
            {
                if (Closed) return; Closed = true; base.Dispose(disposing);
                if (UsedAbout && owner.CloseFailureAfterAbout is { } failure) throw failure;
            }
        }
    }

    private sealed class Peer : NativeSourceOwnerConnection.ControlledPeer, IDisposable
    {
        private readonly object _gate = new(); private readonly Dictionary<int, Queue<byte>> _inputs = [];
        private readonly HashSet<int> _eof = [], _owned = []; private readonly List<byte> _writing = [];
        private readonly List<NativeSourceOwnerCommandMessage> _commands = []; private readonly byte[] _nonce; private readonly Trace _trace;
        private NativeSourceOwnerCommandMessage? _start, _stop; private bool _reaped, _running; private ulong _generation;
        internal NativeSourceOwnerConnection.BootMessage Boot { get; }
        internal volatile bool HoldStart, HoldStop, UnavailableStart, ExitOnNextStatus, CorruptNextResponse, BadStopAfterFirst;
        internal ulong StatusGenerationDelta; internal IOException? CommandCloseFailure;
        internal readonly TaskCompletionSource StartSeen = Signal();
        internal readonly ManualResetEventSlim CloseGate = new(true), CloseEntered = new();
        internal bool ClosedFinished;
        internal readonly Dictionary<int, int> Closed = [];
        internal bool Running { get { lock (_gate) return _running; } }
        internal ulong Generation { get { lock (_gate) return _generation; } }
        internal void SeedGeneration(ulong generation)
        { lock (_gate) { if (_commands.Count != 0 || _running) throw new InvalidOperationException("Seed is controlled pre-command data only"); _generation = generation; } }
        internal IReadOnlyList<NativeSourceOwnerCommandMessage> Commands { get { lock (_gate) return _commands.ToArray(); } }
        internal Peer(byte[] boot, Trace trace)
        {
            Boot = NativeSourceOwnerConnection.ParseBootMessage(boot); _nonce = Convert.FromHexString(Boot.Nonce); _trace = trace;
            Feed(6, boot); End(6); Feed(5, boot.AsSpan(192 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(boot.AsSpan(96)))).ToArray()); End(5);
        }
        internal override long Milliseconds => Environment.TickCount64;
        internal override void OpenBoot() { lock (_gate) _owned.Add(6); }
        internal override void RequireMainIdentity(uint pid, uint groups, uint kvmGid)
        { Check.Equal(Boot.MainPid, pid); Check.Equal(Boot.GroupCount, groups); Check.Equal(Boot.KvmGid, kvmGid); }
        internal override NativeSourceOwnerConnection.PipeIdentity Inspect(int slot) => Boot.Pipes[slot - 3];
        internal override void AdoptIntegrated() { lock (_gate) foreach (var slot in new[] { 3, 4, 5, 7 }) _owned.Add(slot); }
        internal override int Read(int slot, Span<byte> destination)
        {
            lock (_gate)
            {
                if (!_owned.Contains(slot)) throw new IOException("Controlled read of an unowned pipe");
                if (_inputs.TryGetValue(slot, out var queue) && queue.Count > 0)
                { var count = Math.Min(queue.Count, destination.Length); for (var index = 0; index < count; index++) destination[index] = queue.Dequeue(); return count; }
                return _eof.Contains(slot) ? 0 : -1;
            }
        }
        internal override int Write(ReadOnlySpan<byte> bytes)
        {
            NativeSourceOwnerCommandMessage? command = null;
            lock (_gate)
            {
                if (!_owned.Contains(3)) throw new IOException("Controlled command pipe is closed");
                _writing.AddRange(bytes.ToArray());
                if (_writing.Count >= 56 && _writing.Count == 56 + BinaryPrimitives.ReadUInt32LittleEndian(_writing.ToArray().AsSpan(52)))
                { command = NativeSourceOwnerProtocol.DecodeCommand(_writing.ToArray(), _nonce, (ulong)_commands.Count + 1); _commands.Add(command); _writing.Clear(); }
            }
            if (command is not null) OnCommand(command); return bytes.Length;
        }
        private void OnCommand(NativeSourceOwnerCommandMessage command)
        {
            _trace.Add("native:" + command.Opcode);
            switch (command.Opcode)
            {
                case NativeSourceOwnerOpcode.Start:
                    lock (_gate) { if (!UnavailableStart) _generation++; _start = command; }
                    StartSeen.TrySetResult(); if (!HoldStart) ReleaseStart(); break;
                case NativeSourceOwnerOpcode.Status:
                    lock (_gate) if (ExitOnNextStatus) { ExitOnNextStatus = false; _running = false; }
                    Reply(command, Running ? NativeSourceOwnerState.Running : NativeSourceOwnerState.Idle, generation: Generation + StatusGenerationDelta); break;
                case NativeSourceOwnerOpcode.Stop:
                    lock (_gate) _stop = command;
                    if (!HoldStop) ReleaseStop(); break;
                case NativeSourceOwnerOpcode.Shutdown:
                    ReleaseStart(unavailable: true); lock (_gate) _running = false;
                    Reply(command, NativeSourceOwnerState.Idle); End(4); Reap(); break;
                default: throw new InvalidOperationException("Unexpected controlled native opcode");
            }
        }
        internal void ReleaseStart(bool unavailable = false)
        {
            NativeSourceOwnerCommandMessage? start; lock (_gate) { start = _start; _start = null; unavailable |= UnavailableStart; _running = !unavailable; }
            if (start is not null) Reply(start, unavailable ? NativeSourceOwnerState.Idle : NativeSourceOwnerState.Running,
                unavailable ? NativeSourceOwnerResult.Unavailable : NativeSourceOwnerResult.Ok, diagnostic: unavailable ? NativeSourceOwnerDiagnostic.Asset : NativeSourceOwnerDiagnostic.None);
        }
        internal void ReleaseStop()
        {
            NativeSourceOwnerCommandMessage? stop; lock (_gate) { HoldStop = false; stop = _stop; _stop = null; _running = false; }
            if (stop is not null) Reply(stop, NativeSourceOwnerState.Idle, badFacts: BadStopAfterFirst && Commands.Count(command => command.Opcode == NativeSourceOwnerOpcode.Stop) > 1);
        }
        private void Reply(NativeSourceOwnerCommandMessage command, NativeSourceOwnerState state,
            NativeSourceOwnerResult result = NativeSourceOwnerResult.Ok, ulong? generation = null,
            NativeSourceOwnerDiagnostic diagnostic = NativeSourceOwnerDiagnostic.None, bool badFacts = false)
        {
            var observedGeneration = generation ?? Generation;
            var frame = new byte[120]; "ASOREP1\n"u8.CopyTo(frame); _nonce.CopyTo(frame, 8);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(40), command.RequestId);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(48), (ushort)command.Opcode); BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(50), (ushort)result);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(52), 56); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(56), observedGeneration);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(64), (uint)state);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(68), observedGeneration == 0 ? 0U : 73U);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(72), observedGeneration == 0 ? 0U : 73U);
            var facts = state == NativeSourceOwnerState.Idle ? NativeSourceOwnerFacts.NoChildren | NativeSourceOwnerFacts.StreamsDrained
                | (observedGeneration == 0 ? NativeSourceOwnerFacts.None : NativeSourceOwnerFacts.Exit) : NativeSourceOwnerFacts.None;
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(80), (uint)(badFacts ? facts & ~NativeSourceOwnerFacts.NoChildren : facts));
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(92), (uint)diagnostic);
            BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(104), Boot.ExpectedOwner!.Root.Device); BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(112), Boot.ExpectedOwner.Root.Inode);
            if (CorruptNextResponse) { CorruptNextResponse = false; frame[0] ^= 1; } Feed(4, frame);
        }
        private void Reap()
        {
            lock (_gate) { if (_reaped) return; _reaped = true; }
            var frame = new byte[48]; "ASREAP1\n"u8.CopyTo(frame); _nonce.CopyTo(frame, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(40), Boot.ExpectedOwner!.OwnerPid); Feed(7, frame); End(7);
        }
        private void Feed(int slot, byte[] bytes)
        { lock (_gate) { if (!_inputs.TryGetValue(slot, out var queue)) _inputs[slot] = queue = new(); foreach (var value in bytes) queue.Enqueue(value); } }
        private void End(int slot) { lock (_gate) _eof.Add(slot); }
        internal override void Poll(bool writing, int milliseconds) => Thread.Sleep(Math.Min(5, milliseconds));
        internal override void Close(int slot)
        {
            bool retired; lock (_gate) { retired = _owned.Remove(slot); if (retired) Closed[slot] = Closed.GetValueOrDefault(slot) + 1; }
            if (!retired || slot != 3) return;
            End(4); Reap(); if (CommandCloseFailure is { } failure) throw failure;
        }
        internal override void CloseAll()
        {
            CloseEntered.Set(); if (!CloseGate.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Controlled native close was not released");
            int[] slots; lock (_gate) slots = _owned.ToArray(); var errors = new List<Exception>();
            foreach (var slot in slots) try { Close(slot); } catch (Exception error) { errors.Add(error); }
            ClosedFinished = true; if (errors.Count > 0) throw new AggregateException(errors);
        }
        internal void ReleaseAll() { CloseGate.Set(); HoldStart = HoldStop = false; ReleaseStart(); ReleaseStop(); }
        public void Dispose() { CloseGate.Set(); CloseGate.Dispose(); CloseEntered.Dispose(); }
    }

    /// <summary>Virtual elapsed time only; neither source/native authority nor a delayed-success injection.</summary>
    private sealed class StepClock : TimeProvider
    {
        private readonly object _gate = new(); private readonly List<Timer> _timers = []; private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + TimeSpan.FromTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { lock (_gate) { var timer = new Timer(this, callback, state); _timers.Add(timer); timer.Change(dueTime, period); return timer; } }
        internal bool HasDelay(int seconds) { lock (_gate) return _timers.Any(timer => timer.Active && timer.Delay == TimeSpan.FromSeconds(seconds)); }
        internal void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (_gate)
            {
                _ticks = checked(_ticks + elapsed.Ticks); due = _timers.Where(timer => timer.Active && timer.Due <= _ticks).ToArray();
                foreach (var timer in due) { timer.Active = timer.Period != Timeout.InfiniteTimeSpan; if (timer.Active) timer.Due = checked(_ticks + timer.Period.Ticks); }
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(StepClock owner, TimerCallback callback, object? state) : ITimer
        {
            internal TimerCallback Callback => callback; internal object? State => state;
            internal TimeSpan Delay, Period; internal long Due; internal bool Active;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            { lock (owner._gate) { Delay = dueTime; Period = period; Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : checked(owner._ticks + dueTime.Ticks); Active = true; return true; } }
            public void Dispose() { lock (owner._gate) { Active = false; owner._timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private static byte[] Boot()
    {
        var bytes = File.ReadAllBytes(ConfigurationTests.Fixture("native-bootstrap-binding.json"));
        Check.Equal("98a52a07dda79f7122e40812952f6575c1b13504cf7f760a6c7836253bb960e7", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return Convert.FromHexString(JsonNode.Parse(bytes)!["boots"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "integrated")!["hex"]!.GetValue<string>());
    }
    private static JsonObject Schema() => new() { ["__schema"] = new JsonObject { ["mutationType"] = new JsonObject { ["fields"] = new JsonArray(new[]
        { "updateManga", "enqueueChapterDownloads", "fetchMangaAndChapters", "fetchChapterPages" }.Select(name => (JsonNode)new JsonObject { ["name"] = name }).ToArray()) } } };
    private static HttpRequestException Refusal() => new("Controlled typed loopback refusal", new SocketException((int)SocketError.ConnectionRefused));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) };
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Controlled controller boundary was not reached"); await Task.Delay(1); }
    }
    private static async Task<Exception?> Failure(Task task)
    { try { await task.WaitAsync(TimeSpan.FromSeconds(5)); return null; } catch (Exception error) { return error; } }
    private static IEnumerable<Exception> Tree(Exception error)
    {
        yield return error;
        if (error is AggregateException aggregate) foreach (var child in aggregate.InnerExceptions) foreach (var item in Tree(child)) yield return item;
        else if (error.InnerException is { } inner) foreach (var item in Tree(inner)) yield return item;
    }
}
