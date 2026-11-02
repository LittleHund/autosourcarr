using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutoSourcarr.Api;
using AutoSourcarr.Core.Persistence;
using AutoSourcarr.Core.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutoSourcarr.Tests.Api;

/// <summary>
/// Phase 11 browser tests (tests/e2e): serves the real Web UI from the controlled external-mode runtime on a
/// loopback port, with an owner account and finished setup, until the process is stopped. No source engine,
/// no native launcher and no Suwayomi; source calls fail as "engine unavailable", which the UI must show calmly.
/// Usage: --serve-ui DATA_ROOT PORT OWNER_JSON [--static DIR] [--bundle DIST_DIR] [--empty] [--setup-pending]
/// </summary>
internal static class UiTestHost
{
    public static int Run(string[] args)
    {
        if (args.Length < 3 || !int.TryParse(args[1], out var port) || port is < 1024 or > 65535)
        {
            Console.Error.WriteLine("Usage: AutoSourcarr.Tests --serve-ui DATA_ROOT PORT OWNER_JSON [--static DIR] [--bundle DIST_DIR] [--empty] [--setup-pending]");
            return 2;
        }
        string? Option(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        try { return RunAsync(Path.GetFullPath(args[0]), port, args[2], Option("--static"), Option("--bundle"), args.Contains("--empty"), args.Contains("--setup-pending")).GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            Console.Error.WriteLine("UI test host failed: " + error);
            return 1;
        }
    }

    private static async Task<int> RunAsync(string root, int port, string ownerFile, string? staticDir, string? bundle, bool empty, bool setupPending)
    {
        // The data root is a scratch folder owned by the test run; it is recreated on every start.
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        using var owner = JsonDocument.Parse(File.ReadAllText(ownerFile));
        var name = owner.RootElement.GetProperty("username").GetString()!;
        var password = owner.RootElement.GetProperty("password").GetString()!;
        var adminToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        var settings = new AutoSourcarrSettings
        {
            DataDir = Path.Combine(root, "data"), SettingsConfigDir = Path.Combine(root, "config"), SourceEngineMode = "external",
            SourceSuwayomiUrl = "http://127.0.0.1:1", AdminToken = adminToken, AutosourcarrReaderApiToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
            AuthMethod = "forms"
        };
        if (!empty) Seed(settings);

        var assets = Path.GetFullPath(staticDir ?? Path.Combine("autosourcarr", "app", "static"));
        if (!File.Exists(Path.Combine(assets, "index.html"))) throw new FileNotFoundException("No index.html in the static folder.", assets);
        if (bundle is not null)
        {
            // The image serves dist/index.html as the page (phase 13); everything else stays where it is.
            var copy = Path.Combine(root, "static");
            Copy(assets, copy);
            File.Copy(Path.Combine(Path.GetFullPath(bundle), "index.html"), Path.Combine(copy, "index.html"), overwrite: true);
            assets = copy;
        }

        await using var runtime = await ApplicationRuntime.CreateExternalForTestsAsync(settings, settings.SettingsConfigDir);
        runtime.Access!.Accounts.CreateOwner(name, password);
        using var host = new HostBuilder().UseConsoleLifetime()
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureServices(services => services.AddRouting())
            .ConfigureWebHost(web => web.UseKestrel(options => { options.AddServerHeader = false; options.Listen(IPAddress.Loopback, port); })
                .Configure(app => runtime.ConfigureHttp(app, assets))).Build();
        await host.StartAsync();
        var address = "http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!setupPending) await FinishSetup(address, adminToken);
        Console.WriteLine($"UI test host listening on {address} (static {assets}, catalogue {(empty ? "empty" : "fixture")}, setup {(setupPending ? "pending" : "finished")})");
        await host.WaitForShutdownAsync();
        await runtime.BeginHttpStopAsync();
        return 0;
    }

    /// <summary>The released 0.4.4 fixture catalogue (one manual entry, a source group and its match), migrated to the current schema.</summary>
    private static void Seed(AutoSourcarrSettings settings)
    {
        using var database = new SqliteDatabase(settings);
        var migrator = new SchemaMigrator(database);
        migrator.Initialize();
        foreach (var schema in new[] { "catalogue-identity.sql", "gallery-policy.sql", "identity-review.sql" })
            migrator.EnsureFeatureSchema(schema);
        database.Write(DatabaseFile.Catalog, session => session.Execute(File.ReadAllText(AutoSourcarr.Tests.ConfigurationTests.Fixture("catalog-seed.sql"))));
        database.Write(DatabaseFile.Origins, session => session.Execute(File.ReadAllText(AutoSourcarr.Tests.ConfigurationTests.Fixture("origins-seed.sql"))));
        migrator.Initialize();
    }

    /// <summary>Completes setup without changing a setting, the same request the wizard's Finish sends.</summary>
    private static async Task FinishSetup(string address, string adminToken)
    {
        using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Add("X-Admin-Token", adminToken);
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-origin");
        var setup = JsonNode.Parse(await client.GetStringAsync("/api/v1/setup"))!;
        var body = new JsonObject { ["revision"] = setup["revision"]!.GetValue<string>(), ["transaction"] = Guid.NewGuid().ToString(), ["changes"] = new JsonObject() };
        using var response = await client.PostAsync("/api/v1/setup/finish", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Finishing setup failed: HTTP " + (int)response.StatusCode + " " + await response.Content.ReadAsStringAsync());
    }

    private static void Copy(string from, string to)
    {
        foreach (var directory in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }
}
