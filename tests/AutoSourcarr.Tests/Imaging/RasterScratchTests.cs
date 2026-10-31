using System.Diagnostics;
using System.Text.Json;
using AutoSourcarr.Core.Imaging;
using ImageMagick;

namespace AutoSourcarr.Tests;

public static class RasterScratchTests
{
    public static void Register(TestSuite suite) => suite.Add("raster analysis: native decoding keeps literal scratch metacharacters and outside-path denials", () =>
    {
        using var temp = new Sandbox();
        foreach (var name in new[] { "literal{scope}", "literal[part]", "literal{scope}[part]" })
        {
            var scratch = Path.Combine(temp.Path, name);
            // Keep both native policy and longer blob-path regressions independent
            // of the output folder; the old private prefix fails at this length.
            if (OperatingSystem.IsWindows() && scratch.Length < 192)
                scratch = Path.Combine(scratch, new string('x', Math.Max(1, 191 - scratch.Length)));
            Directory.CreateDirectory(scratch);
            using var receipt = RunProbe(scratch);
            Check.Equal(Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar), receipt.RootElement.GetProperty("scratch").GetString());
            Check.Equal(6, receipt.RootElement.GetProperty("formats").GetInt32());
            Check.True(receipt.RootElement.GetProperty("outside_path_denied").GetBoolean());
        }
        if (OperatingSystem.IsWindows())
        {
            var unsupported = Path.Combine(temp.Path, new string('z', Math.Max(1, 239 - temp.Path.Length)));
            Directory.CreateDirectory(unsupported);
            using var receipt = RunProbe(unsupported, reject: true);
            Check.True(receipt.RootElement.GetProperty("unsupported_path_rejected").GetBoolean());
            Check.Equal(0, receipt.RootElement.GetProperty("remaining_files").GetInt32());
        }
    });

    private static JsonDocument RunProbe(string scratch, bool reject = false)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(RasterScratchTests).Assembly.Location);
        start.ArgumentList.Add(reject ? "--raster-scratch-reject-probe" : "--raster-scratch-probe"); start.ArgumentList.Add(scratch);
        start.Environment["TMP"] = scratch; start.Environment["TEMP"] = scratch; start.Environment["TMPDIR"] = scratch;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Raster probe did not start");
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try
        {
            child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var text = output.GetAwaiter().GetResult(); var details = error.GetAwaiter().GetResult();
            Check.True(child.ExitCode == 0, "Literal scratch probe failed: " + text + details);
            return JsonDocument.Parse(text);
        }
        finally
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            child.WaitForExit(); output.GetAwaiter().GetResult(); error.GetAwaiter().GetResult();
        }
    }

    public static int Probe(string scratch)
    {
        var actual = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar), actual,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("The probe did not receive its requested private temporary directory");
        foreach (var format in new[] { "png", "jpg", "webp", "gif", "bmp", "avif" })
        {
            using var image = RasterImages.Open(File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample." + format)));
            Check.Equal(8, image.Width); Check.Equal(6, image.Height);
        }
        Check.Throws<MagickPolicyErrorException>(() =>
        {
            using var image = new MagickImage(ConfigurationTests.Fixture("Images/sample.png"), MagickFormat.Png);
        });
        Console.WriteLine(JsonSerializer.Serialize(new { scratch = actual, formats = 6, outside_path_denied = true }));
        return 0;
    }

    public static int RejectProbe(string scratch)
    {
        Check.True(OperatingSystem.IsWindows());
        Check.True(Path.GetFullPath(scratch).Length >= 240);
        Check.Equal(Path.GetFullPath(scratch).TrimEnd(Path.DirectorySeparatorChar), Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar));
        var error = Check.Throws<InvalidOperationException>(() =>
        {
            using var image = RasterImages.Open(File.ReadAllBytes(ConfigurationTests.Fixture("Images/sample.png")));
        });
        Check.True(error.Message.Contains("private raster temporary path", StringComparison.Ordinal));
        var remaining = Directory.EnumerateFileSystemEntries(scratch).Count(); Check.Equal(0, remaining);
        Console.WriteLine(JsonSerializer.Serialize(new { unsupported_path_rejected = true, remaining_files = remaining }));
        return 0;
    }
}
