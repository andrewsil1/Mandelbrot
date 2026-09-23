namespace MandelbrotGpu;

internal static class AllocatingMpfrBaseline
{
    // Used only for the final repair pass. It is deliberately slower than the
    // GPU path, but provides a trusted answer for pixels whose perturbation
    // orbit became numerically invalid.
    private const uint PrecisionBits = 384;

    public static int EscapeIterations(MpfrComplex c, int maxIterations, uint precisionBits = PrecisionBits)
    {
        // Do not round c to double for an interior shortcut: repaired pixels
        // can sit arbitrarily close to a cardioid or bulb boundary.
        MpfrFloat zr = MpfrFloat.FromDouble(0, precisionBits);
        MpfrFloat zi = MpfrFloat.FromDouble(0, precisionBits);

        try
        {
            for (int i = 0; i < maxIterations; i++)
            {
                using MpfrFloat zr2 = zr.Multiply(zr);
                using MpfrFloat zi2 = zi.Multiply(zi);
                using MpfrFloat radiusSquared = zr2.Add(zi2);

                if (radiusSquared.IsGreaterThan(4.0))
                {
                    return i;
                }

                using MpfrFloat zrZi = zr.Multiply(zi);
                using MpfrFloat twoZrZi = zrZi.Multiply(2.0);
                using MpfrFloat zr2MinusZi2 = zr2.Subtract(zi2);

                MpfrFloat nextZr = zr2MinusZi2.Add(c.Real);
                MpfrFloat nextZi = twoZrZi.Add(c.Imaginary);

                zr.Dispose();
                zi.Dispose();
                zr = nextZr;
                zi = nextZi;
            }
        }
        finally
        {
            zr.Dispose();
            zi.Dispose();
        }

        return EscapeClassification.Interior;
    }

}
