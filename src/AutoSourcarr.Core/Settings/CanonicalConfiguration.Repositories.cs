using System.Text.Json.Nodes;
using AutoSourcarr.Core.Sources.Repositories;

namespace AutoSourcarr.Core.Settings;

public sealed partial class CanonicalConfiguration
{
    internal JsonObject RepositorySnapshot()
    {
        lock (_gate)
        {
            _ = RecoverCore();
            return SourceRepositoryStore.Parse(RepositoryBytes());
        }
    }

    internal JsonObject ChangeRepository(string url, string revision, bool remove)
    {
        lock (_gate)
        {
            // A pending setup rollback is reconciled before comparing the actual env bytes.
            // All future setup/env editors must retain this same canonical path-keyed gate.
            _ = RecoverCore();
            var after = SourceRepositoryStore.Edit(RepositoryBytes(), url, revision, remove);
            _files.Write("source-host.env", after, preserveSourceHostMode: true);
            var snapshot = SourceRepositoryStore.Parse(RepositoryBytes());
            snapshot["restart_required"] = true;
            return snapshot;
        }
    }

    private byte[] RepositoryBytes() => _files.Read("source-host.env")
        ?? throw new FileNotFoundException("Private source host settings file is not mounted");
}
