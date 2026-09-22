using ComputeSharp;

namespace MandelbrotGpu;

[ThreadGroupSize(64, 1, 1)]
// Compensated arithmetic relies on operation order and rounded intermediates.
[CompileOptions(CompileOptions.Default | CompileOptions.IeeeStrictness)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
public readonly partial struct MandelbrotPerturbationDoubleDoubleShader(
    ReadWriteBuffer<int> iterations,
    ReadOnlyBuffer<int> pixelIndices,
    ReadOnlyBuffer<double> referenceRealHigh,
    ReadOnlyBuffer<double> referenceRealLow,
    ReadOnlyBuffer<double> referenceImaginaryHigh,
    ReadOnlyBuffer<double> referenceImaginaryLow,
    ReadOnlyBuffer<double> bla,
    ReadWriteBuffer<int> metrics,
    ReadWriteBuffer<double> state,
    int blaLeaves,
    int referenceLength,
    int acceleration,
    int minimumBlaLength,
    bool metricsEnabled,
#if DEBUG
    bool blaProfileEnabled,
#endif
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
    int workOffset,
    int sliceStart,
    int sliceEnd,
    int maxIterations) : IComputeShader
{
    // Keep this threshold intentionally conservative. Double-double arithmetic
    // moves the failure point deeper, but the same perturbation cancellation
    // failure mode still creates bad escape counts if left unclassified.
    private const double GlitchThreshold = 1E-6;

    public void Execute()
    {
        // Output is compact; pixelIndices maps each work item back to the image.
        int slot = ThreadIds.X;
        int index = workOffset + slot;
        bool firstSlice = sliceStart == 0;
        if (!firstSlice && iterations[index] != EscapeClassification.Pending) return;
        int s = slot * 7;
#if DEBUG
        int metricBase = index * (blaProfileEnabled ? 21 : 3);
#else
        int metricBase = index * 3;
#endif
        if (metricsEnabled && firstSlice)
        {
            metrics[metricBase] = 0;
            metrics[metricBase + 1] = 0;
            metrics[metricBase + 2] = 0;
#if DEBUG
            if (blaProfileEnabled)
                for (int counter = 0; counter < 18; counter++) metrics[metricBase + 3 + counter] = 0;
#endif
        }
        int pixelIndex = pixelIndices[index];
        int x = pixelIndex % width;
        int y = pixelIndex / width;

        // Build the pixel delta in double-double form. Each value is passed as
        // high/low doubles because HLSL has no native double-double type.
        double xOffsetHigh = MultiplyByInt(stepXHigh, stepXLow, x, out double xOffsetLow);
        Add(xOffsetHigh, xOffsetLow, leftDeltaHigh, leftDeltaLow, out double dcRealHigh, out double dcRealLow);
        ScaleByPowerOfTwo(stepXHigh, stepXLow, 0.5, out double halfXHigh, out double halfXLow);
        Add(dcRealHigh, dcRealLow, halfXHigh, halfXLow, out dcRealHigh, out dcRealLow);

        double yOffsetHigh = MultiplyByInt(stepYHigh, stepYLow, y, out double yOffsetLow);
        Subtract(topDeltaHigh, topDeltaLow, yOffsetHigh, yOffsetLow, out double dcImaginaryHigh, out double dcImaginaryLow);
        ScaleByPowerOfTwo(stepYHigh, stepYLow, 0.5, out double halfYHigh, out double halfYLow);
        Subtract(dcImaginaryHigh, dcImaginaryLow, halfYHigh, halfYLow, out dcImaginaryHigh, out dcImaginaryLow);
        // Bound delta construction before cancellation, then propagate this
        // persistent c uncertainty through scalar and BLA steps alike.
        double coordinateError = 1E-30 * (MagnitudeBound(leftDeltaHigh, topDeltaHigh)
            + MagnitudeBound((x + 0.5) * stepXHigh, (y + 0.5) * stepYHigh));

        if (firstSlice)
        {
            Add(centerRealHigh, centerRealLow, dcRealHigh, dcRealLow, out double crHigh, out double crLow);
            Add(centerImaginaryHigh, centerImaginaryLow, dcImaginaryHigh, dcImaginaryLow, out double ciHigh, out double ciLow);

            // Use double-double arithmetic for the known interior tests as well,
            // then compare using the high component. These tests are only early
            // exits; failing them simply falls through to the orbit loop.
            Subtract(crHigh, crLow, 0.25, 0, out double shiftedXHigh, out double shiftedXLow);
            Square(shiftedXHigh, shiftedXLow, out double shiftedXSquaredHigh, out double shiftedXSquaredLow);
            Square(ciHigh, ciLow, out double ciSquaredHigh, out double ciSquaredLow);
            Add(shiftedXSquaredHigh, shiftedXSquaredLow, ciSquaredHigh, ciSquaredLow, out double qHigh, out double qLow);
            Add(qHigh, qLow, shiftedXHigh, shiftedXLow, out double qPlusShiftedHigh, out double qPlusShiftedLow);
            Multiply(qHigh, qLow, qPlusShiftedHigh, qPlusShiftedLow, out double cardioidLeftHigh, out _);
            ScaleByPowerOfTwo(ciSquaredHigh, ciSquaredLow, 0.25, out double cardioidRightHigh, out _);

            Add(crHigh, crLow, 1.0, 0, out double bulbXHigh, out double bulbXLow);
            Square(bulbXHigh, bulbXLow, out double bulbXSquaredHigh, out double bulbXSquaredLow);
            Add(bulbXSquaredHigh, bulbXSquaredLow, ciSquaredHigh, ciSquaredLow, out double bulbHigh, out _);

            // Leave a rounding margin: uncertain boundary pixels must iterate.
            if (firstSlice && (cardioidLeftHigh < cardioidRightHigh - 1E-14 || bulbHigh < 0.0625 - 1E-14))
            {
                iterations[index] = EscapeClassification.Interior;
                return;
            }
        }

        double dzRealHigh = firstSlice ? 0 : state[s];
        double dzRealLow = firstSlice ? 0 : state[s + 1];
        double dzImaginaryHigh = firstSlice ? 0 : state[s + 2];
        double dzImaginaryLow = firstSlice ? 0 : state[s + 3];

        int r = firstSlice ? 0 : (int)state[s + 6];
        int i = firstSlice ? 0 : (int)state[s + 5];
        double error = firstSlice ? 0 : state[s + 4];
        while (i < sliceEnd)
        {
            Add(referenceRealHigh[r], referenceRealLow[r], dzRealHigh, dzRealLow, out double zRealHigh, out double zRealLow);
            Add(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzImaginaryHigh, dzImaginaryLow, out double zImaginaryHigh, out double zImaginaryLow);
            double zMagnitude = zRealHigh * zRealHigh + zImaginaryHigh * zImaginaryHigh;
            double difference = zMagnitude - 4;
            // Most steps are far from the escape circle. Pay for a compensated
            // radius only where rounding could change the escape decision.
            if ((difference < 0 ? -difference : difference) < 1E-12)
            {
                Square(zRealHigh, zRealLow, out double radiusReal, out double radiusRealLow);
                Square(zImaginaryHigh, zImaginaryLow, out double radiusImaginary, out double radiusImaginaryLow);
                Add(radiusReal, radiusRealLow, radiusImaginary, radiusImaginaryLow, out double radiusHigh, out double radiusLow);
                Subtract(radiusHigh, radiusLow, 4, 0, out double escapeDifference, out double escapeDifferenceLow);
                difference = escapeDifference + escapeDifferenceLow;
            }
            if (i > 0 && (difference < 0 ? -difference : difference) < 8 * error + 1E-29)
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }

            if (difference > 0)
            {
                iterations[index] = i;
                return;
            }

            double referenceMagnitude = referenceRealHigh[r] * referenceRealHigh[r] + referenceImaginaryHigh[r] * referenceImaginaryHigh[r];

            double deltaMagnitude = dzRealHigh * dzRealHigh + dzImaginaryHigh * dzImaginaryHigh;
            // Preserve the actual pixel orbit while returning to Z_0=0. The
            // pixel iteration i never resets; only the reference position does.
            if (acceleration >= 1 && r > 0 && (zMagnitude < deltaMagnitude || r + 1 >= referenceLength))
            {
                dzRealHigh = zRealHigh;
                dzRealLow = zRealLow;
                dzImaginaryHigh = zImaginaryHigh;
                dzImaginaryLow = zImaginaryLow;
                error += 1E-30 * (MagnitudeBound(referenceRealHigh[r], referenceImaginaryHigh[r]) + MagnitudeBound(dzRealHigh, dzImaginaryHigh));
                r = 0;
                deltaMagnitude = zMagnitude;
                if (metricsEnabled) metrics[metricBase]++;
            }
            else if ((r + 1 >= referenceLength && i + 1 < maxIterations) || (i > 8 && zMagnitude < GlitchThreshold * referenceMagnitude))
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }

            if (acceleration >= 2 && r > 0)
            {
#if DEBUG
                if (blaProfileEnabled) metrics[metricBase + 3]++;
#endif
                int node = blaLeaves + r - 1;
                int length = 1;
                int best = 0;
                int bestLength = 0;
                while (node > 0 && r + length < referenceLength && i + length < maxIterations && i + length <= sliceEnd)
                {
                    double radius = bla[node * 13 + 8];
#if DEBUG
                    // Diagnostic candidates partition into disabled radius,
                    // raw delta, accumulated error, or eligible. Selection
                    // below still uses the unchanged numerical predicate.
                    if (blaProfileEnabled && length >= minimumBlaLength)
                    {
                        metrics[metricBase + 4]++;
                        double norm = MagnitudeBound(dzRealHigh, dzImaginaryHigh);
                        if (radius <= 0) metrics[metricBase + 5]++;
                        else if (norm >= radius) metrics[metricBase + 6]++;
                        else if (norm + error < radius) metrics[metricBase + 20]++;
                        else metrics[metricBase + 7]++;
                    }
#endif
                    if (length >= minimumBlaLength && radius > 0 && MagnitudeBound(dzRealHigh, dzImaginaryHigh) + error < radius)
                    {
                        best = node;
                        bestLength = length;
                    }
                    if ((node & 1) != 0)
                    {
#if DEBUG
                        if (blaProfileEnabled) metrics[metricBase + 10]++;
#endif
                        break;
                    }
                    node /= 2;
                    length *= 2;
                }
#if DEBUG
                if (blaProfileEnabled && node > 0)
                {
                    // One terminal bound reason, with reference/budget taking
                    // priority when several boundaries coincide.
                    if (r + length >= referenceLength) metrics[metricBase + 11]++;
                    else if (i + length >= maxIterations) metrics[metricBase + 12]++;
                    else if (i + length > sliceEnd) metrics[metricBase + 9]++;
                }
#endif
                if (best > 0)
                {
#if DEBUG
                    if (blaProfileEnabled)
                    {
                        metrics[metricBase + 8]++;
                        int bin = 10;
                        int block = bestLength;
                        while (block > 2 && bin < 16) { bin++; block /= 2; }
                        metrics[metricBase + 3 + bin]++;
                    }
#endif
                    int b = best * 13;
                    ComplexMultiply(bla[b], bla[b + 1], bla[b + 2], bla[b + 3], dzRealHigh, dzRealLow, dzImaginaryHigh, dzImaginaryLow,
                        out double azr, out double azrl, out double azi, out double azil);
                    ComplexMultiply(bla[b + 4], bla[b + 5], bla[b + 6], bla[b + 7], dcRealHigh, dcRealLow, dcImaginaryHigh, dcImaginaryLow,
                        out double bcr, out double bcrl, out double bci, out double bcil);
                    double deltaNorm = MagnitudeBound(dzRealHigh, dzImaginaryHigh);
                    double cNorm = MagnitudeBound(dcRealHigh, dcImaginaryHigh);
                    error = bla[b + 12] * error + bla[b + 9] * deltaNorm * deltaNorm
                        + bla[b + 10] * deltaNorm * cNorm + bla[b + 11] * cNorm * cNorm
                        + 1E-30 * (MagnitudeBound(bla[b], bla[b + 2]) * deltaNorm + MagnitudeBound(bla[b + 4], bla[b + 6]) * cNorm)
                        + MagnitudeBound(bla[b + 4], bla[b + 6]) * coordinateError;
                    Add(azr, azrl, bcr, bcrl, out dzRealHigh, out dzRealLow);
                    Add(azi, azil, bci, bcil, out dzImaginaryHigh, out dzImaginaryLow);
                    r += bestLength;
                    i += bestLength;
                    if (metricsEnabled) metrics[metricBase + 1] += bestLength;
                    continue;
                }
            }

            double roundoff = 1E-30 * (2 * MagnitudeBound(referenceRealHigh[r], referenceImaginaryHigh[r]) * MagnitudeBound(dzRealHigh, dzImaginaryHigh)
                + deltaMagnitude + MagnitudeBound(dcRealHigh, dcImaginaryHigh)) + coordinateError;
            // Double-double version of dz' = 2*z_ref*dz + dz^2 + dc.
            // The helpers below implement compensated add/multiply operations
            // that ComputeSharp can translate into shader code.
            Multiply(referenceRealHigh[r], referenceRealLow[r], dzRealHigh, dzRealLow, out double rzRealHigh, out double rzRealLow);
            Multiply(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzImaginaryHigh, dzImaginaryLow, out double izImaginaryHigh, out double izImaginaryLow);
            Subtract(rzRealHigh, rzRealLow, izImaginaryHigh, izImaginaryLow, out double linearRealHigh, out double linearRealLow);
            ScaleByPowerOfTwo(linearRealHigh, linearRealLow, 2.0, out linearRealHigh, out linearRealLow);

            Square(dzRealHigh, dzRealLow, out double dzRealSquaredHigh, out double dzRealSquaredLow);
            Square(dzImaginaryHigh, dzImaginaryLow, out double dzImaginarySquaredHigh, out double dzImaginarySquaredLow);
            Subtract(dzRealSquaredHigh, dzRealSquaredLow, dzImaginarySquaredHigh, dzImaginarySquaredLow, out double nonlinearRealHigh, out double nonlinearRealLow);

            Add(linearRealHigh, linearRealLow, nonlinearRealHigh, nonlinearRealLow, out double nextDzRealHigh, out double nextDzRealLow);
            Add(nextDzRealHigh, nextDzRealLow, dcRealHigh, dcRealLow, out nextDzRealHigh, out nextDzRealLow);

            Multiply(referenceRealHigh[r], referenceRealLow[r], dzImaginaryHigh, dzImaginaryLow, out double rzImaginaryHigh, out double rzImaginaryLow);
            Multiply(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzRealHigh, dzRealLow, out double izRealHigh, out double izRealLow);
            Add(rzImaginaryHigh, rzImaginaryLow, izRealHigh, izRealLow, out double linearImaginaryHigh, out double linearImaginaryLow);
            ScaleByPowerOfTwo(linearImaginaryHigh, linearImaginaryLow, 2.0, out linearImaginaryHigh, out linearImaginaryLow);

            Multiply(dzRealHigh, dzRealLow, dzImaginaryHigh, dzImaginaryLow, out double nonlinearImaginaryHigh, out double nonlinearImaginaryLow);
            ScaleByPowerOfTwo(nonlinearImaginaryHigh, nonlinearImaginaryLow, 2.0, out nonlinearImaginaryHigh, out nonlinearImaginaryLow);

            Add(linearImaginaryHigh, linearImaginaryLow, nonlinearImaginaryHigh, nonlinearImaginaryLow, out double nextDzImaginaryHigh, out double nextDzImaginaryLow);
            Add(nextDzImaginaryHigh, nextDzImaginaryLow, dcImaginaryHigh, dcImaginaryLow, out nextDzImaginaryHigh, out nextDzImaginaryLow);

            dzRealHigh = nextDzRealHigh;
            dzRealLow = nextDzRealLow;
            dzImaginaryHigh = nextDzImaginaryHigh;
            dzImaginaryLow = nextDzImaginaryLow;
            error = (2 * NormBound(zRealHigh, zImaginaryHigh) + error) * error + roundoff;
            r++;
            i++;
            if (metricsEnabled) metrics[metricBase + 2]++;
        }

        if (i < maxIterations)
        {
            state[s] = dzRealHigh;
            state[s + 1] = dzRealLow;
            state[s + 2] = dzImaginaryHigh;
            state[s + 3] = dzImaginaryLow;
            state[s + 4] = error;
            state[s + 5] = i;
            state[s + 6] = r;
            iterations[index] = EscapeClassification.Pending;
            return;
        }
        iterations[index] = error > 1E-6 ? EscapeClassification.Glitch : EscapeClassification.Interior;
    }

    private static double MagnitudeBound(double real, double imaginary) =>
        (real < 0 ? -real : real) + (imaginary < 0 ? -imaginary : imaginary);

    private static double NormBound(double real, double imaginary) =>
        Hlsl.Sqrt((float)(real * real + imaginary * imaginary)) * 1.000001 + 1E-22;

    private static void ComplexMultiply(double ar, double arl, double ai, double ail,
        double br, double brl, double bi, double bil,
        out double rr, out double rrl, out double ri, out double ril)
    {
        Multiply(ar, arl, br, brl, out double x, out double xl);
        Multiply(ai, ail, bi, bil, out double y, out double yl);
        Subtract(x, xl, y, yl, out rr, out rrl);
        Multiply(ar, arl, bi, bil, out x, out xl);
        Multiply(ai, ail, br, brl, out y, out yl);
        Add(x, xl, y, yl, out ri, out ril);
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

    internal static void ScaleByPowerOfTwo(double high, double low, double factor, out double resultHigh, out double resultLow)
    {
        // Callers use only 2, 1/2, or 1/4 on normalized pairs. In this range,
        // component-wise binary scaling is exact and preserves normalization.
        // Use general multiplication near underflow/overflow and for nonfinite data.
        const double minimum = 8.900295434028806E-308; // Four times the smallest normal double.
        const double maximum = 1E290; // Conservatively below overflow of the legacy Dekker split.
        double absoluteHigh = high < 0 ? -high : high;
        double absoluteLow = low < 0 ? -low : low;
        if ((high == 0 || (absoluteHigh >= minimum && absoluteHigh <= maximum))
            && (low == 0 || (absoluteLow >= minimum && absoluteLow <= maximum)))
        {
            resultHigh = high * factor;
            resultLow = low * factor;
        }
        else
        {
            MultiplyByDouble(high, low, factor, out resultHigh, out resultLow);
        }
    }

    internal static void Square(double high, double low, out double resultHigh, out double resultLow)
    {
        // Recover the high square's residual with explicit FMA. Preserve the
        // general multiply's cross-term order rather than reassociating terms.
        double product = high * high;
        double productError = Hlsl.FusedMultiplyAdd(high, high, -product);
        double lowCross = high * low;
        double error = productError + lowCross + lowCross;
        resultHigh = product + error;
        resultLow = error - (resultHigh - product);
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

    internal static void Multiply(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
    {
        // FMA product: multiply the high components, recover their
        // rounding error, then add the cross terms involving the low parts.
        double product = aHigh * bHigh;
        double error = TwoProductError(aHigh, bHigh, product) + aHigh * bLow + aLow * bHigh;

        resultHigh = product + error;
        resultLow = error - (resultHigh - product);
    }

    private static double TwoProductError(double a, double b, double product)
    {
        return Hlsl.FusedMultiplyAdd(a, b, -product);
    }
}
