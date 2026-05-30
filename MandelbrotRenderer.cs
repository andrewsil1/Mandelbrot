using ComputeSharp;

namespace MandelbrotGpu;

public sealed class MandelbrotRenderer(int width, int height)
{
    private readonly GraphicsDevice device = GraphicsDevice.GetDefault();

    public int[] Render(MandelbrotView view, int maxIterations)
    {
        int pixelCount = width * height;
        int[] iterations = new int[pixelCount];

        using ReadWriteBuffer<int> iterationBuffer = device.AllocateReadWriteBuffer<int>(pixelCount);

        // The compute shader writes one escape count per pixel. Coloring stays
        // on the CPU because histogram coloring needs a whole-image cumulative
        // distribution after every pixel has been evaluated.
        device.For(
            width,
            height,
            new MandelbrotEscapeShader(
                iterationBuffer,
                view.Left,
                view.Top,
                view.Width / width,
                view.Height / height,
                width,
                maxIterations));

        iterationBuffer.CopyTo(iterations);

        return HistogramColorizer.Colorize(iterations, maxIterations);
    }
}
