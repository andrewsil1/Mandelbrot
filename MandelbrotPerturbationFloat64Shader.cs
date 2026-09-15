using ComputeSharp;

namespace MandelbrotGpu;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotPerturbationFloat64Shader(
    ReadWriteBuffer<int> iterations,
    ReadOnlyBuffer<double> referenceReal,
    ReadOnlyBuffer<double> referenceImaginary,
    double centerReal,
    double centerImaginary,
    double leftDelta,
    double topDelta,
    double stepX,
    double stepY,
    int width,
    int maxIterations) : IComputeShader
{
    // This is not a machine-epsilon threshold. It is a practical validity
    // heuristic for perturbation rendering: if the reconstructed orbit is tiny
    // compared with the reference orbit, cancellation can corrupt the escape
    // count and therefore the histogram palette.
    private const double GlitchThreshold = 1E-6;

    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        int index = y * width + x;

        // dc is the pixel's offset from the reference point. The shader evolves
        // dz, where z_pixel = z_reference + dz, instead of re-evaluating the
        // whole Mandelbrot orbit for every deep-zoom pixel.
        double dcReal = leftDelta + x * stepX;
        double dcImaginary = topDelta - y * stepY;
        double cr = centerReal + dcReal;
        double ci = centerImaginary + dcImaginary;
        double dzReal = 0;
        double dzImaginary = 0;

        double shiftedX = cr - 0.25;
        double q = shiftedX * shiftedX + ci * ci;

        if (q * (q + shiftedX) <= 0.25 * ci * ci || (cr + 1.0) * (cr + 1.0) + ci * ci <= 0.0625)
        {
            iterations[index] = EscapeClassification.Interior;
            return;
        }

        for (int i = 0; i < maxIterations; i++)
        {
            double referenceMagnitude = referenceReal[i] * referenceReal[i] + referenceImaginary[i] * referenceImaginary[i];
            double zReal = referenceReal[i] + dzReal;
            double zImaginary = referenceImaginary[i] + dzImaginary;
            double zMagnitude = zReal * zReal + zImaginary * zImaginary;

            if (zMagnitude > 4.0)
            {
                iterations[index] = i;
                return;
            }

            // Perturbation becomes unreliable when the perturbed orbit nearly
            // cancels the reference orbit. Recompute these pixels with MPFR on
            // the CPU instead of allowing them to speckle the histogram.
            if (i > 8 && zMagnitude < GlitchThreshold * referenceMagnitude)
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }

            // Perturbation recurrence:
            //   dz' = 2 * z_ref * dz + dz^2 + dc
            // The reference orbit comes from MPFR on the CPU; each pixel's
            // delta orbit is independent and therefore maps well to the GPU.
            double nextDzReal =
                2.0 * (referenceReal[i] * dzReal - referenceImaginary[i] * dzImaginary) +
                dzReal * dzReal -
                dzImaginary * dzImaginary +
                dcReal;

            double nextDzImaginary =
                2.0 * (referenceReal[i] * dzImaginary + referenceImaginary[i] * dzReal) +
                2.0 * dzReal * dzImaginary +
                dcImaginary;

            dzReal = nextDzReal;
            dzImaginary = nextDzImaginary;
        }

        iterations[index] = EscapeClassification.Interior;
    }
}
