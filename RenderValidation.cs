namespace MandelbrotGpu;

public sealed record RenderValidation(int Samples, int Mismatches, int Unresolved);

internal static class NumericalValidation
{
    // Explicit opt-in: this is expensive CPU work and should not run during
    // normal interaction. Samples are deterministic, including the corners.
    public static RenderValidation Check(MandelbrotViewport viewport, int[] iterations, int width, int height, int maxIterations)
    {
        int mismatches = 0;
        int unresolved = 0;
        for (int sample = 0; sample < 64; sample++)
        {
            int x = (sample % 8) * (width - 1) / 7;
            int y = (sample / 8) * (height - 1) / 7;
            int actual = iterations[y * width + x];
            if (actual == EscapeClassification.Glitch)
            {
                unresolved++;
                continue;
            }

            using MpfrComplex point = viewport.PointAtPixel(x, y, width, height);
            // Higher orbit precision checks GPU/repair results for the stored
            // viewport coordinates; it cannot recover precision already lost
            // when those coordinates were originally constructed.
            int expected = MpfrMandelbrot.EscapeIterations(point, maxIterations, 768);
            if (actual != expected)
            {
                mismatches++;
            }
        }

        return new RenderValidation(64, mismatches, unresolved);
    }
}
