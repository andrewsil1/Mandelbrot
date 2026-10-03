namespace MandelbrotGpu;

public sealed class MandelbrotViewport : IDisposable
{
    // View state is stored in MPFR so repeated zoom clicks preserve coordinates
    // beyond the point where a double can represent screen-pixel deltas.
    private const uint DefaultPrecisionBits = 384;

    private MandelbrotViewport(MpfrFloat centerX, MpfrFloat centerY, MpfrFloat height, double aspect)
    {
        CenterX = centerX;
        CenterY = centerY;
        Height = height;
        Aspect = aspect;
    }

    public MpfrFloat CenterX { get; }

    public MpfrFloat CenterY { get; }

    public MpfrFloat Height { get; }

    public double Aspect { get; }

    public MpfrFloat Width => Height.Multiply(Aspect);

    // Scale is the current vertical complex-plane span relative to the initial
    // full-set span of 4. Using height keeps zoom stable across window resizes.
    public double Scale => Height.ToDouble() / 4.0;

    public static MandelbrotViewport FullSet(int pixelWidth, int pixelHeight)
    {
        double aspect = (double)pixelWidth / pixelHeight;
        // The initial view always contains roughly [-2, 2] on the shorter
        // dimension, expanding the longer dimension to match the window.
        double height = aspect >= 1.0 ? 4.0 : 4.0 / aspect;

        return new MandelbrotViewport(
            MpfrFloat.FromDouble(0, DefaultPrecisionBits),
            MpfrFloat.FromDouble(0, DefaultPrecisionBits),
            MpfrFloat.FromDouble(height, DefaultPrecisionBits),
            aspect);
    }

    public MandelbrotViewport Zoom(MpfrComplex center, double factor)
    {
        // A zoom replaces the center with the clicked MPFR point and shrinks
        // or expands only the view height. Aspect is handled separately.
        return new MandelbrotViewport(
            center.Real.Clone(),
            center.Imaginary.Clone(),
            Height.Multiply(factor),
            Aspect);
    }

    public MandelbrotViewport WithAspect(double aspect)
    {
        // Preserve center and zoom depth while changing only the horizontal
        // span. This is what makes corner-drag resizing recalculate the same
        // view into the new rectangular shape.
        return new MandelbrotViewport(CenterX.Clone(), CenterY.Clone(), Height.Clone(), aspect);
    }

    public MandelbrotView ToFloat64View()
    {
        // Direct rendering is still fastest while the viewport can be safely
        // represented with doubles. Deeper modes keep using this MPFR viewport.
        double height = Height.ToDouble();
        double width = height * Aspect;

        return new MandelbrotView(CenterX.ToDouble(), CenterY.ToDouble(), width, height);
    }

    public MpfrComplex PointAt(double normalizedX, double normalizedY)
    {
        // Convert normalized image coordinates to a complex coordinate using
        // MPFR arithmetic. This path is used for click centers, reference
        // selection, and CPU repair pixels.
        using MpfrFloat width = Width;
        using MpfrFloat halfWidth = width.Multiply(0.5);
        using MpfrFloat halfHeight = Height.Multiply(0.5);
        using MpfrFloat left = CenterX.Subtract(halfWidth);
        using MpfrFloat top = CenterY.Add(halfHeight);
        using MpfrFloat xOffset = width.Multiply(normalizedX);
        using MpfrFloat yOffset = Height.Multiply(normalizedY);

        MpfrFloat x = left.Add(xOffset);
        MpfrFloat y = top.Subtract(yOffset);

        return new MpfrComplex(x, y);
    }

    public MpfrComplex PointAtPixel(int x, int y, int pixelWidth, int pixelHeight)
    {
        // Use the same reciprocal steps and half-pixel offsets as GPU delta
        // construction. Dividing normalized coordinates first rounds at a
        // different point, which is avoidable for reference/repair pixels.
        using MpfrFloat width = Width;
        using MpfrFloat halfWidth = width.Multiply(0.5);
        using MpfrFloat halfHeight = Height.Multiply(0.5);
        using MpfrFloat left = CenterX.Subtract(halfWidth);
        using MpfrFloat top = CenterY.Add(halfHeight);
        using MpfrFloat stepX = width.Multiply(1.0 / pixelWidth);
        using MpfrFloat stepY = Height.Multiply(1.0 / pixelHeight);
        using MpfrFloat offsetX = stepX.Multiply(x + 0.5);
        using MpfrFloat offsetY = stepY.Multiply(y + 0.5);
        return new MpfrComplex(left.Add(offsetX), top.Subtract(offsetY));
    }

    public string Describe()
    {
        return $"center=({CenterX.ToDisplayString()}, {CenterY.ToDisplayString()})  scale={Scale:E3}";
    }

    public void Dispose()
    {
        CenterX.Dispose();
        CenterY.Dispose();
        Height.Dispose();
    }
}

public readonly record struct MandelbrotView(double CenterX, double CenterY, double Width, double Height)
{
    public double Left => CenterX - Width * 0.5;

    public double Top => CenterY + Height * 0.5;

    // Zoom level is measured from the vertical span so resizing wider or
    // narrower does not change the iteration budget.
    public double Scale => Height / 4.0;

    public static MandelbrotView FullSet(int pixelWidth, int pixelHeight)
    {
        double aspect = (double)pixelWidth / pixelHeight;
        double width;
        double height;

        if (aspect >= 1.0)
        {
            height = 4.0;
            width = height * aspect;
        }
        else
        {
            width = 4.0;
            height = width / aspect;
        }

        return new MandelbrotView(0, 0, width, height);
    }

    public MandelbrotView Zoom(double centerX, double centerY, double factor)
    {
        return new MandelbrotView(centerX, centerY, Width * factor, Height * factor);
    }

    public MandelbrotView WithAspect(double aspect)
    {
        // Preserve the current zoom and center while changing the horizontal
        // span to match the render target.
        return new MandelbrotView(CenterX, CenterY, Height * aspect, Height);
    }
}

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
