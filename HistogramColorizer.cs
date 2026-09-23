namespace MandelbrotGpu;

internal static class HistogramColorizer
{
    // Histogram coloring maps escape counts through the cumulative escape-count
    // distribution, so colors reflect the population of the current image
    // rather than fixed iteration bands. The palette is stored as BGRA later
    // because WPF's WriteableBitmap uses PixelFormats.Bgra32.
    private static readonly (double Stop, Rgb Color)[] Palette =
    [
        (0.00, new Rgb(8, 12, 22)),
        (0.16, new Rgb(36, 67, 130)),
        (0.33, new Rgb(65, 170, 160)),
        (0.50, new Rgb(236, 214, 120)),
        (0.70, new Rgb(221, 105, 64)),
        (0.86, new Rgb(110, 52, 126)),
        (1.00, new Rgb(244, 246, 250))
    ];

    public static int[] Colorize(int[] iterations, int maxIterations)
        => Colorize(iterations, maxIterations, out _);

    internal static int[] Colorize(int[] iterations, int maxIterations, out int[] palette)
    {
        int[] histogram = new int[maxIterations + 1];
        int escapedCount = 0;

        // Interior pixels are stored as -1 and excluded from the histogram.
        foreach (int iteration in iterations)
        {
            if (iteration >= 0)
            {
                // The shader stores the first iteration whose orbit exceeds
                // radius 2. Values at or below maxIterations are valid bins.
                histogram[iteration]++;
                escapedCount++;
            }
        }

        int[] cumulative = new int[histogram.Length];
        int running = 0;

        // Convert the histogram to a cumulative distribution. This spreads
        // colors according to how many pixels escaped at each iteration count,
        // which avoids harsh bands compared with direct iteration coloring.
        for (int i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            cumulative[i] = running;
        }

        palette = new int[histogram.Length];
        for (int i = 0; i < palette.Length; i++)
        {
            Rgb color = SamplePalette(escapedCount == 0 ? 0 : (double)cumulative[i] / escapedCount);
            palette[i] = escapedCount == 0 ? unchecked((int)0xFF020308)
                : unchecked((int)(0xFF000000 | (uint)(color.R << 16) | (uint)(color.G << 8) | color.B));
        }

        int[] pixels = new int[iterations.Length];

        for (int i = 0; i < iterations.Length; i++)
        {
            int iteration = iterations[i];

            if (iteration < 0 || escapedCount == 0)
            {
                // Interior and still-unresolved glitch pixels are rendered
                // dark. A visible dark speckle after repair usually means the
                // glitch count exceeded the current final-repair budget.
                pixels[i] = unchecked((int)0xFF020308);
                continue;
            }

            pixels[i] = palette[iteration];
        }

        return pixels;
    }

    // First-render fallback, before any completed histogram is available.
    internal static int PreviewColor(int iteration, int maxIterations)
    {
        if (iteration < 0) return unchecked((int)0xFF020308);
        Rgb color = SamplePalette(Math.Log(1.0 + iteration) / Math.Log(1.0 + maxIterations));
        return unchecked((int)(0xFF000000 | (uint)(color.R << 16) | (uint)(color.G << 8) | color.B));
    }

    private static Rgb SamplePalette(double t)
    {
        t = global::System.Math.Clamp(t, 0, 1);

        // The palette is piecewise linear, with SmoothStep easing to soften
        // transitions between adjacent color stops.
        for (int i = 1; i < Palette.Length; i++)
        {
            (double stop, Rgb color) = Palette[i];

            if (t <= stop)
            {
                (double previousStop, Rgb previousColor) = Palette[i - 1];
                double localT = (t - previousStop) / (stop - previousStop);

                return Rgb.Lerp(previousColor, color, SmoothStep(localT));
            }
        }

        return Palette[^1].Color;
    }

    private static double SmoothStep(double t)
    {
        return t * t * (3 - 2 * t);
    }

    private readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            return new Rgb(
                LerpChannel(a.R, b.R, t),
                LerpChannel(a.G, b.G, t),
                LerpChannel(a.B, b.B, t));
        }

        private static byte LerpChannel(byte a, byte b, double t)
        {
            return (byte)global::System.Math.Round(a + (b - a) * t);
        }
    }
}
