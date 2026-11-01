namespace AutoSourcarr.Core.Matching.Mixed;

/// <summary>One decoded chapter page, already reduced to at most 256×256 for colour analysis (RGB, 3 bytes per pixel)
/// plus the original page size (its shape tells a long strip from a paged layout).</summary>
public sealed record PageRaster(int Width, int Height, byte[] Rgb, int SampleWidth, int SampleHeight);

/// <summary>What one page shows: colored pixel fraction, mean saturation, distinct hue families, and the page size.</summary>
public sealed record PageColorSample(double ColoredFraction, double MeanSaturation, int HueBins, int Width, int Height);

/// <summary>Reads a few interior pages of one source record's newest chapter (implemented by the source runtime owner:
/// chapter list → page list → <see cref="PageColorAnalysis.SampleIndexes"/> → fetch and decode each image, bounded).</summary>
public interface IPageImageSource
{
    Task<IReadOnlyList<PageRaster>> SamplePagesAsync(string sourceId, long mangaId, int pages, CancellationToken cancellationToken);
}

/// <summary>Port of color_profile.py (_inspect_color, _classify_samples) on raw pixels; no image library is needed here.</summary>
public static class PageColorAnalysis
{
    /// <summary>A page at least this many times taller than wide is a vertical-scroll strip.</summary>
    public const double LongStripRatio = 2.5;

    public static PageColorSample Inspect(PageRaster page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var pixels = page.Rgb.Length / 3;
        if (pixels == 0) return new(0, 0, 0, page.Width, page.Height);
        // Nearly black/white pixels (text, gutters) are ignored; saturation and hue are Pillow's 0..255 HSV.
        var eligible = 0; var colored = 0; long saturation = 0; var hues = new int[12];
        for (var i = 0; i < pixels; i++)
        {
            var (h, s, v) = Hsv(page.Rgb[i * 3], page.Rgb[i * 3 + 1], page.Rgb[i * 3 + 2]);
            if (v < 18 || v > 242) continue;
            eligible++; saturation += s;
            if (s >= 30) { colored++; hues[Math.Min(11, h * 12 / 256)]++; }
        }
        var denominator = Math.Max(1, eligible);
        var meaningful = hues.Count(count => (double)count / denominator >= .01);
        return new((double)colored / denominator, (double)saturation / denominator / 255.0, meaningful, page.Width, page.Height);
    }

    /// <summary>Released page vote: ≥60% colored pages → colored, ≤20% → monochrome, otherwise unknown (0.45).</summary>
    public static (string Mode, double Confidence, double ColoredFraction) Classify(IReadOnlyList<PageColorSample> samples, double coloredPixelFraction)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0) return ("unknown", 0, 0);
        var threshold = Math.Clamp(coloredPixelFraction, .01, .5);
        // A uniform sepia/scan tint is saturated but monochrome; real colour spans several hue families.
        var votes = samples.Count(sample => sample.ColoredFraction >= threshold && sample.HueBins >= 3 || sample.MeanSaturation >= .18 && sample.HueBins >= 2);
        var ratio = (double)votes / samples.Count;
        var average = samples.Average(sample => sample.ColoredFraction);
        if (ratio >= .6) return ("colored", Math.Min(.99, .6 + .3 * ratio + Math.Min(.09, average)), average);
        if (ratio <= .2) return ("monochrome", Math.Min(.99, .7 + .25 * (1 - ratio)), average);
        return ("unknown", .45, average);
    }

    /// <summary>True when most sampled pages are tall vertical strips.</summary>
    public static bool LongStrip(IReadOnlyList<PageColorSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var sized = samples.Where(sample => sample.Width > 0 && sample.Height > 0).ToArray();
        return sized.Length != 0 && sized.Count(sample => (double)sample.Height / sample.Width >= LongStripRatio) * 2 > sized.Length;
    }

    /// <summary>Interior page indexes (cover, title and end cards avoided), as released.</summary>
    public static IReadOnlyList<int> SampleIndexes(int count, int sampleCount)
    {
        sampleCount = Math.Clamp(sampleCount, 2, 6);
        if (count <= 0) return [];
        if (count <= sampleCount) return Enumerable.Range(0, count).ToArray();
        return Enumerable.Range(0, sampleCount)
            .Select(i => Math.Clamp((int)Math.Round((i + 1) * (count - 1) / (double)(sampleCount + 1), MidpointRounding.ToEven), 0, count - 1))
            .Distinct().Order().ToArray();
    }

    /// <summary>Pillow's RGB→HSV (Convert.c rgb2hsv): all three channels 0..255.</summary>
    internal static (int H, int S, int V) Hsv(byte r, byte g, byte b)
    {
        int max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max == min) return (0, 0, max);
        double range = max - min, s = range / max, rc = (max - r) / range, gc = (max - g) / range, bc = (max - b) / range;
        var h = r == max ? bc - gc : g == max ? 2.0 + rc - bc : 4.0 + gc - rc;
        h = (h / 6.0 + 1.0) % 1.0;
        return (Math.Clamp((int)(h * 255.0), 0, 255), Math.Clamp((int)(s * 255.0), 0, 255), max);
    }
}
