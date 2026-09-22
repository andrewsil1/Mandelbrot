namespace MandelbrotGpu;

// Represents a number as the unevaluated sum High + Low. This is enough to
// extend the useful GPU perturbation depth without moving arbitrary-precision
// arithmetic onto the GPU.
public readonly record struct DoubleDouble(double High, double Low)
{
    public static DoubleDouble operator +(DoubleDouble a, DoubleDouble b)
    {
        double sum = a.High + b.High;
        double v = sum - a.High;
        double error = (a.High - (sum - v)) + (b.High - v) + a.Low + b.Low;
        double high = sum + error;
        return new(high, error - (high - sum));
    }

    public static DoubleDouble operator -(DoubleDouble a, DoubleDouble b) => a + new DoubleDouble(-b.High, -b.Low);

    public static DoubleDouble operator *(DoubleDouble a, DoubleDouble b)
    {
        double product = a.High * b.High;
        double error = Math.FusedMultiplyAdd(a.High, b.High, -product) + a.High * b.Low + a.Low * b.High;
        double high = product + error;
        return new(high, error - (high - product));
    }
}
