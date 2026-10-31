using AutoSourcarr.Core.Concurrency;

namespace AutoSourcarr.Core.Sources;

/// <summary>Owns logical scopes, every physical request/response and all pools that may contact one engine incarnation.</summary>
internal sealed class SourceTransportEpoch(HttpClient http, Func<SourceTransportPlane, HttpClient> allocate)
{
    private readonly OperationOwner _owner = new();
    private readonly object _gate = new();
    private readonly HashSet<OwnedUpstreamResponse> _responses = [];
    private readonly Dictionary<object, HttpClient> _about = [];
    private readonly List<Exception> _errors = [];
    private readonly object _defaultAbout = new();
    private int _omittedErrors;
    private int _retired;
    private Task? _close;
    public HttpClient Http { get; } = http;
    public CancellationToken CancellationToken => _owner.CancellationToken;
    public bool IsRetired => Volatile.Read(ref _retired) != 0;

    public void Fence() { lock (_gate) Interlocked.Exchange(ref _retired, 1); }
    public IDisposable Enter()
    {
        lock (_gate) { ThrowIfRetired(); return _owner.Enter(); }
    }
    public void ThrowIfRetired()
    { if (IsRetired) throw new OperationCanceledException(CancellationToken); }

    public HttpClient About(SourceAboutTransportRegistration? registration)
    {
        var key = (object?)registration ?? _defaultAbout;
        lock (_gate)
        {
            ThrowIfRetired();
            if (_about.TryGetValue(key, out var current)) return current;
        }
        // Controlled factories and physical disposal can call arbitrary code. Neither runs under admission locks.
        var created = registration is null ? allocate(SourceTransportPlane.CapabilityAbout) : registration.Client(this);
        HttpClient? selected;
        lock (_gate)
        {
            if (!_about.TryGetValue(key, out selected) && !IsRetired) _about.Add(key, selected = created);
        }
        if (!ReferenceEquals(selected, created))
            try { created.Dispose(); } catch (Exception error) { Retain(error); throw; }
        ThrowIfRetired(); return selected ?? throw new OperationCanceledException(CancellationToken);
    }

    public bool Register(OwnedUpstreamResponse response)
    {
        lock (_gate) { _responses.Add(response); return !IsRetired; }
    }
    public void Released(OwnedUpstreamResponse response, Exception? failure)
    {
        lock (_gate) { _responses.Remove(response); if (failure is not null) RetainCore(failure); }
    }
    public void Retain(Exception failure) { lock (_gate) RetainCore(failure); }
    private void RetainCore(Exception failure) { if (_errors.Count < 8) _errors.Add(failure); else _omittedErrors++; }

    public Task CloseAsync()
    {
        lock (_gate) { Fence(); return _close ??= CloseCoreAsync(); }
    }
    private async Task CloseCoreAsync()
    {
        // Neither caller admission nor BeginPause can run arbitrary physical disposers while holding their locks.
        await Task.Yield();
        var drain = _owner.StopAsync();
        OwnedUpstreamResponse[] responses; HttpClient[] clients;
        lock (_gate) { responses = _responses.ToArray(); clients = _about.Values.Prepend(Http).ToArray(); }
        foreach (var response in responses) response.Dispose(); // Initiate all closes before joining any blocked close.
        foreach (var client in clients) try { client.Dispose(); } catch (Exception error) { Retain(error); }
        try { await drain.ConfigureAwait(false); } catch (Exception error) { Retain(error); }
        try { await _owner.DisposeAsync().ConfigureAwait(false); } catch (Exception) { /* The same retained Stop outcome. */ }
        Exception[] errors; int omitted;
        lock (_gate) { errors = _errors.ToArray(); omitted = _omittedErrors; }
        if (errors.Length != 0 || omitted != 0)
            throw new AggregateException("Source transport retirement failed", errors.Concat(omitted == 0 ? [] : new Exception[] { new IOException($"{omitted} additional transport cleanup failures occurred.") }));
    }
}
