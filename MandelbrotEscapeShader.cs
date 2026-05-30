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
    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        double cr = left + x * stepX;
        double ci = top - y * stepY;
        int index = y * width + x;

        double shiftedX = cr - 0.25;
        double q = shiftedX * shiftedX + ci * ci;

        if (q * (q + shiftedX) <= 0.25 * ci * ci || (cr + 1.0) * (cr + 1.0) + ci * ci <= 0.0625)
        {
            iterations[index] = -1;
            return;
        }

        double zr = 0;
        double zi = 0;
        double oldZr = 0;
        double oldZi = 0;
        int check = 20;

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

            if (i == check)
            {
                double deltaR = zr - oldZr;
                double deltaI = zi - oldZi;

                if (deltaR * deltaR + deltaI * deltaI < 0.000000000000000000000001)
                {
                    iterations[index] = -1;
                    return;
                }

                oldZr = zr;
                oldZi = zi;
                check += 20;
            }
        }

        iterations[index] = -1;
    }
}
