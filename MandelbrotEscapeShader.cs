using ComputeSharp;

namespace MandelbrotGpu;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotEscapeShader(
    ReadWriteBuffer<int> iterations,
    double left,
    double top,
    double stepX,
    double stepY,
    int width,
    int maxIterations) : IComputeShader
{
    // Direct FP64 mode: one GPU thread evaluates one pixel from z = 0. This is
    // the simplest and fastest path while the viewport is not so small that
    // adjacent pixel coordinates collapse into the same double value.
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;

        // Map this GPU thread's pixel to a point in the complex plane.
        double cr = left + x * stepX;
        double ci = top - y * stepY;
        int index = y * width + x;

        // Fast interior tests for the main cardioid and the period-2 bulb.
        // These points never escape, so skipping the full loop saves a lot of
        // work near the largest solid regions of the set.
        double shiftedX = cr - 0.25;
        double q = shiftedX * shiftedX + ci * ci;

        if (q * (q + shiftedX) <= 0.25 * ci * ci || (cr + 1.0) * (cr + 1.0) + ci * ci <= 0.0625)
        {
            iterations[index] = EscapeClassification.Interior;
            return;
        }

        double zr = 0;
        double zi = 0;
        double oldZr = 0;
        double oldZi = 0;
        int check = 20;

        // Iterate z = z^2 + c until the orbit escapes radius 2, or until the
        // iteration budget is exhausted.
        for (int i = 0; i < maxIterations; i++)
        {
            double zr2 = zr * zr;
            double zi2 = zi * zi;

            if (zr2 + zi2 > 4.0)
            {
                iterations[index] = i;
                return;
            }

            zi = 2.0 * zr * zi + ci;
            zr = zr2 - zi2 + cr;

            // Periodicity checking catches many interior points that settle
            // into a repeating orbit without spending the whole iteration cap.
            if (i == check)
            {
                double deltaR = zr - oldZr;
                double deltaI = zi - oldZi;

                if (deltaR * deltaR + deltaI * deltaI < 0.000000000000000000000001)
                {
                    iterations[index] = EscapeClassification.Interior;
                    return;
                }

                oldZr = zr;
                oldZi = zi;
                check += 20;
            }
        }

        iterations[index] = EscapeClassification.Interior;
    }
}
