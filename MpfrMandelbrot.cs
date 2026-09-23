namespace MandelbrotGpu;

internal static class MpfrMandelbrot
{
    // Used only for the final repair pass. It is deliberately slower than the
    // GPU path, but provides a trusted answer for pixels whose perturbation
    // orbit became numerically invalid.
    private const uint PrecisionBits = 384;

    public static int EscapeIterations(MpfrComplex c, int maxIterations, uint precisionBits = PrecisionBits)
    {
        // Do not round c to double for an interior shortcut: repaired pixels
        // can sit arbitrarily close to a cardioid or bulb boundary.
        // Keep each MPFR destination alive for the entire orbit. All storage is
        // local to this call, so parallel repair workers never share scratch.
        // Preserve the original operation order and rounding at every step.
        using MpfrFloat zr = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat zi = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat zr2 = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat zi2 = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat radiusSquared = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat zrZi = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat twoZrZi = MpfrFloat.FromDouble(0, precisionBits);
        using MpfrFloat zr2MinusZi2 = MpfrFloat.FromDouble(0, precisionBits);

        for (int i = 0; i < maxIterations; i++)
        {
            NativeMpfr.mpfr_mul(zr2.Handle, zr.Handle, zr.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_mul(zi2.Handle, zi.Handle, zi.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_add(radiusSquared.Handle, zr2.Handle, zi2.Handle, NativeMpfr.RoundToNearest);

            if (radiusSquared.IsGreaterThan(4.0))
            {
                return i;
            }

            NativeMpfr.mpfr_mul(zrZi.Handle, zr.Handle, zi.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_mul_d(twoZrZi.Handle, zrZi.Handle, 2.0, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_sub(zr2MinusZi2.Handle, zr2.Handle, zi2.Handle, NativeMpfr.RoundToNearest);
            // Both old orbit components are dead after the cross product.
            NativeMpfr.mpfr_add(zr.Handle, zr2MinusZi2.Handle, c.Real.Handle, NativeMpfr.RoundToNearest);
            NativeMpfr.mpfr_add(zi.Handle, twoZrZi.Handle, c.Imaginary.Handle, NativeMpfr.RoundToNearest);
        }

        return EscapeClassification.Interior;
    }

}
