using System.Diagnostics;
using System.Reflection;
using System.Text;
using AutoSourcarr.Core.Serialization;
using AutoSourcarr.Core.Persistence;

namespace AutoSourcarr.Tests;

public static class PrivateDocumentTests
{
    private static readonly string Name = new string('a', 64) + ".json";
    public static void Register(TestSuite suite)
    {
        suite.Add("private documents: bounded atomic bytes survive reopen and reject path traversal", () =>
        {
            using var temp = new Sandbox(); var store = new PrivateDocumentStore(temp.Path);
            store.Locked(Name, document => { Check.True(document.Read(1024) is null); document.Write(Encoding.UTF8.GetBytes("{\"source\":\"源\"}")); return 0; });
            var reopened = new PrivateDocumentStore(temp.Path);
            Check.Equal("{\"source\":\"源\"}", reopened.Locked(Name, document => Encoding.UTF8.GetString(document.Read(1024)!)));
            Check.Throws<ArgumentException>(() => store.Locked("../config.json", _ => 0));
            Check.Throws<InvalidDataException>(() => store.Locked(Name, document => document.Read(1)));
            Check.Equal(1, store.JsonDocuments(4096).Count);
        });
        suite.Add("private documents: scopes cannot escape their lock or accept asynchronous callbacks", () =>
        {
            using var temp = new Sandbox(); var store = new PrivateDocumentStore(temp.Path); IPrivateDocument? escaped = null;
            store.Locked(Name, document => { escaped = document; return 0; });
            Check.Throws<ObjectDisposedException>(() => escaped!.Read(1024));
            Check.Throws<InvalidOperationException>(() => store.Locked(Name, _ => Task.FromResult(1)));
        });
        suite.Add("private documents: cross-process locks exclude another writer and release after completion", () =>
        {
            using var temp = new Sandbox(); var store = new PrivateDocumentStore(temp.Path);
            store.Locked(Name, document => { Check.Equal(75, Child(temp.Path, Name)); document.Write("{}"u8.ToArray()); return 0; });
            Check.Equal(0, Child(temp.Path, Name));
            Check.Equal("{}", store.Locked(Name, document => Encoding.UTF8.GetString(document.Read(1024)!)));
        });
        suite.Add("private documents: a special or oversized destination cannot be replaced", () =>
        {
            using var temp = new Sandbox(); var store = new PrivateDocumentStore(temp.Path);
            Directory.CreateDirectory(Path.Combine(temp.Path, Name));
            Check.Throws<IOException>(() => store.Locked(Name, document => { document.Write("{}"u8.ToArray()); return 0; }));
            Check.True(Directory.Exists(Path.Combine(temp.Path, Name)));
        });
    }
    public static int Probe(string root, string name)
    {
        var parent = Path.GetFullPath(Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_ROOT") ?? Path.GetTempPath());
        var path = Path.GetFullPath(root); var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison)
            || !Path.GetFileName(path).StartsWith("autosourcarr-dotnet-test-", StringComparison.Ordinal)) return 76;
        try { return new PrivateDocumentStore(path).Locked(name, _ => 0, TimeSpan.FromMilliseconds(100)); }
        catch (IOException) { return 75; }
    }
    private static int Child(string root, string name)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Signed .NET host is unavailable"))
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); info.ArgumentList.Add("--private-document-probe"); info.ArgumentList.Add(root); info.ArgumentList.Add(name);
        var testRoot = Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_ROOT") ?? Path.GetTempPath();
        info.Environment.Clear(); info.Environment["AUTOSOURCARR_TEST_ROOT"] = testRoot;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Cannot start the local lock probe");
        if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: true); process.WaitForExit(); throw new TimeoutException("Local file-lock probe did not finish"); }
        var error = process.StandardError.ReadToEnd();
        Check.True(process.ExitCode is 0 or 75, "Unexpected lock-probe exit: " + process.ExitCode + " " + error);
        return process.ExitCode;
    }
}
