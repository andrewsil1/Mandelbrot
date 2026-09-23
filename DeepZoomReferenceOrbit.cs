namespace MandelbrotGpu;

public sealed class DeepZoomReferenceOrbit
{
    // The reference orbit is the high-precision backbone of the perturbation
    // renderers. Each GPU thread only tracks its delta from this orbit, so the
    // CPU computes the shared orbit once with enough precision to survive much
    // deeper zooms than direct double arithmetic.
    private const uint PrecisionBits = 384;

    private DeepZoomReferenceOrbit(
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

    public static DeepZoomReferenceOrbit Build(MpfrComplex referencePoint, int maxIterations)
    {
        // Store one z value for each iteration. The GPU samples these arrays
        // before advancing the per-pixel perturbation recurrence.
        double[] realHigh = new double[maxIterations];
        double[] realLow = new double[maxIterations];
        double[] imaginaryHigh = new double[maxIterations];
        double[] imaginaryLow = new double[maxIterations];

        using MpfrFloat zr = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat zi = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat zr2 = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat zi2 = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat zrZi = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat twoZrZi = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat zr2MinusZi2 = MpfrFloat.FromDouble(0, PrecisionBits);
        using MpfrFloat residual = MpfrFloat.FromDouble(0, PrecisionBits);
        int length = maxIterations;

        // Reuse one residual for both DD conversions. Neither conversion nor
        // the recurrence changes precision or operation/rounding order.
        DoubleDouble Split(MpfrFloat value)
        {
            double high = value.ToDouble();
            NativeMpfr.mpfr_sub_d(residual.Handle, value.Handle, high, NativeMpfr.RoundToNearest);
            return new DoubleDouble(high, residual.ToDouble());
        }
        for (int i = 0; i < maxIterations; i++)
        {
            // Collapse the MPFR value to a double-double pair for GPU
            // consumption. ComputeSharp can translate the scalar math in
            // the shader; it cannot run MPFR itself on the GPU.
            DoubleDouble real = Split(zr);
            DoubleDouble imaginary = Split(zi);
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

            NativeMpfr.mpfr_mul(zr2.Handle, zr.Handle, zr.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_mul(zi2.Handle, zi.Handle, zi.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_mul(zrZi.Handle, zr.Handle, zi.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_mul_d(twoZrZi.Handle, zrZi.Handle, 2.0, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_sub(zr2MinusZi2.Handle, zr2.Handle, zi2.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_add(zr.Handle, zr2MinusZi2.Handle, referencePoint.Real.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_add(zi.Handle, twoZrZi.Handle, referencePoint.Imaginary.Handle, NativeMpfr.RoundToNearest);
        }

        return new DeepZoomReferenceOrbit(realHigh, realLow, imaginaryHigh, imaginaryLow) { Length = length };
    }
}
