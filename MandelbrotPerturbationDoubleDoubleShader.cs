using ComputeSharp;

namespace MandelbrotGpu;

[ThreadGroupSize(DefaultThreadGroupSizes.XY)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotPerturbationDoubleDoubleShader(
    ReadWriteBuffer<int> iterations,
    ReadOnlyBuffer<double> referenceRealHigh,
    ReadOnlyBuffer<double> referenceRealLow,
    ReadOnlyBuffer<double> referenceImaginaryHigh,
    ReadOnlyBuffer<double> referenceImaginaryLow,
    double centerRealHigh,
    double centerRealLow,
    double centerImaginaryHigh,
    double centerImaginaryLow,
    double leftDeltaHigh,
    double leftDeltaLow,
    double topDeltaHigh,
    double topDeltaLow,
    double stepXHigh,
    double stepXLow,
    double stepYHigh,
    double stepYLow,
    int width,
    int maxIterations) : IComputeShader
{
    // Keep this threshold intentionally conservative. Double-double arithmetic
    // moves the failure point deeper, but the same perturbation cancellation
    // failure mode still creates bad escape counts if left unclassified.
    private const double GlitchThreshold = 1E-6;

    public void Execute()
    {
        int x = ThreadIds.X;
        int y = ThreadIds.Y;
        int index = y * width + x;

        // Build the pixel delta in double-double form. Each value is passed as
        // high/low doubles because HLSL has no native double-double type.
        double xOffsetHigh = MultiplyByInt(stepXHigh, stepXLow, x, out double xOffsetLow);
        Add(xOffsetHigh, xOffsetLow, leftDeltaHigh, leftDeltaLow, out double dcRealHigh, out double dcRealLow);

        double yOffsetHigh = MultiplyByInt(stepYHigh, stepYLow, y, out double yOffsetLow);
        Subtract(topDeltaHigh, topDeltaLow, yOffsetHigh, yOffsetLow, out double dcImaginaryHigh, out double dcImaginaryLow);

        Add(centerRealHigh, centerRealLow, dcRealHigh, dcRealLow, out double crHigh, out double crLow);
        Add(centerImaginaryHigh, centerImaginaryLow, dcImaginaryHigh, dcImaginaryLow, out double ciHigh, out double ciLow);

        // Use double-double arithmetic for the known interior tests as well,
        // then compare using the high component. These tests are only early
        // exits; failing them simply falls through to the orbit loop.
        Subtract(crHigh, crLow, 0.25, 0, out double shiftedXHigh, out double shiftedXLow);
        Multiply(shiftedXHigh, shiftedXLow, shiftedXHigh, shiftedXLow, out double shiftedXSquaredHigh, out double shiftedXSquaredLow);
        Multiply(ciHigh, ciLow, ciHigh, ciLow, out double ciSquaredHigh, out double ciSquaredLow);
        Add(shiftedXSquaredHigh, shiftedXSquaredLow, ciSquaredHigh, ciSquaredLow, out double qHigh, out double qLow);
        Add(qHigh, qLow, shiftedXHigh, shiftedXLow, out double qPlusShiftedHigh, out double qPlusShiftedLow);
        Multiply(qHigh, qLow, qPlusShiftedHigh, qPlusShiftedLow, out double cardioidLeftHigh, out _);
        MultiplyByDouble(ciSquaredHigh, ciSquaredLow, 0.25, out double cardioidRightHigh, out _);

        Add(crHigh, crLow, 1.0, 0, out double bulbXHigh, out double bulbXLow);
        Multiply(bulbXHigh, bulbXLow, bulbXHigh, bulbXLow, out double bulbXSquaredHigh, out double bulbXSquaredLow);
        Add(bulbXSquaredHigh, bulbXSquaredLow, ciSquaredHigh, ciSquaredLow, out double bulbHigh, out _);

        if (cardioidLeftHigh <= cardioidRightHigh || bulbHigh <= 0.0625)
        {
            iterations[index] = EscapeClassification.Interior;
            return;
        }

        double dzRealHigh = 0;
        double dzRealLow = 0;
        double dzImaginaryHigh = 0;
        double dzImaginaryLow = 0;

        for (int i = 0; i < maxIterations; i++)
        {
            Add(referenceRealHigh[i], referenceRealLow[i], dzRealHigh, dzRealLow, out double zRealHigh, out _);
            Add(referenceImaginaryHigh[i], referenceImaginaryLow[i], dzImaginaryHigh, dzImaginaryLow, out double zImaginaryHigh, out _);
            double zMagnitude = zRealHigh * zRealHigh + zImaginaryHigh * zImaginaryHigh;

            if (zMagnitude > 4.0)
            {
                iterations[index] = i;
                return;
            }

            double referenceMagnitude = referenceRealHigh[i] * referenceRealHigh[i] + referenceImaginaryHigh[i] * referenceImaginaryHigh[i];

            // The same cancellation problem exists for double-double math,
            // just later. Mark it for MPFR repair before it becomes visible
            // as frame-wide histogram speckle.
            if (i > 8 && zMagnitude < GlitchThreshold * referenceMagnitude)
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }

            // Double-double version of dz' = 2*z_ref*dz + dz^2 + dc.
            // The helpers below implement compensated add/multiply operations
            // that ComputeSharp can translate into shader code.
            Multiply(referenceRealHigh[i], referenceRealLow[i], dzRealHigh, dzRealLow, out double rzRealHigh, out double rzRealLow);
            Multiply(referenceImaginaryHigh[i], referenceImaginaryLow[i], dzImaginaryHigh, dzImaginaryLow, out double izImaginaryHigh, out double izImaginaryLow);
            Subtract(rzRealHigh, rzRealLow, izImaginaryHigh, izImaginaryLow, out double linearRealHigh, out double linearRealLow);
            MultiplyByDouble(linearRealHigh, linearRealLow, 2.0, out linearRealHigh, out linearRealLow);

            Multiply(dzRealHigh, dzRealLow, dzRealHigh, dzRealLow, out double dzRealSquaredHigh, out double dzRealSquaredLow);
            Multiply(dzImaginaryHigh, dzImaginaryLow, dzImaginaryHigh, dzImaginaryLow, out double dzImaginarySquaredHigh, out double dzImaginarySquaredLow);
            Subtract(dzRealSquaredHigh, dzRealSquaredLow, dzImaginarySquaredHigh, dzImaginarySquaredLow, out double nonlinearRealHigh, out double nonlinearRealLow);

            Add(linearRealHigh, linearRealLow, nonlinearRealHigh, nonlinearRealLow, out double nextDzRealHigh, out double nextDzRealLow);
            Add(nextDzRealHigh, nextDzRealLow, dcRealHigh, dcRealLow, out nextDzRealHigh, out nextDzRealLow);

            Multiply(referenceRealHigh[i], referenceRealLow[i], dzImaginaryHigh, dzImaginaryLow, out double rzImaginaryHigh, out double rzImaginaryLow);
            Multiply(referenceImaginaryHigh[i], referenceImaginaryLow[i], dzRealHigh, dzRealLow, out double izRealHigh, out double izRealLow);
            Add(rzImaginaryHigh, rzImaginaryLow, izRealHigh, izRealLow, out double linearImaginaryHigh, out double linearImaginaryLow);
            MultiplyByDouble(linearImaginaryHigh, linearImaginaryLow, 2.0, out linearImaginaryHigh, out linearImaginaryLow);

            Multiply(dzRealHigh, dzRealLow, dzImaginaryHigh, dzImaginaryLow, out double nonlinearImaginaryHigh, out double nonlinearImaginaryLow);
            MultiplyByDouble(nonlinearImaginaryHigh, nonlinearImaginaryLow, 2.0, out nonlinearImaginaryHigh, out nonlinearImaginaryLow);

            Add(linearImaginaryHigh, linearImaginaryLow, nonlinearImaginaryHigh, nonlinearImaginaryLow, out double nextDzImaginaryHigh, out double nextDzImaginaryLow);
            Add(nextDzImaginaryHigh, nextDzImaginaryLow, dcImaginaryHigh, dcImaginaryLow, out nextDzImaginaryHigh, out nextDzImaginaryLow);

            dzRealHigh = nextDzRealHigh;
            dzRealLow = nextDzRealLow;
            dzImaginaryHigh = nextDzImaginaryHigh;
            dzImaginaryLow = nextDzImaginaryLow;
        }

        iterations[index] = EscapeClassification.Interior;
    }

    private static double MultiplyByInt(double high, double low, int value, out double resultLow)
    {
        MultiplyByDouble(high, low, value, out double resultHigh, out resultLow);
        return resultHigh;
    }

    private static void MultiplyByDouble(double high, double low, double value, out double resultHigh, out double resultLow)
    {
        Multiply(high, low, value, 0, out resultHigh, out resultLow);
    }

    private static void Add(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
    {
        // Knuth-style TwoSum. The returned pair represents the rounded sum plus
        // the rounding error that still fits in another double.
        double sum = aHigh + bHigh;
        double v = sum - aHigh;
        double error = (aHigh - (sum - v)) + (bHigh - v) + aLow + bLow;

        resultHigh = sum + error;
        resultLow = error - (resultHigh - sum);
    }

    private static void Subtract(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
    {
        Add(aHigh, aLow, -bHigh, -bLow, out resultHigh, out resultLow);
    }

    private static void Multiply(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
    {
        // Dekker-style product: multiply the high components, recover their
        // rounding error, then add the cross terms involving the low parts.
        double product = aHigh * bHigh;
        double error = TwoProductError(aHigh, bHigh, product) + aHigh * bLow + aLow * bHigh;

        resultHigh = product + error;
        resultLow = error - (resultHigh - product);
    }

    private static double TwoProductError(double a, double b, double product)
    {
        // Split each operand into high/low halves so the product error can be
        // reconstructed using only ordinary double operations.
        const double splitter = 134217729.0;

        double aSplit = splitter * a;
        double aHigh = aSplit - (aSplit - a);
        double aLow = a - aHigh;

        double bSplit = splitter * b;
        double bHigh = bSplit - (bSplit - b);
        double bLow = b - bHigh;

        return ((aHigh * bHigh - product) + aHigh * bLow + aLow * bHigh) + aLow * bLow;
    }
}
