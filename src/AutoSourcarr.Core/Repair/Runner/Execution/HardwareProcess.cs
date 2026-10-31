using System.Diagnostics;
using System.Runtime.CompilerServices;
using AutoSourcarr.Core.Repair.Runner.Isolation;

[assembly: InternalsVisibleTo("AutoSourcarr.Tests")]

namespace AutoSourcarr.Core.Repair.Runner.Execution;

internal interface IHardwareProcess : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    Task WaitForExitAsync();
    void Kill();
}
internal interface IHardwareProcessFactory { IHardwareProcess Start(VmLaunchPlan plan); }

internal interface IBlockingHardwareChild : IDisposable
{
    int Id { get; }
    bool HasExited { get; }
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    void WaitForExit();
    void Kill();
}

/// <summary>Linux PDEATHSIG follows the creating thread. This dedicated thread is never returned
/// to a pool: it creates the child and remains in its physical wait until that child is gone.</summary>
internal sealed class DedicatedHardwareProcess : IHardwareProcess
{
    private readonly TaskCompletionSource<IBlockingHardwareChild> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _recheck = new(false);
    private readonly Thread _creator;
    private Exception? _waitFailure;
    private int _physicalExit;
    private int _disposed;
    private DedicatedHardwareProcess(Func<IBlockingHardwareChild> start, Action<Thread>? observe)
    {
        _creator = new Thread(() => CreateAndWait(start)) { IsBackground = true, Name = "AutoSourcarr hardware creator" };
        observe?.Invoke(_creator);
    }
    internal static IHardwareProcess Start(Func<IBlockingHardwareChild> start, Action<Thread>? observe = null)
    {
        var owned = new DedicatedHardwareProcess(start, observe);
        var launched = false;
        try
        {
            owned._creator.Start(); launched = true; _ = owned._started.Task.GetAwaiter().GetResult(); return owned;
        }
        catch
        {
            if (launched)
            { owned._finished.Task.GetAwaiter().GetResult(); owned._creator.Join(); }
            owned._recheck.Dispose(); throw;
        }
    }
    private IBlockingHardwareChild Child => _started.Task.GetAwaiter().GetResult();
    public int Id => Child.Id;
    public bool HasExited => Volatile.Read(ref _physicalExit) != 0 || Child.HasExited;
    public Stream Input => Child.Input;
    public Stream Output => Child.Output;
    public Stream Error => Child.Error;
    private void CreateAndWait(Func<IBlockingHardwareChild> start)
    {
        IBlockingHardwareChild? child = null;
        try
        {
            child = start(); _started.TrySetResult(child);
            while (true)
            {
                try
                {
                    if (child.HasExited) { Volatile.Write(ref _physicalExit, 1); break; }
                    child.WaitForExit();
                }
                catch (Exception error)
                {
                    _waitFailure ??= error;
                    try { child.Kill(); child.WaitForExit(); } catch (Exception killError) { _waitFailure = new AggregateException(_waitFailure, killError); }
                    // Never retire a creator thread while its PID might be alive. If the
                    // native wait/kill failed, another owned Kill can wake a recheck.
                    var exited = false;
                    try { exited = child.HasExited; } catch (Exception stateError) { _waitFailure = new AggregateException(_waitFailure, stateError); }
                    if (exited) { Volatile.Write(ref _physicalExit, 1); break; }
                    _recheck.Wait(); _recheck.Reset();
                }
            }
        }
        catch (Exception error) when (child is null) { _started.TrySetException(error); }
        finally { _finished.TrySetResult(); }
    }
    public async Task WaitForExitAsync()
    {
        await _finished.Task.ConfigureAwait(false); _creator.Join();
        if (_waitFailure is not null) throw new IOException("The dedicated hardware creator retained a wait failure", _waitFailure);
    }
    public void Kill()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { Child.Kill(); } finally { _recheck.Set(); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _finished.Task.GetAwaiter().GetResult(); _creator.Join();
        if (Volatile.Read(ref _physicalExit) == 0) throw new InvalidOperationException("The creating thread did not establish physical child exit");
        try { Child.Dispose(); } finally { _recheck.Dispose(); }
    }
}

internal sealed class FixedHardwareProcessFactory : IHardwareProcessFactory
{
    public IHardwareProcess Start(VmLaunchPlan plan)
    {
        if (!OperatingSystem.IsLinux() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64 ||
            plan.Executable != "/opt/autosourcarr-native/repair-launcher") throw new PlatformNotSupportedException("Hardware repair requires the fixed Linux x64 launcher");
        // Runtime's Task.Run may wait for this handoff, but is never the Linux creator.
        var start = plan.StartInfo();
        return DedicatedHardwareProcess.Start(() =>
        {
            var process = new Process { StartInfo = start, EnableRaisingEvents = true }; var child = new OwnedChild(process); var started = false;
            try
            {
                if (!process.Start()) throw new IOException("The fixed hardware repair launcher did not start");
                started = true; return child;
            }
            catch
            {
                if (started) { if (!process.HasExited) process.Kill(entireProcessTree: true); process.WaitForExit(); }
                process.Dispose(); throw;
            }
        });
    }
    private sealed class OwnedChild(Process process) : IBlockingHardwareChild
    {
        public int Id => process.Id;
        public bool HasExited => process.HasExited;
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public Stream Error => process.StandardError.BaseStream;
        public void WaitForExit() => process.WaitForExit();
        public void Kill()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            catch (System.ComponentModel.Win32Exception) when (process.HasExited) { }
        }
        public void Dispose() => process.Dispose();
    }
}
