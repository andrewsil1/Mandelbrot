namespace MandelbrotGpu;

internal static class IterationBudget
{
    public static int ForScale(double scale)
    {
        double zoom = Math.Max(1.0, 1.0 / scale);
        double budget = 512 + 160 * Math.Log2(zoom + 1);

        return Math.Clamp((int)Math.Round(budget), 512, 32_768);
    }
}
