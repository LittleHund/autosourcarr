using AutoSourcarr.Core.Concurrency;
using System.Runtime.CompilerServices;

namespace AutoSourcarr.Core.Sources;

internal sealed class SourceTransportManager
{
    private readonly object _gate = new(), _allocationGate = new();
    private readonly SuwayomiClient _client;
    private readonly OperationOwner _terminal;
    private readonly ISourceTransportFactory _factory;
    private readonly ConditionalWeakTable<HttpMessageHandler, object> _usedHandlers = new();
    private readonly List<SourceTransportEpoch> _epochs = [];
    private readonly bool _oneShot;
    private SourceTransportEpoch? _current;
    private readonly SourceTransportEpoch _assets;
    private SourceTransportPause? _pause;
    private Task? _resume, _close;
    private Exception? _fault;
    private bool _closing;

    public SourceTransportManager(SuwayomiClient client, OperationOwner terminal, ISourceTransportFactory factory, HttpMessageHandler? oneShot)
    {
        _client = client; _terminal = terminal; _factory = factory; _oneShot = oneShot is not null;
        var engine = oneShot is null ? Allocate(SourceTransportPlane.Engine) : new HttpClient(oneShot, true) { Timeout = Timeout.InfiniteTimeSpan };
        HttpClient assets;
        try
        {
            // Old handler injection is deliberately a single nonrestartable test transport. It never claims isolated pools.
            assets = _oneShot ? engine : Allocate(SourceTransportPlane.Assets);
        }
        catch (Exception primary)
        {
            try { engine.Dispose(); } catch (Exception cleanup) { throw new AggregateException(primary, cleanup); }
            throw;
        }
        var initial = new SourceTransportEpoch(engine, Allocate); _current = initial; _epochs.Add(initial);
        _assets = _oneShot ? initial : new(assets, Allocate);
        if (!_oneShot) _epochs.Add(_assets);
    }

    private HttpClient Allocate(SourceTransportPlane plane)
    {
        var handler = _factory.Create(plane) ?? throw new InvalidOperationException("Transport factory returned no owned handler.");
        lock (_allocationGate)
        {
            if (_usedHandlers.TryGetValue(handler, out _))
            {
                var error = new ReusedSourceTransportException(); Fault(error); throw error;
            }
            _usedHandlers.Add(handler, new object());
        }
        try { return new(handler, true) { Timeout = Timeout.InfiniteTimeSpan }; }
        catch (Exception primary)
        {
            try { handler.Dispose(); } catch (Exception cleanup) { var error = new AggregateException(primary, cleanup); Fault(error); throw error; }
            throw;
        }
    }

    public SourceTransportSession Open(CancellationToken lifetime, bool asset)
    {
        lifetime.ThrowIfCancellationRequested();
        lock (_gate)
        {
            TerminalCheck();
            var epoch = asset ? null : Current(); var plane = epoch ?? _assets;
            var terminal = _terminal.Enter(); IDisposable? lease = null;
            try
            {
                lease = plane.Enter();
                return new(_client, this, plane, epoch, _current, terminal, lease, lifetime);
            }
            catch { lease?.Dispose(); terminal.Dispose(); throw; }
        }
    }

    public SourceTransportEpoch BindEngine(SourceTransportSession session, SourceTransportEpoch? stamp)
    {
        lock (_gate)
        {
            TerminalCheck();
            if (_fault is not null || stamp is null || !ReferenceEquals(_current, stamp) || stamp.IsRetired)
                throw new SourceEngineUnavailableException("The captured engine transport retired; retry this image operation.");
            return session.BindInsideManager(stamp);
        }
    }

    public void Publish(SourceTransportSession session, OwnedUpstreamResponse response)
    {
        lock (_gate)
        {
            TerminalCheck(); session.ThrowIfRetired();
            session.PublishInsideManager(response);
        }
    }

