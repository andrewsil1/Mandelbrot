namespace MandelbrotGpu;

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
