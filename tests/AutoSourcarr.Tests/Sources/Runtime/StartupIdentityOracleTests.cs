using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AutoSourcarr.Core.Settings;

namespace AutoSourcarr.Tests;

/// <summary>
/// Exact observations from the original locally pinned Python three-field settings model.
/// Inputs are explicit observed dictionaries. This does not exercise .NET dotenv parsing/
/// interpolation, typed initializer coercion, native handoff/selection or Linux alias order.
/// </summary>
public static class StartupIdentityOracleTests
{
    private const string ReceiptSha256 = "5f7f30623337161c34b79d37950e56d5799b2ca9adc41644022ed89a760c4d39";
    private const string ExtractedFixtureSha256 = "849913aef7a3d73d50b7a3083541aec3889af3d3a449c0d23fdf3ae235c0a931";

    public static void Register(TestSuite suite)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "startup-identities.json")), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement.Clone(); var provenance = root.GetProperty("provenance");
        var rows = root.GetProperty("cases").EnumerateArray().Select(row => row.Clone()).ToArray();
        suite.Add("startup identity original oracle: fixture custody and string-loader-only scope", () =>
        {
            Check.Equal(1, root.GetProperty("schema").GetInt32()); Check.Equal(180, rows.Length);
            Check.Equal(ReceiptSha256, provenance.GetProperty("receipt_sha256").GetString()); Check.Equal(ExtractedFixtureSha256, provenance.GetProperty("extracted_fixture_sha256").GetString());
            var counts = provenance.GetProperty("counts"); Check.Equal(197, counts.GetProperty("source_cases").GetInt32()); Check.Equal(17, counts.GetProperty("excluded_typed_initializers").GetInt32()); Check.Equal(180, counts.GetProperty("string_loader_cases").GetInt32());
            Check.Equal(rows.Count(row => row.GetProperty("expected").GetProperty("accepted").GetBoolean()), counts.GetProperty("accepted").GetInt32());
            Check.Equal(rows.Count(row => !row.GetProperty("expected").GetProperty("accepted").GetBoolean()), counts.GetProperty("rejected").GetInt32());
            Check.Equal(4, counts.GetProperty("omitted_decimals").GetInt32()); Check.Equal(180, rows.Select(row => row.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
            var packages = provenance.GetProperty("parser_packages");
            foreach (var (name, version) in new[] { ("pydantic-settings", "2.10.1"), ("pydantic", "2.14.0"), ("pydantic-core", "2.50.0"), ("python-dotenv", "1.2.4") }) Check.Equal(version, packages.GetProperty(name).GetProperty("version").GetString());
            Check.Equal("win32", provenance.GetProperty("observed_platform").GetString());
            Check.Equal("non-authoritative", provenance.GetProperty("input_order").GetProperty("sorted_worker_dotenv_object_order").GetString());
        });
        foreach (var row in rows)
        {
            var captured = row;
            suite.Add("startup identity original string-loader oracle: " + row.GetProperty("id").GetString(), () => Verify(captured));
        }
    }

    private static void Verify(JsonElement row)
    {
        var expected = row.GetProperty("expected");
        AutoSourcarrSettings Load() => SettingsLoader.Load(dotenv: ReadDictionary(row.GetProperty("dotenv")), environment: ReadDictionary(row.GetProperty("environment")), bundledImage: false);
        if (!expected.GetProperty("accepted").GetBoolean())
        {
            var error = Check.Throws<ConfigurationException>(() => Load());
            var allowed = expected.GetProperty("errors").EnumerateArray().Select(item => "Invalid configuration value for " + item.GetProperty("key").GetString() + ".").ToHashSet(StringComparer.Ordinal);
            Check.True(allowed.Contains(error.Message), "Failure must name only an actually rejected setting key, without its value");
            Check.True(error.InnerException is null, "A configuration failure must not retain an input-bearing inner exception");
            Check.Equal(0, error.Data.Count);
            return;
        }
        var settings = Load();
        Integer(settings.SourceEngineUid, expected.GetProperty("uid")); Integer(settings.SourceEngineGid, expected.GetProperty("gid"));
        Check.Equal(expected.GetProperty("mode").GetString(), settings.SourceEngineMode);
    }

    private static IReadOnlyDictionary<string, string?> ReadDictionary(JsonElement pairs)
    {
        // Ordinal keys preserve distinct case aliases. The supplied list owns
        // enumeration order independently of any Dictionary implementation. The
        // loader owns case-insensitive precedence; no dotenv parser runs here.
        var result = new List<KeyValuePair<string, string?>>();
        foreach (var pair in pairs.EnumerateArray())
        {
            Check.Equal(2, pair.GetArrayLength()); var key = pair[0].GetString() ?? throw new InvalidDataException("Missing original dictionary key");
            var value = pair[1].ValueKind == JsonValueKind.Null ? null : pair[1].GetString() ?? throw new InvalidDataException("Missing original dictionary string");
            result.Add(new(key, value));
        }
        return new OrderedInputs(result);
    }

    private sealed class OrderedInputs : IReadOnlyDictionary<string, string?>
    {
        private readonly KeyValuePair<string, string?>[] _pairs;
        private readonly Dictionary<string, string?> _lookup;
        internal OrderedInputs(IEnumerable<KeyValuePair<string, string?>> pairs)
        { _pairs = pairs.ToArray(); _lookup = _pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal); }
        public string? this[string key] => _lookup[key];
        public IEnumerable<string> Keys => _pairs.Select(pair => pair.Key);
        public IEnumerable<string?> Values => _pairs.Select(pair => pair.Value);
        public int Count => _pairs.Length;
        public bool ContainsKey(string key) => _lookup.ContainsKey(key);
        public bool TryGetValue(string key, out string? value) => _lookup.TryGetValue(key, out value);
        public IEnumerator<KeyValuePair<string, string?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, string?>>)_pairs).GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void Integer(BigInteger actual, JsonElement expected)
    {
        Check.Equal("int", expected.GetProperty("python_type").GetString());
        Check.Equal(expected.GetProperty("sign").GetInt32(), actual.Sign);
        var magnitude = BigInteger.Abs(actual); Check.Equal(expected.GetProperty("bit_length").GetInt64(), magnitude.GetBitLength());
        var bytes = magnitude.ToByteArray(isUnsigned: true, isBigEndian: true);
        Check.Equal(expected.GetProperty("magnitude_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(bytes)));
        var decimalValue = expected.GetProperty("decimal");
        if (decimalValue.ValueKind != JsonValueKind.Null)
        {
            // The expected decimal is already an actual normalized original
            // observation. This is not a grammar implementation. Huge omitted
            // decimals are never reconstructed or formatted, even on failure.
            Check.True(actual == BigInteger.Parse(decimalValue.GetString()!, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), "Exact requested integer differs from the original decimal observation");
        }
    }
}