    public SourceTransportPause BeginPause()
    {
        lock (_gate)
        {
            TerminalCheck();
            if (_oneShot) throw new NotSupportedException("A single injected handler is terminal-only. Supply a fresh transport factory for pause/resume.");
            if (_pause is not null) return _pause;
            var epoch = Current(); epoch.Fence(); _current = null;
            var pause = new SourceTransportPause(this, epoch); _pause = pause; pause.Work = DrainCoreAsync(pause); Observe(pause.Work); return pause;
        }
    }
    public Task<SourceTransportDrain> DrainAsync(SourceTransportPause pause, CancellationToken waiter)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(pause.Owner, this)) throw new ArgumentException("Pause belongs to a different client.", nameof(pause));
            return waiter.CanBeCanceled ? pause.Work.WaitAsync(waiter) : pause.Work;
        }
    }
    private async Task<SourceTransportDrain> DrainCoreAsync(SourceTransportPause pause)
    {
        await Task.Yield();
        try
        {
            await pause.Epoch.CloseAsync().ConfigureAwait(false);
            lock (_gate) { TerminalCheck(); _epochs.Remove(pause.Epoch); return pause.Drain = new(pause); }
        }
        catch (Exception error) { lock (_gate) if (error is not OperationCanceledException) _fault ??= error; throw; }
    }
    public Task ResumeAsync(SourceTransportDrain drain, CancellationToken waiter)
    {
        Task work;
        lock (_gate)
        {
            TerminalCheck();
            if (_fault is not null) throw new InvalidOperationException("Failed transport cleanup forbids resume.");
            if (drain.Consumed || !ReferenceEquals(drain.Pause, _pause) || !ReferenceEquals(_pause?.Drain, drain))
                throw new InvalidOperationException("This transport drain is stale or was already consumed.");
            if (_resume is null || _resume.IsCompleted) { _resume = ResumeCoreAsync(drain); Observe(_resume); }
            work = _resume;
        }
        return waiter.CanBeCanceled ? work.WaitAsync(waiter) : work;
    }
    private async Task ResumeCoreAsync(SourceTransportDrain drain)
    {
        await Task.Yield(); SourceTransportEpoch? staged = null;
        try
        {
            staged = new(Allocate(SourceTransportPlane.Engine), Allocate);
            bool terminal;
            lock (_gate)
            {
                _epochs.Add(staged); terminal = _closing || _terminal.IsStopping;
                if (!terminal)
                {
                    if (!ReferenceEquals(_pause?.Drain, drain)) throw new InvalidOperationException("Transport transition changed during creation.");
                    _current = staged; drain.Consumed = true; _pause = null;
                }
            }
            if (terminal) { await staged.CloseAsync().ConfigureAwait(false); throw new OperationCanceledException(_terminal.CancellationToken); }
        }
        catch (Exception error)
        {
            if (staged is not null && !ReferenceEquals(staged, _current))
                try { await staged.CloseAsync().ConfigureAwait(false); } catch (Exception cleanup) { lock (_gate) _fault ??= cleanup; throw new AggregateException(error, cleanup); }
            if (error is ReusedSourceTransportException or AggregateException) lock (_gate) _fault ??= error;
            throw;
        }
    }

    public Task CloseAsync()
    {
        lock (_gate)
        {
            if (_close is not null) return _close;
            _closing = true; _current = null; foreach (var epoch in _epochs) epoch.Fence();
            return _close = CloseCoreAsync();
        }
    }
    private async Task CloseCoreAsync()
    {
        await Task.Yield(); Task? resume, pause; SourceTransportEpoch[] epochs;
        lock (_gate) { resume = _resume; pause = _pause?.Work; epochs = _epochs.ToArray(); }
        foreach (var epoch in epochs) _ = epoch.CloseAsync();
        var errors = new List<Exception>();
        if (resume is not null) try { await resume.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception error) { errors.Add(error); }
        if (pause is not null) try { await pause.ConfigureAwait(false); } catch (OperationCanceledException) { } catch (Exception) { /* The same failed epoch is joined below. */ }
        lock (_gate) epochs = _epochs.ToArray();
        foreach (var epoch in epochs.Distinct()) try { await epoch.CloseAsync().ConfigureAwait(false); } catch (Exception error) { errors.Add(error); }
        lock (_gate) if (_fault is not null) errors.Add(_fault);
        if (errors.Count > 0) throw new AggregateException("Source transport shutdown failed", errors);
    }
    private SourceTransportEpoch Current()
    {
        if (_fault is not null) throw new SourceEngineUnavailableException("Source transport cleanup is unresolved; native evidence remains unknown.");
        return _current ?? throw new SourceEngineUnavailableException("Source engine transport is paused; retry shortly.");
    }
    private void TerminalCheck() { if (_closing || _terminal.IsStopping) throw new OperationCanceledException(_terminal.CancellationToken); }
    private void Fault(Exception error)
    {
        SourceTransportEpoch? retired;
        lock (_gate) { _fault ??= error; retired = _current; _current = null; retired?.Fence(); }
        if (retired is not null) Observe(retired.CloseAsync());
    }
    private static void Observe(Task work) => _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private sealed class ReusedSourceTransportException() : InvalidOperationException("A transport factory reused an earlier handler/pool; resume is forbidden.");
}
