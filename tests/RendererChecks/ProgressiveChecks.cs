using MandelbrotGpu;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class ProgressiveChecks
{
    internal static void Run()
    {
        CheckPresentation();
        CheckRetainedHistogram();
        ProgressiveFrame frame = new(6, 64);
        int[] pixels = Enumerable.Repeat(123, 6).ToArray();
        frame.Publish([4, -3, -2, -1], 0, 4, [5, 4, 3, 2]);
        if (!frame.Drain(pixels, out _, out _) || pixels[5] == 123 || pixels[2] == 123
            || pixels[0] != 123 || pixels[3] != 123 || pixels[4] != 123)
            throw new Exception("Progressive sparse mapping or preview preservation failed.");
        frame.Publish([4, -3, -2, -1], 0, 4, [5, 4, 3, 2]);
        if (frame.Drain(pixels, out _, out _)) throw new Exception("Unchanged progress was republished.");
        Parallel.For(0, 6, i => frame.Publish([i + 10], 0, 1, [i]));
        frame.Drain(pixels, out _, out _);
        for (int i = 0; i < 6; i++)
            if (pixels[i] != HistogramColorizer.PreviewColor(i + 10, 64))
                throw new Exception("Concurrent progressive correction lost a pixel.");

        const int width = 97, height = 53, budget = 1024;
        foreach (double scale in new[] { 1.0, 1E-14, 1E-28 })
        {
            MandelbrotViewport view = MandelbrotViewport.FullSet(width, height);
            if (scale != 1)
            {
                using MpfrComplex point = new(MpfrFloat.FromDouble(-2, 768), MpfrFloat.FromDouble(0, 768));
                view = view.Zoom(point, scale);
            }
            int[] observed = Enumerable.Repeat(EscapeClassification.Glitch, width * height).ToArray();
            int updates = 0;
            MandelbrotRenderer renderer = new(width, height)
            {
                PublishPixels = (values, offset, count, indices) =>
                {
                    Interlocked.Increment(ref updates);
                    for (int i = offset; i < offset + count; i++)
                        if (values[i] >= EscapeClassification.Interior)
                            observed[indices is null ? i : indices[i]] = values[i];
                }
            };
            RenderResult progressive = renderer.Render(view, budget);
            RenderResult baseline = new MandelbrotRenderer(width, height).Render(view, budget);
            if (updates == 0 || !progressive.Pixels.SequenceEqual(baseline.Pixels)
                || !HistogramColorizer.Colorize(observed, budget).SequenceEqual(progressive.Pixels))
                throw new Exception($"Progressive output differs from completed render at scale {scale}.");
        }
        Console.WriteLine("Progressive checks passed: preview preservation, sparse mapping, concurrent repairs and final GPU image equivalence.");
    }

    private static void CheckRetainedHistogram()
    {
        int[] original = [2, 2, 8, 20, 20, 20, -1, -2];
        int[] colors = HistogramColorizer.Colorize(original, 32, out int[] palette);
        foreach (int budget in new[] { 16, 64 })
        {
            ProgressiveFrame frame = new(5, budget, palette);
            int[] pixels = [123, 123, 123, 123, 123];
            frame.Publish([2, 8, budget, -1, -2], 0, 5, null);
            frame.Drain(pixels, out _, out _);
            if (pixels[0] != colors[0] || pixels[1] != colors[2]
                || pixels[2] != palette[Math.Min(budget, 32)]
                || pixels[3] != colors[6] || pixels[4] != 123)
                throw new Exception("Progressive refresh did not retain the original histogram across iteration budgets.");
        }
        HistogramColorizer.Colorize([-1, -2], 32, out int[] emptyPalette);
        ProgressiveFrame empty = new(1, 64, emptyPalette);
        int[] dark = [123];
        empty.Publish([64], 0, 1, null);
        empty.Drain(dark, out _, out _);
        if (dark[0] != colors[6]) throw new Exception("Empty histogram preview was not preserved.");
    }

    private static void CheckPresentation()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                const int size = 16;
                int color = unchecked((int)0xFF33AA66);
                WriteableBitmap bitmap = new(size, size, 96, 96, PixelFormats.Bgra32, null);
                int[] pixels = Enumerable.Repeat(color, size * size).ToArray();
                bitmap.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
                ProgressiveFrame frame = new(size * size, 64);
                frame.Publish([12], 0, 1, [size * 7 + 3]);
                frame.Apply(bitmap);
                bitmap.CopyPixels(pixels, size * 4, 0);
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i] != (i == size * 7 + 3 ? HistogramColorizer.PreviewColor(12, 64) : color))
                        throw new Exception("WPF progressive row update damaged the preview.");

                // Exercise the actual zoom-preview method without showing a
                // window or triggering Loaded/GPU work.
                MainWindow window = new();
                try
                {
                    const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                    typeof(MainWindow).GetField("bitmap", flags)!.SetValue(window, bitmap);
                    typeof(MainWindow).GetField("imageWidth", flags)!.SetValue(window, size);
                    typeof(MainWindow).GetField("imageHeight", flags)!.SetValue(window, size);
                    Image image = (Image)window.FindName("FractalImage");
                    image.Source = bitmap;
                    image.Measure(new Size(size, size));
                    image.Arrange(new Rect(0, 0, size, size));
                    // Off-center zoom out places the old image to the right
                    // and above the new center, leaving exposed space dark.
                    typeof(MainWindow).GetMethod("ScaleZoomPreview", flags)!
                        .Invoke(window, [new Point(4, 12), 4.0]);
                    bitmap.CopyPixels(pixels, size * 4, 0);
                    if (pixels[6 * size + 8] != color || pixels[0] != unchecked((int)0xFF020308))
                        throw new Exception("Off-center WPF zoom preview mapping failed.");
                }
                finally { window.Close(); }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
