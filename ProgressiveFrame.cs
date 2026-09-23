using System.Windows;
using System.Windows.Media.Imaging;

namespace MandelbrotGpu;

// One bounded mailbox per render. GPU callbacks and parallel MPFR workers never
// touch WPF, and a slow UI cannot accumulate dispatcher messages or frame copies.
internal sealed class ProgressiveFrame
{
    private readonly object gate = new();
    private readonly int[] values;
    private readonly int[] palette;
    private int first = int.MaxValue;
    private int last = -1;

    internal ProgressiveFrame(int pixelCount, int maxIterations, int[]? histogramPalette = null)
    {
        values = Enumerable.Repeat(EscapeClassification.Pending, pixelCount).ToArray();
        palette = histogramPalette ?? Enumerable.Range(0, maxIterations + 1)
            .Select(i => HistogramColorizer.PreviewColor(i, maxIterations)).ToArray();
    }

    internal void Publish(int[] iterations, int offset, int count, int[]? indices)
    {
        lock (gate)
        {
            for (int i = offset; i < offset + count; i++)
            {
                int value = iterations[i];
                if (value < EscapeClassification.Interior) continue;
                int index = indices is null ? i : indices[i];
                if (values[index] == value) continue;
                values[index] = value;
                first = Math.Min(first, index);
                last = Math.Max(last, index);
            }
        }
    }

    internal bool Drain(int[] pixels, out int start, out int end)
    {
        lock (gate)
        {
            start = first;
            end = last;
            if (last < first) return false;
            for (int i = first; i <= last; i++)
            {
                int value = values[i];
                if (value == EscapeClassification.Pending) continue;
                // Counts beyond the previous budget lie at the top of its CDF.
                pixels[i] = value < 0 ? unchecked((int)0xFF020308) : palette[Math.Min(value, palette.Length - 1)];
            }
            first = int.MaxValue;
            last = -1;
            return true;
        }
    }

    private int[]? display;

    internal void Apply(WriteableBitmap bitmap)
    {
        if (display is null)
        {
            display = new int[values.Length];
            bitmap.CopyPixels(display, bitmap.PixelWidth * sizeof(int), 0);
        }
        if (!Drain(display, out int start, out int end)) return;
        int width = bitmap.PixelWidth;
        int firstRow = start / width;
        int rows = end / width - firstRow + 1;
        bitmap.WritePixels(new Int32Rect(0, firstRow, width, rows), display,
            width * sizeof(int), 0, firstRow);
    }
}
