using System.Globalization;
using System.Text.Json;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

public static class StartupIdentityTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("startup identity: released OS identity string coercion preserves exact wide integers", () =>
        {
            // Observed by the actual three-field pydantic model, not a transcription of this parser.
            var vectors = new (string Input, string Expected)[]
            {
                ("0", "0"), ("-0", "0"), ("+0", "0"), ("000568", "568"), ("+568", "568"),
                (" 568 ", "568"), ("\t568\n", "568"), ("-1", "-1"), ("568.0", "568"),
                ("568.000", "568"), ("-1.0", "-1"), ("1_000", "1000"), ("5_6_8", "568"),
                ("5_6_8.0", "568"), ("568." + new string('0', 32), "568"),
                ("\v568\f", "568"), ("\t568.0\r\n", "568"), ("\u00a0568\u00a0", "568"),
                ("\u2003568\u2003", "568"), ("\u202f568\u202f", "568"), ("\u3000568\u3000", "568"),
                ("2147483648", "2147483648"), ("4294967294", "4294967294"),
                ("-2147483649", "-2147483649"), ("18446744073709551616", "18446744073709551616")
            };
            foreach (var key in new[] { "SOURCE_ENGINE_UID", "SOURCE_ENGINE_GID" })
                foreach (var (input, expected) in vectors)
                    Check.Equal(expected, Value(Load(key, input), key));
        });
        suite.Add("startup identity: original exponent, malformed separator and non-ASCII integer refusals", () =>
        {
            foreach (var key in new[] { "SOURCE_ENGINE_UID", "SOURCE_ENGINE_GID" })
                foreach (var input in new[] { "", "bad", "568.1", "5.68e2", "568e0", "1e3", "1e-3", "0e4000", "1e10000", "_568", "568_", "0x238", "٥٦٨", "５６８", "५६८", "NaN", "Infinity", "-Infinity" })
                    Check.Throws<ConfigurationException>(() => Load(key, input));
            foreach (var input in new[] { "568.0_0", "5_68.00_0", "568_.0", "568._0", "568.0_", "568.0__0", "568.", ".0", "+568.", "-568.", "+.0", "-.0", "+", "\u200b568\u200b", "\ufeff568\ufeff", "  +5_6_8.0_0  ", "568." + new string('0', 32) + "1" })
                Check.Throws<ConfigurationException>(() => Load("SOURCE_ENGINE_UID", input));
        });
        suite.Add("startup identity: JSON and environment retain numeric identity without narrowing", () =>
        {
            var settings = SettingsLoader.Load(environment: new Dictionary<string, string?>
            { ["SOURCE_ENGINE_UID"] = "4294967294", ["SOURCE_ENGINE_GID"] = "18446744073709551616" });
            var document = JsonSerializer.SerializeToElement(settings);
            Check.Equal(JsonValueKind.Number, document.GetProperty("source_engine_uid").ValueKind);
            Check.Equal("4294967294", document.GetProperty("source_engine_uid").GetRawText());
            Check.Equal("18446744073709551616", document.GetProperty("source_engine_gid").GetRawText());
            var restored = JsonSerializer.Deserialize<AutoSourcarrSettings>(document.GetRawText())!;
            Check.Equal(Value(settings, "SOURCE_ENGINE_UID"), Value(restored, "SOURCE_ENGINE_UID"));
            Check.Equal(Value(settings, "SOURCE_ENGINE_GID"), Value(restored, "SOURCE_ENGINE_GID"));
            var environment = SettingsLoader.ToEnvironment(restored);
            Check.Equal("4294967294", environment["SOURCE_ENGINE_UID"]);
            Check.Equal("18446744073709551616", environment["SOURCE_ENGINE_GID"]);
        });
        suite.Add("startup identity: local parser size observations never truncate a requested ID", () =>
        {
            var accepted = new string('9', 4300);
            Check.Equal(accepted, Value(Load("SOURCE_ENGINE_UID", accepted), "SOURCE_ENGINE_UID"));
            Check.Throws<ConfigurationException>(() => Load("SOURCE_ENGINE_UID", accepted + "9"));
        });
        suite.Add("startup identity: precedence and external unused values preserve the actual request", () =>
        {
            var settings = SettingsLoader.Load(
                dotenv: new Dictionary<string, string?> { ["SOURCE_ENGINE_UID"] = "1000", ["SOURCE_ENGINE_GID"] = "2000" },
                environment: new Dictionary<string, string?> { ["source_engine_uid"] = "4294967294", ["SOURCE_ENGINE_MODE"] = "external" });
            Check.Equal("4294967294", Value(settings, "SOURCE_ENGINE_UID"));
            Check.Equal("2000", Value(settings, "SOURCE_ENGINE_GID"));
            Check.Equal("external", settings.SourceEngineMode);
            // Model acceptance is distinct from any later owned-child native width/positive-ID check.
            Check.Equal("-1", Value(Load("SOURCE_ENGINE_UID", "-1"), "SOURCE_ENGINE_UID"));
            var overridden = SettingsLoader.Load(environment: new Dictionary<string, string?> { ["SOURCE_ENGINE_UID"] = "invalid" },
                managedOverrides: new Dictionary<string, string?> { ["SOURCE_ENGINE_UID"] = "4294967294" });
            Check.Equal("4294967294", Value(overridden, "SOURCE_ENGINE_UID"));
        });
        suite.Add("startup identity: errors never expose a failed requested value", () =>
        {
            const string input = "synthetic-private-value-pasted-in-wrong-field";
            var error = Check.Throws<ConfigurationException>(() => Load("SOURCE_ENGINE_UID", input));
            Check.True(!error.ToString().Contains(input, StringComparison.Ordinal));
            Check.True(error.Message.Contains("SOURCE_ENGINE_UID", StringComparison.Ordinal));
        });
    }

    private static AutoSourcarrSettings Load(string key, string value) => SettingsLoader.Load(environment: new Dictionary<string, string?> { [key] = value });
    private static string Value(AutoSourcarrSettings settings, string key) => key == "SOURCE_ENGINE_UID"
        ? settings.SourceEngineUid.ToString(CultureInfo.InvariantCulture) : settings.SourceEngineGid.ToString(CultureInfo.InvariantCulture);
}
