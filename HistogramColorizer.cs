namespace MandelbrotGpu;

internal static class HistogramColorizer
{
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
    {
        int[] histogram = new int[maxIterations + 1];
        int escapedCount = 0;

        foreach (int iteration in iterations)
        {
            if (iteration >= 0)
            {
                histogram[iteration]++;
                escapedCount++;
            }
        }

        int[] cumulative = new int[histogram.Length];
        int running = 0;

        for (int i = 0; i < histogram.Length; i++)
        {
            running += histogram[i];
            cumulative[i] = running;
        }

        int[] pixels = new int[iterations.Length];

        for (int i = 0; i < iterations.Length; i++)
        {
            int iteration = iterations[i];

            if (iteration < 0 || escapedCount == 0)
            {
                pixels[i] = unchecked((int)0xFF020308);
                continue;
            }

            double t = (double)cumulative[iteration] / escapedCount;
            Rgb color = SamplePalette(t);
            pixels[i] = unchecked((int)(0xFF000000 | (uint)(color.R << 16) | (uint)(color.G << 8) | color.B));
        }

        return pixels;
    }

    private static Rgb SamplePalette(double t)
    {
        t = Math.Clamp(t, 0, 1);

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
            return (byte)Math.Round(a + (b - a) * t);
        }
    }
}
