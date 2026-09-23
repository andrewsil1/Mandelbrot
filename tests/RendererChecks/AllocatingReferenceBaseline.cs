namespace MandelbrotGpu;

internal sealed class AllocatingReferenceBaseline
{
    // The reference orbit is the high-precision backbone of the perturbation
    // renderers. Each GPU thread only tracks its delta from this orbit, so the
    // CPU computes the shared orbit once with enough precision to survive much
    // deeper zooms than direct double arithmetic.
    private const uint PrecisionBits = 384;

    private AllocatingReferenceBaseline(
        double[] realHigh,
        double[] realLow,
        double[] imaginaryHigh,
        double[] imaginaryLow)
    {
        RealHigh = realHigh;
        RealLow = realLow;
        ImaginaryHigh = imaginaryHigh;
        ImaginaryLow = imaginaryLow;
        Length = realHigh.Length;
    }

    public double[] RealHigh { get; }

    public double[] RealLow { get; }

    public double[] ImaginaryHigh { get; }

    public double[] ImaginaryLow { get; }
    public int Length { get; private set; }

    public static AllocatingReferenceBaseline Build(MpfrComplex referencePoint, int maxIterations)
    {
        // Store one z value for each iteration. The GPU samples these arrays
        // before advancing the per-pixel perturbation recurrence.
        double[] realHigh = new double[maxIterations];
        double[] realLow = new double[maxIterations];
        double[] imaginaryHigh = new double[maxIterations];
        double[] imaginaryLow = new double[maxIterations];

        MpfrFloat zr = MpfrFloat.FromDouble(0, PrecisionBits);
        MpfrFloat zi = MpfrFloat.FromDouble(0, PrecisionBits);
        int length = maxIterations;

        try
        {
            for (int i = 0; i < maxIterations; i++)
            {
                // Collapse the MPFR value to a double-double pair for GPU
                // consumption. ComputeSharp can translate the scalar math in
                // the shader; it cannot run MPFR itself on the GPU.
                DoubleDouble real = zr.ToDoubleDouble();
                DoubleDouble imaginary = zi.ToDoubleDouble();
                realHigh[i] = real.High;
                realLow[i] = real.Low;
                imaginaryHigh[i] = imaginary.High;
                imaginaryLow[i] = imaginary.Low;
                // Stop before an escaping reference grows without bound. Its
                // last finite value remains available to reconstruct/rebase.
                if (real.High * real.High + imaginary.High * imaginary.High > 4.0)
                {
                    length = i + 1;
                    break;
                }

                // Advance z = z^2 + c in MPFR. The temporary values are owned
                // by this loop iteration and disposed immediately after the
                // next persistent z values have been produced.
                using MpfrFloat zr2 = zr.Multiply(zr);
                using MpfrFloat zi2 = zi.Multiply(zi);
                using MpfrFloat zrZi = zr.Multiply(zi);
                using MpfrFloat twoZrZi = zrZi.Multiply(2.0);
                using MpfrFloat zr2MinusZi2 = zr2.Subtract(zi2);

                MpfrFloat nextZr = zr2MinusZi2.Add(referencePoint.Real);
                MpfrFloat nextZi = twoZrZi.Add(referencePoint.Imaginary);

                zr.Dispose();
                zi.Dispose();
                zr = nextZr;
                zi = nextZi;
            }
        }
        finally
        {
            // zr and zi are reassigned inside the loop, so a finally block is
            // the safest place to release whichever instances are current.
            zr.Dispose();
            zi.Dispose();
        }

        return new AllocatingReferenceBaseline(realHigh, realLow, imaginaryHigh, imaginaryLow) { Length = length };
    }
}
