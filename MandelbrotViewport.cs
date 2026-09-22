namespace MandelbrotGpu;

public sealed class MandelbrotViewport
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
}
