namespace MandelbrotGpu;

internal static class MpfrMandelbrot
{
    // Used only for the final repair pass. It is deliberately slower than the
    // GPU path, but provides a trusted answer for pixels whose perturbation
    // orbit became numerically invalid.
    private const uint PrecisionBits = 384;

    public static int EscapeIterations(MpfrComplex c, int maxIterations)
    {
        if (IsDefinitelyInsideKnownBulbs(c))
        {
            return EscapeClassification.Interior;
        }

        MpfrFloat zr = MpfrFloat.FromDouble(0, PrecisionBits);
        MpfrFloat zi = MpfrFloat.FromDouble(0, PrecisionBits);

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

    private static bool IsDefinitelyInsideKnownBulbs(MpfrComplex c)
    {
        // This quick rejection is evaluated in double precision. It is used
        // only for the well-known large cardioid/bulb interiors where the test
        // is far from the tiny deep-zoom deltas repaired here.
        double cr = c.Real.ToDouble();
        double ci = c.Imaginary.ToDouble();
        double shiftedX = cr - 0.25;
        double q = shiftedX * shiftedX + ci * ci;

        return q * (q + shiftedX) <= 0.25 * ci * ci ||
               (cr + 1.0) * (cr + 1.0) + ci * ci <= 0.0625;
    }
}
