#define __GroupSize__get_X 64
#define __GroupSize__get_Y 1
#define __GroupSize__get_Z 1
#define __MandelbrotGpu_EscapeClassification__Pending -3
#define __MandelbrotGpu_EscapeClassification__Interior -1
#define __MandelbrotGpu_EscapeClassification__Glitch -2
#define __MandelbrotGpu_ExperimentalFmaDoubleDoubleShader__GlitchThreshold 1E-06

static double MagnitudeBound(double real, double imaginary);

static double NormBound(double real, double imaginary);

static void ComplexMultiply(double ar, double arl, double ai, double ail, double br, double brl, double bi, double bil, out double rr, out double rrl, out double ri, out double ril);

static double MultiplyByInt(double high, double low, int value, out double resultLow);

static void MultiplyByDouble(double high, double low, double value, out double resultHigh, out double resultLow);

static void ScaleByPowerOfTwo(double high, double low, double factor, out double resultHigh, out double resultLow);

static void Square(double high, double low, out double resultHigh, out double resultLow);

static void Add(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow);

static void Subtract(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow);

static void Multiply(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow);

static double TwoProductError(double a, double b, double product);

cbuffer _ : register(b0)
{
    uint __x;
    uint __y;
    uint __z;
    int blaLeaves;
    int referenceLength;
    int acceleration;
    int minimumBlaLength;
    bool metricsEnabled;
    double centerRealHigh;
    double centerRealLow;
    double centerImaginaryHigh;
    double centerImaginaryLow;
    double leftDeltaHigh;
    double leftDeltaLow;
    double topDeltaHigh;
    double topDeltaLow;
    double stepXHigh;
    double stepXLow;
    double stepYHigh;
    double stepYLow;
    int width;
    int workOffset;
    int sliceStart;
    int sliceEnd;
    int maxIterations;
}

RWStructuredBuffer<int> iterations : register(u0);

StructuredBuffer<int> pixelIndices : register(t0);

StructuredBuffer<double> referenceRealHigh : register(t1);

StructuredBuffer<double> referenceRealLow : register(t2);

StructuredBuffer<double> referenceImaginaryHigh : register(t3);

StructuredBuffer<double> referenceImaginaryLow : register(t4);

StructuredBuffer<double> bla : register(t5);

RWStructuredBuffer<int> metrics : register(u1);

RWStructuredBuffer<double> state : register(u2);

static double MagnitudeBound(double real, double imaginary)
{
    return (real < 0 ? -real : real) + (imaginary < 0 ? -imaginary : imaginary);
}

static double NormBound(double real, double imaginary)
{
    return sqrt((float)(real * real + imaginary * imaginary)) * 1.000001L + 1E-22L;
}

static void ComplexMultiply(double ar, double arl, double ai, double ail, double br, double brl, double bi, double bil, out double rr, out double rrl, out double ri, out double ril)
{
    double x;
    double xl;
    double y;
    double yl;
    Multiply(ar, arl, br, brl, x, xl);
    Multiply(ai, ail, bi, bil, y, yl);
    Subtract(x, xl, y, yl, rr, rrl);
    Multiply(ar, arl, bi, bil, x, xl);
    Multiply(ai, ail, br, brl, y, yl);
    Add(x, xl, y, yl, ri, ril);
}

static double MultiplyByInt(double high, double low, int value, out double resultLow)
{
    double resultHigh;
    MultiplyByDouble(high, low, value, resultHigh, resultLow);
    return resultHigh;
}

static void MultiplyByDouble(double high, double low, double value, out double resultHigh, out double resultLow)
{
    Multiply(high, low, value, 0, resultHigh, resultLow);
}

static void ScaleByPowerOfTwo(double high, double low, double factor, out double resultHigh, out double resultLow)
{
    const double minimum = 8.900295434028806E-308L;
    const double maximum = 1E+290L;
    double absoluteHigh = high < 0 ? -high : high;
    double absoluteLow = low < 0 ? -low : low;
    if ((high == 0 || (absoluteHigh >= minimum && absoluteHigh <= maximum)) && (low == 0 || (absoluteLow >= minimum && absoluteLow <= maximum)))
    {
        resultHigh = high * factor;
        resultLow = low * factor;
    }
    else
    {
        MultiplyByDouble(high, low, factor, resultHigh, resultLow);
    }
}

