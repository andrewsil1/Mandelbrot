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

    internal unsafe void Apply(WriteableBitmap bitmap)
    {
        if ((long)bitmap.PixelWidth * bitmap.PixelHeight != values.Length || bitmap.Format != System.Windows.Media.PixelFormats.Bgra32)
            throw new ArgumentException("Progressive bitmap must match the frame dimensions and BGRA32 format.", nameof(bitmap));
        // Update only resolved pixels in the existing bitmap. Keeping a second
        // full-frame display array would duplicate the preview and mailbox.
        bitmap.Lock();
        try
        {
            lock (gate)
            {
                if (last < first) return;
                int width = bitmap.PixelWidth;
                int firstRow = first / width;
                int rows = last / width - firstRow + 1;
                byte* target = (byte*)bitmap.BackBuffer;
                int stride = bitmap.BackBufferStride;
                for (int y = firstRow; y < firstRow + rows; y++)
                {
                    int* row = (int*)(target + y * stride);
                    int rowStart = y * width;
                    int end = Math.Min(last + 1, rowStart + width);
                    for (int i = Math.Max(first, rowStart); i < end; i++)
                    {
                        int value = values[i];
                        if (value == EscapeClassification.Pending) continue;
                        row[i - rowStart] = value < 0 ? unchecked((int)0xFF020308) : palette[Math.Min(value, palette.Length - 1)];
                    }
                }
                bitmap.AddDirtyRect(new Int32Rect(0, firstRow, width, rows));
                first = int.MaxValue;
                last = -1;
            }
        }
        finally { bitmap.Unlock(); }
    }
}
