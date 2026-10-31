using System.Diagnostics;
using System.Text.Json;

namespace AutoSourcarr.Tests;

public sealed class TestSuite
{
    private readonly List<(string Name, Action Run)> _tests = [];
    public void Add(string name, Action run) => _tests.Add((name, run));

    public int Run(IReadOnlyList<string>? filters = null)
    {
        filters ??= [];
        if (filters.Any(filter => string.IsNullOrWhiteSpace(filter)
            || !_tests.Any(test => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
        {
            Console.Error.WriteLine("Each --filter must match at least one registered test name.");
            return 2;
        }
        var selected = _tests.Where(test => filters.Count == 0
            || filters.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
        var outcomes = new List<object>();
        var failures = 0;
        foreach (var (name, run) in selected)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                run();
                Console.WriteLine("PASS " + name);
                outcomes.Add(new { name, passed = true, elapsedMs = watch.ElapsedMilliseconds });
            }
            catch (Exception error)
            {
                failures++;
                Console.WriteLine("FAIL " + name + "\n" + error);
                if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
                {
                    // Annotations are readable through the API even where the raw log is not.
                    var detail = string.Join(" | ", error.ToString().Split('\n').Take(12).Select(line => line.Trim()));
                    Console.WriteLine("::error title=" + Escape(name) + "::" + Escape(detail));
                }
                outcomes.Add(new { name, passed = false, elapsedMs = watch.ElapsedMilliseconds, error = error.ToString() });
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { total = selected.Length, passed = selected.Length - failures,
            failed = failures, registered = _tests.Count, filtered = filters.Count > 0, filters, outcomes }));
        return failures == 0 ? 0 : 1;
    }

    private static string Escape(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "%0A", StringComparison.Ordinal).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
}

public static class Check
{
    public static void True(bool value, string message = "Condition failed")
    {
        if (!value)
            throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }

    public static T Throws<T>(Action run) where T : Exception
    {
        try { run(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("Expected exception " + typeof(T).Name);
    }

    public static void JsonEqual(JsonElement expected, JsonElement actual, string path = "$")
    {
        if (expected.ValueKind != actual.ValueKind)
            throw new InvalidOperationException("JSON type differs at " + path);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                var left = expected.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                var right = actual.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
                Equal(left.Count, right.Count);
                foreach (var (key, value) in left)
                {
                    True(right.ContainsKey(key), "Missing JSON property " + path + "." + key);
                    JsonEqual(value, right[key], path + "." + key);
                }
                break;
            case JsonValueKind.Array:
                Equal(expected.GetArrayLength(), actual.GetArrayLength());
                for (var i = 0; i < expected.GetArrayLength(); i++)
                    JsonEqual(expected[i], actual[i], path + "[" + i + "]");
                break;
            case JsonValueKind.Number:
                if (expected.GetDecimal() != actual.GetDecimal())
                    throw new InvalidOperationException("JSON number differs at " + path);
                break;
            case JsonValueKind.String:
                if (expected.GetString() != actual.GetString())
                    throw new InvalidOperationException("JSON text differs at " + path);
                break;
            default:
                Equal(expected.GetRawText(), actual.GetRawText());
                break;
        }
    }
}

public sealed class Sandbox : IDisposable
{
    private readonly string _parent = System.IO.Path.GetFullPath(Environment.GetEnvironmentVariable("AUTOSOURCARR_TEST_ROOT") ?? System.IO.Path.GetTempPath());
    public string Path { get; }

    public Sandbox()
    {
        Directory.CreateDirectory(_parent);
        Path = System.IO.Path.Combine(_parent, "autosourcarr-dotnet-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        Check.True(resolved.StartsWith(_parent.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, comparison));
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