static void Square(double high, double low, out double resultHigh, out double resultLow)
{
    double product = high * high;
    double productError = fma(high, high, -product);
    double lowCross = high * low;
    double error = productError + lowCross + lowCross;
    resultHigh = product + error;
    resultLow = error - (resultHigh - product);
}

static void Add(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
{
    double sum = aHigh + bHigh;
    double v = sum - aHigh;
    double error = (aHigh - (sum - v)) + (bHigh - v) + aLow + bLow;
    resultHigh = sum + error;
    resultLow = error - (resultHigh - sum);
}

static void Subtract(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
{
    Add(aHigh, aLow, -bHigh, -bLow, resultHigh, resultLow);
}

static void Multiply(double aHigh, double aLow, double bHigh, double bLow, out double resultHigh, out double resultLow)
{
    double product = aHigh * bHigh;
    double error = TwoProductError(aHigh, bHigh, product) + aHigh * bLow + aLow * bHigh;
    resultHigh = product + error;
    resultLow = error - (resultHigh - product);
}

static double TwoProductError(double a, double b, double product)
{
    return fma(a, b, -product);
}

[NumThreads(__GroupSize__get_X, __GroupSize__get_Y, __GroupSize__get_Z)]
void Execute(uint3 ThreadIds : SV_DispatchThreadID)
{
    if (ThreadIds.x < __x && ThreadIds.y < __y && ThreadIds.z < __z)
    {
        double xOffsetLow;
        double dcRealHigh;
        double dcRealLow;
        double halfXHigh;
        double halfXLow;
        double yOffsetLow;
        double dcImaginaryHigh;
        double dcImaginaryLow;
        double halfYHigh;
        double halfYLow;
        double crHigh;
        double crLow;
        double ciHigh;
        double ciLow;
        double shiftedXHigh;
        double shiftedXLow;
        double shiftedXSquaredHigh;
        double shiftedXSquaredLow;
        double ciSquaredHigh;
        double ciSquaredLow;
        double qHigh;
        double qLow;
        double qPlusShiftedHigh;
        double qPlusShiftedLow;
        double cardioidLeftHigh;
        double __implicit25;
        double cardioidRightHigh;
        double __implicit27;
        double bulbXHigh;
        double bulbXLow;
        double bulbXSquaredHigh;
        double bulbXSquaredLow;
        double bulbHigh;
        double __implicit33;
        double zRealHigh;
        double zRealLow;
        double zImaginaryHigh;
        double zImaginaryLow;
        double radiusReal;
        double radiusRealLow;
        double radiusImaginary;
        double radiusImaginaryLow;
        double radiusHigh;
        double radiusLow;
        double escapeDifference;
        double escapeDifferenceLow;
        double azr;
        double azrl;
        double azi;
        double azil;
        double bcr;
        double bcrl;
        double bci;
        double bcil;
        double rzRealHigh;
        double rzRealLow;
        double izImaginaryHigh;
        double izImaginaryLow;
        double linearRealHigh;
        double linearRealLow;
        double dzRealSquaredHigh;
        double dzRealSquaredLow;
        double dzImaginarySquaredHigh;
        double dzImaginarySquaredLow;
        double nonlinearRealHigh;
        double nonlinearRealLow;
        double nextDzRealHigh;
        double nextDzRealLow;
        double rzImaginaryHigh;
        double rzImaginaryLow;
        double izRealHigh;
        double izRealLow;
        double linearImaginaryHigh;
        double linearImaginaryLow;
        double nonlinearImaginaryHigh;
        double nonlinearImaginaryLow;
        double nextDzImaginaryHigh;
        double nextDzImaginaryLow;
        int slot = ThreadIds.x;
        int index = workOffset + slot;
        bool firstSlice = sliceStart == 0;
        if (!firstSlice && iterations[index] != __MandelbrotGpu_EscapeClassification__Pending)
            return;
        int s = slot * 7;
        int metricBase = index * 3;
        if (metricsEnabled && firstSlice)
        {
            metrics[metricBase] = 0;
            metrics[metricBase + 1] = 0;
            metrics[metricBase + 2] = 0;
        }

        int pixelIndex = pixelIndices[index];
        int x = pixelIndex % width;
        int y = pixelIndex / width;
        double xOffsetHigh = MultiplyByInt(stepXHigh, stepXLow, x, xOffsetLow);
        Add(xOffsetHigh, xOffsetLow, leftDeltaHigh, leftDeltaLow, dcRealHigh, dcRealLow);
        ScaleByPowerOfTwo(stepXHigh, stepXLow, 0.5L, halfXHigh, halfXLow);
        Add(dcRealHigh, dcRealLow, halfXHigh, halfXLow, dcRealHigh, dcRealLow);
        double yOffsetHigh = MultiplyByInt(stepYHigh, stepYLow, y, yOffsetLow);
        Subtract(topDeltaHigh, topDeltaLow, yOffsetHigh, yOffsetLow, dcImaginaryHigh, dcImaginaryLow);
        ScaleByPowerOfTwo(stepYHigh, stepYLow, 0.5L, halfYHigh, halfYLow);
        Subtract(dcImaginaryHigh, dcImaginaryLow, halfYHigh, halfYLow, dcImaginaryHigh, dcImaginaryLow);
        double coordinateError = 1E-30L * (MagnitudeBound(leftDeltaHigh, topDeltaHigh) + MagnitudeBound((x + 0.5L) * stepXHigh, (y + 0.5L) * stepYHigh));
        if (firstSlice)
        {
            Add(centerRealHigh, centerRealLow, dcRealHigh, dcRealLow, crHigh, crLow);
            Add(centerImaginaryHigh, centerImaginaryLow, dcImaginaryHigh, dcImaginaryLow, ciHigh, ciLow);
            Subtract(crHigh, crLow, 0.25L, 0, shiftedXHigh, shiftedXLow);
            Square(shiftedXHigh, shiftedXLow, shiftedXSquaredHigh, shiftedXSquaredLow);
            Square(ciHigh, ciLow, ciSquaredHigh, ciSquaredLow);
            Add(shiftedXSquaredHigh, shiftedXSquaredLow, ciSquaredHigh, ciSquaredLow, qHigh, qLow);
            Add(qHigh, qLow, shiftedXHigh, shiftedXLow, qPlusShiftedHigh, qPlusShiftedLow);
            Multiply(qHigh, qLow, qPlusShiftedHigh, qPlusShiftedLow, cardioidLeftHigh, __implicit25);
            ScaleByPowerOfTwo(ciSquaredHigh, ciSquaredLow, 0.25L, cardioidRightHigh, __implicit27);
            Add(crHigh, crLow, 1.0L, 0, bulbXHigh, bulbXLow);
            Square(bulbXHigh, bulbXLow, bulbXSquaredHigh, bulbXSquaredLow);
            Add(bulbXSquaredHigh, bulbXSquaredLow, ciSquaredHigh, ciSquaredLow, bulbHigh, __implicit33);
            if (firstSlice && (cardioidLeftHigh < cardioidRightHigh - 1E-14L || bulbHigh < 0.0625L - 1E-14L))
            {
                iterations[index] = __MandelbrotGpu_EscapeClassification__Interior;
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
            Add(referenceRealHigh[r], referenceRealLow[r], dzRealHigh, dzRealLow, zRealHigh, zRealLow);
            Add(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzImaginaryHigh, dzImaginaryLow, zImaginaryHigh, zImaginaryLow);
            double zMagnitude = zRealHigh * zRealHigh + zImaginaryHigh * zImaginaryHigh;
            double difference = zMagnitude - 4;
            if ((difference < 0 ? -difference : difference) < 1E-12L)
            {
                Square(zRealHigh, zRealLow, radiusReal, radiusRealLow);
                Square(zImaginaryHigh, zImaginaryLow, radiusImaginary, radiusImaginaryLow);
                Add(radiusReal, radiusRealLow, radiusImaginary, radiusImaginaryLow, radiusHigh, radiusLow);
                Subtract(radiusHigh, radiusLow, 4, 0, escapeDifference, escapeDifferenceLow);
                difference = escapeDifference + escapeDifferenceLow;
            }

            if (i > 0 && (difference < 0 ? -difference : difference) < 8 * error + 1E-29L)
            {
                iterations[index] = __MandelbrotGpu_EscapeClassification__Glitch;
                return;
            }

            if (difference > 0)
            {
                iterations[index] = i;
                return;
            }

            double referenceMagnitude = referenceRealHigh[r] * referenceRealHigh[r] + referenceImaginaryHigh[r] * referenceImaginaryHigh[r];
            double deltaMagnitude = dzRealHigh * dzRealHigh + dzImaginaryHigh * dzImaginaryHigh;
            if (acceleration >= 1 && r > 0 && (zMagnitude < deltaMagnitude || r + 1 >= referenceLength))
            {
                dzRealHigh = zRealHigh;
                dzRealLow = zRealLow;
                dzImaginaryHigh = zImaginaryHigh;
                dzImaginaryLow = zImaginaryLow;
                error += 1E-30L * (MagnitudeBound(referenceRealHigh[r], referenceImaginaryHigh[r]) + MagnitudeBound(dzRealHigh, dzImaginaryHigh));
                r = 0;
                deltaMagnitude = zMagnitude;
                if (metricsEnabled)
                    metrics[metricBase]++;
            }
            else if ((r + 1 >= referenceLength && i + 1 < maxIterations) || (i > 8 && zMagnitude < __MandelbrotGpu_ExperimentalFmaDoubleDoubleShader__GlitchThreshold * referenceMagnitude))
            {
                iterations[index] = __MandelbrotGpu_EscapeClassification__Glitch;
                return;
            }

            if (acceleration >= 2 && r > 0)
            {
                int node = blaLeaves + r - 1;
                int __reserved__length = 1;
                int best = 0;
                int bestLength = 0;
                while (node > 0 && r + __reserved__length < referenceLength && i + __reserved__length < maxIterations && i + __reserved__length <= sliceEnd)
                {
                    double radius = bla[node * 13 + 8];
                    if (__reserved__length >= minimumBlaLength && radius > 0 && MagnitudeBound(dzRealHigh, dzImaginaryHigh) + error < radius)
                    {
                        best = node;
                        bestLength = __reserved__length;
                    }

                    if ((node & 1) != 0)
                    {
                        break;
                    }

                    node /= 2;
                    __reserved__length *= 2;
                }

                if (best > 0)
                {
                    int b = best * 13;
                    ComplexMultiply(bla[b], bla[b + 1], bla[b + 2], bla[b + 3], dzRealHigh, dzRealLow, dzImaginaryHigh, dzImaginaryLow, azr, azrl, azi, azil);
                    ComplexMultiply(bla[b + 4], bla[b + 5], bla[b + 6], bla[b + 7], dcRealHigh, dcRealLow, dcImaginaryHigh, dcImaginaryLow, bcr, bcrl, bci, bcil);
                    double deltaNorm = MagnitudeBound(dzRealHigh, dzImaginaryHigh);
                    double cNorm = MagnitudeBound(dcRealHigh, dcImaginaryHigh);
                    error = bla[b + 12] * error + bla[b + 9] * deltaNorm * deltaNorm + bla[b + 10] * deltaNorm * cNorm + bla[b + 11] * cNorm * cNorm + 1E-30L * (MagnitudeBound(bla[b], bla[b + 2]) * deltaNorm + MagnitudeBound(bla[b + 4], bla[b + 6]) * cNorm) + MagnitudeBound(bla[b + 4], bla[b + 6]) * coordinateError;
                    Add(azr, azrl, bcr, bcrl, dzRealHigh, dzRealLow);
                    Add(azi, azil, bci, bcil, dzImaginaryHigh, dzImaginaryLow);
                    r += bestLength;
                    i += bestLength;
                    if (metricsEnabled)
                        metrics[metricBase + 1] += bestLength;
                    continue;
                }
            }

            double roundoff = 1E-30L * (2 * MagnitudeBound(referenceRealHigh[r], referenceImaginaryHigh[r]) * MagnitudeBound(dzRealHigh, dzImaginaryHigh) + deltaMagnitude + MagnitudeBound(dcRealHigh, dcImaginaryHigh)) + coordinateError;
            Multiply(referenceRealHigh[r], referenceRealLow[r], dzRealHigh, dzRealLow, rzRealHigh, rzRealLow);
            Multiply(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzImaginaryHigh, dzImaginaryLow, izImaginaryHigh, izImaginaryLow);
            Subtract(rzRealHigh, rzRealLow, izImaginaryHigh, izImaginaryLow, linearRealHigh, linearRealLow);
            ScaleByPowerOfTwo(linearRealHigh, linearRealLow, 2.0L, linearRealHigh, linearRealLow);
            Square(dzRealHigh, dzRealLow, dzRealSquaredHigh, dzRealSquaredLow);
            Square(dzImaginaryHigh, dzImaginaryLow, dzImaginarySquaredHigh, dzImaginarySquaredLow);
            Subtract(dzRealSquaredHigh, dzRealSquaredLow, dzImaginarySquaredHigh, dzImaginarySquaredLow, nonlinearRealHigh, nonlinearRealLow);
            Add(linearRealHigh, linearRealLow, nonlinearRealHigh, nonlinearRealLow, nextDzRealHigh, nextDzRealLow);
            Add(nextDzRealHigh, nextDzRealLow, dcRealHigh, dcRealLow, nextDzRealHigh, nextDzRealLow);
            Multiply(referenceRealHigh[r], referenceRealLow[r], dzImaginaryHigh, dzImaginaryLow, rzImaginaryHigh, rzImaginaryLow);
            Multiply(referenceImaginaryHigh[r], referenceImaginaryLow[r], dzRealHigh, dzRealLow, izRealHigh, izRealLow);
            Add(rzImaginaryHigh, rzImaginaryLow, izRealHigh, izRealLow, linearImaginaryHigh, linearImaginaryLow);
            ScaleByPowerOfTwo(linearImaginaryHigh, linearImaginaryLow, 2.0L, linearImaginaryHigh, linearImaginaryLow);
            Multiply(dzRealHigh, dzRealLow, dzImaginaryHigh, dzImaginaryLow, nonlinearImaginaryHigh, nonlinearImaginaryLow);
            ScaleByPowerOfTwo(nonlinearImaginaryHigh, nonlinearImaginaryLow, 2.0L, nonlinearImaginaryHigh, nonlinearImaginaryLow);
            Add(linearImaginaryHigh, linearImaginaryLow, nonlinearImaginaryHigh, nonlinearImaginaryLow, nextDzImaginaryHigh, nextDzImaginaryLow);
            Add(nextDzImaginaryHigh, nextDzImaginaryLow, dcImaginaryHigh, dcImaginaryLow, nextDzImaginaryHigh, nextDzImaginaryLow);
            dzRealHigh = nextDzRealHigh;
            dzRealLow = nextDzRealLow;
            dzImaginaryHigh = nextDzImaginaryHigh;
            dzImaginaryLow = nextDzImaginaryLow;
            error = (2 * NormBound(zRealHigh, zImaginaryHigh) + error) * error + roundoff;
            r++;
            i++;
            if (metricsEnabled)
                metrics[metricBase + 2]++;
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
            iterations[index] = __MandelbrotGpu_EscapeClassification__Pending;
            return;
        }

        iterations[index] = error > 1E-06L ? __MandelbrotGpu_EscapeClassification__Glitch : __MandelbrotGpu_EscapeClassification__Interior;
    }
}