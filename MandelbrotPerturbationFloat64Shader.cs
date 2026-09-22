using ComputeSharp;

namespace MandelbrotGpu;

[ThreadGroupSize(64, 1, 1)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotPerturbationFloat64Shader(
    ReadWriteBuffer<int> iterations,
    ReadOnlyBuffer<double> referenceReal,
    ReadOnlyBuffer<double> referenceImaginary,
    ReadOnlyBuffer<double> bla,
    ReadWriteBuffer<int> metrics,
    ReadWriteBuffer<double> state,
    int blaLeaves,
    int referenceLength,
    int acceleration,
    bool metricsEnabled,
    double centerReal,
    double centerImaginary,
    double leftDelta,
    double topDelta,
    double stepX,
    double stepY,
    int width,
    int workOffset,
    int sliceStart,
    int sliceEnd,
    int maxIterations,
    bool seeded = false) : IComputeShader
{
    // This is not a machine-epsilon threshold. It is a practical validity
    // heuristic for perturbation rendering: if the reconstructed orbit is tiny
    // compared with the reference orbit, cancellation can corrupt the escape
    // count and therefore the histogram palette.
    private const double GlitchThreshold = 1E-6;

    public void Execute()
    {
        int index = workOffset + ThreadIds.X;
        bool firstSlice = sliceStart == 0;
        if (firstSlice && seeded && iterations[index] != EscapeClassification.Glitch) return;
        if (!firstSlice && iterations[index] != EscapeClassification.Pending) return;
        int s = ThreadIds.X * 5;
        int x = index % width;
        int y = index / width;
        if (metricsEnabled && firstSlice)
        {
            metrics[index * 3] = 0;
            metrics[index * 3 + 1] = 0;
            metrics[index * 3 + 2] = 0;
        }

        // dc is the pixel's offset from the reference point. The shader evolves
        // dz, where z_pixel = z_reference + dz, instead of re-evaluating the
        // whole Mandelbrot orbit for every deep-zoom pixel.
        double dcReal = leftDelta + (x + 0.5) * stepX;
        double dcImaginary = topDelta - (y + 0.5) * stepY;
        // Cancellation can make dc tiny while its construction error is not.
        // This is a persistent uncertainty in c, injected on EVERY iteration,
        // not just an initial uncertainty in z. Include conversion and arithmetic.
        double coordinateError = 1E-15 * (MathMagnitude(leftDelta, topDelta)
            + MathMagnitude((x + 0.5) * stepX, (y + 0.5) * stepY));
        double cr = centerReal + dcReal;
        double ci = centerImaginary + dcImaginary;
        double dzReal = firstSlice ? 0 : state[s];
        double dzImaginary = firstSlice ? 0 : state[s + 1];

        double shiftedX = cr - 0.25;
        double q = shiftedX * shiftedX + ci * ci;

        if (firstSlice && (q * (q + shiftedX) < 0.25 * ci * ci - 1E-14 || (cr + 1.0) * (cr + 1.0) + ci * ci < 0.0625 - 1E-14))
        {
            iterations[index] = EscapeClassification.Interior;
            return;
        }

        int i = firstSlice ? 0 : (int)state[s + 3];
        int r = firstSlice ? 0 : (int)state[s + 4];
        double error = firstSlice ? 0 : state[s + 2];
        while (i < sliceEnd)
        {
            double referenceMagnitude = referenceReal[r] * referenceReal[r] + referenceImaginary[r] * referenceImaginary[r];
            double zReal = referenceReal[r] + dzReal;
            double zImaginary = referenceImaginary[r] + dzImaginary;
            double zMagnitude = zReal * zReal + zImaginary * zImaginary;

            double uncertainty = 8.0 * error + 1E-14;
            // Decide against the actual escape boundary. A fixed trajectory
            // error cap rejected otherwise unambiguous escape counts and
            // forced millions of pixels into the slow DD path.
            if (i > 0 && (zMagnitude > 4.0 ? zMagnitude - 4.0 : 4.0 - zMagnitude) < uncertainty)
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }
            if (zMagnitude > 4.0)
            {
                iterations[index] = i;
                return;
            }

            // Perturbation becomes unreliable when the perturbed orbit nearly
            // cancels the reference orbit. Recompute these pixels with MPFR on
            // the CPU instead of allowing them to speckle the histogram.
            double deltaMagnitude = dzReal * dzReal + dzImaginary * dzImaginary;
            if (acceleration >= 1 && r > 0 && (zMagnitude < deltaMagnitude || r + 1 >= referenceLength))
            {
                dzReal = zReal;
                dzImaginary = zImaginary;
                error += 1E-15 * (MathMagnitude(referenceReal[r], referenceImaginary[r]) + MathMagnitude(dzReal, dzImaginary));
                r = 0;
                deltaMagnitude = zMagnitude;
                if (metricsEnabled) metrics[index * 3]++;
            }
            else if ((r + 1 >= referenceLength && i + 1 < maxIterations) || (i > 8 && zMagnitude < GlitchThreshold * referenceMagnitude))
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }

            if (acceleration >= 2 && r > 0)
            {
                int node = blaLeaves + r - 1;
                int length = 1;
                int best = 0;
                int bestLength = 0;
                while (node > 0 && r + length < referenceLength && i + length < maxIterations && i + length <= sliceEnd)
                {
                    double radius = bla[node * 13 + 8];
                    if (length >= 2 && radius > 0 && MathMagnitude(dzReal, dzImaginary) + error < radius)
                    {
                        best = node;
                        bestLength = length;
                    }
                    if ((node & 1) != 0) break;
                    node /= 2;
                    length *= 2;
                }
                if (best > 0)
                {
                    int b = best * 13;
                    double nextReal = bla[b] * dzReal - bla[b + 2] * dzImaginary + bla[b + 4] * dcReal - bla[b + 6] * dcImaginary;
                    double nextImaginary = bla[b] * dzImaginary + bla[b + 2] * dzReal + bla[b + 4] * dcImaginary + bla[b + 6] * dcReal;
                    double deltaNorm = MathMagnitude(dzReal, dzImaginary);
                    double cNorm = MathMagnitude(dcReal, dcImaginary);
                    error = bla[b + 12] * error + bla[b + 9] * deltaNorm * deltaNorm
                        + bla[b + 10] * deltaNorm * cNorm + bla[b + 11] * cNorm * cNorm
                        + 1E-15 * (MathMagnitude(bla[b], bla[b + 2]) * deltaNorm + MathMagnitude(bla[b + 4], bla[b + 6]) * cNorm)
                        + MathMagnitude(bla[b + 4], bla[b + 6]) * coordinateError;
                    dzReal = nextReal;
                    dzImaginary = nextImaginary;
                    r += bestLength;
                    i += bestLength;
                    if (metricsEnabled) metrics[index * 3 + 1] += bestLength;
                    continue;
                }
            }

            double roundoff = 1E-15 * (2 * MathMagnitude(referenceReal[r], referenceImaginary[r]) * MathMagnitude(dzReal, dzImaginary)
                + deltaMagnitude + MathMagnitude(dcReal, dcImaginary)) + coordinateError;
            // Perturbation recurrence:
            //   dz' = 2 * z_ref * dz + dz^2 + dc
            // The reference orbit comes from MPFR on the CPU; each pixel's
            // delta orbit is independent and therefore maps well to the GPU.
            double nextDzReal =
                2.0 * (referenceReal[r] * dzReal - referenceImaginary[r] * dzImaginary) +
                dzReal * dzReal -
                dzImaginary * dzImaginary +
                dcReal;

            double nextDzImaginary =
                2.0 * (referenceReal[r] * dzImaginary + referenceImaginary[r] * dzReal) +
                2.0 * dzReal * dzImaginary +
                dcImaginary;

            dzReal = nextDzReal;
            dzImaginary = nextDzImaginary;
            error = (2.0 * NormBound(zReal, zImaginary) + error) * error + roundoff;
            r++;
            i++;
            if (metricsEnabled) metrics[index * 3 + 2]++;
        }

        if (i < maxIterations)
        {
            // State is local to this batch, while classifications retain global indices.
            state[s] = dzReal;
            state[s + 1] = dzImaginary;
            state[s + 2] = error;
            state[s + 3] = i;
            state[s + 4] = r;
            iterations[index] = EscapeClassification.Pending;
            return;
        }

        // Budget exhaustion isn't a proven interior result. Do not accept it
        // when accumulated trajectory uncertainty has become substantial.
        iterations[index] = error > 1E-6 ? EscapeClassification.Glitch : EscapeClassification.Interior;
    }

    private static double MathMagnitude(double real, double imaginary) =>
        (real < 0 ? -real : real) + (imaginary < 0 ? -imaginary : imaginary);

    // sqrt is a float intrinsic in this shader API. Inflate its result to
    // conservatively cover float conversion/rounding for the bounded orbit.
    private static double NormBound(double real, double imaginary) =>
        Hlsl.Sqrt((float)(real * real + imaginary * imaginary)) * 1.000001 + 1E-22;
}
