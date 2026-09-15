namespace MandelbrotGpu;

internal static class IterationBudget
{
    public static int ForScale(double scale)
    {
        // Increase iteration count logarithmically with zoom. This keeps the
        // initial view quick while giving deep boundary regions enough
        // iterations to show detail instead of prematurely becoming interior.
        double zoom = global::System.Math.Max(1.0, 1.0 / scale);
        double budget = 512 + 160 * global::System.Math.Log2(zoom + 1);

        // Clamp the budget so accidental clicks cannot request an impractical
        // amount of GPU work.
        return global::System.Math.Clamp((int)global::System.Math.Round(budget), 512, 32_768);
    }
}
