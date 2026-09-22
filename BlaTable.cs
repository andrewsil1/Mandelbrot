namespace MandelbrotGpu;

// Binary tree rooted at 1; leaves represent reference steps starting at m=1.
// Each node stores complex DD A/B, radius, three quadratic remainder bounds,
// and a Lipschitz bound (thirteen doubles).
// The radius bounds every intermediate delta and keeps the orbit within the
// escape disk during the skipped block. Zero radius disables an unsafe node.
internal sealed class BlaTable
{
    public const int Stride = 13;
    public int LeafCount { get; }
    public double[] Data { get; }
    public bool HasSkips { get; private set; }

    public BlaTable(DeepZoomReferenceOrbit orbit, double cBound, double epsilon)
    {
        LeafCount = Capacity(orbit.RealHigh.Length);
        Data = new double[2 * LeafCount * Stride];
        if (epsilon <= 0) return;
        for (int m = 1; m + 1 < orbit.Length; m++)
        {
            int node = LeafCount + m - 1;
            ComplexPair a = new(new DoubleDouble(orbit.RealHigh[m], orbit.RealLow[m]) * new DoubleDouble(2, 0),
                new DoubleDouble(orbit.ImaginaryHigh[m], orbit.ImaginaryLow[m]) * new DoubleDouble(2, 0));
            double magnitude = Math.Sqrt(orbit.RealHigh[m] * orbit.RealHigh[m] + orbit.ImaginaryHigh[m] * orbit.ImaginaryHigh[m]);
            Store(node, a, new(new(1, 0), new(0, 0)), Math.Min(epsilon * a.Norm, Math.Max(0, 2 - magnitude) * 0.25));
            Data[node * Stride + 9] = 1;
            Data[node * Stride + 12] = a.Norm + 2 * Data[node * Stride + 8];
        }

        for (int node = LeafCount - 1; node > 0; node--)
        {
            ComplexPair ax = Read(node * 2, 0), bx = Read(node * 2, 4);
            ComplexPair ay = Read(node * 2 + 1, 0), by = Read(node * 2 + 1, 4);
            double rx = Data[node * 2 * Stride + 8], ry = Data[(node * 2 + 1) * Stride + 8];
            // Inflate norms slightly and shrink the result to cover radius
            // construction rounding. This is an approximation tolerance,
            // not an interval-arithmetic proof of an escape count.
            double radius = ax.Norm > 0 ? Math.Min(rx, (ry - bx.Norm * cBound * (1 + 1E-12)) / (ax.Norm * (1 + 1E-12))) : 0;
            Store(node, ay * ax, ay * bx + by, Math.Max(0, radius) * 0.5);
            int x = node * 2 * Stride, y = (node * 2 + 1) * Stride, z = node * Stride;
            double an = ax.Norm * (1 + 1E-12), bn = bx.Norm * (1 + 1E-12);
            double ly = Data[y + 12];
            Data[z + 9] = (ly * Data[x + 9] + Data[y + 9] * an * an) * (1 + 1E-12);
            Data[z + 10] = (ly * Data[x + 10] + 2 * Data[y + 9] * an * bn + Data[y + 10] * an) * (1 + 1E-12);
            Data[z + 11] = (ly * Data[x + 11] + Data[y + 9] * bn * bn + Data[y + 10] * bn + Data[y + 11]) * (1 + 1E-12);
            Data[z + 12] = ly * Data[x + 12] * (1 + 1E-12);
            if (!double.IsFinite(Data[z + 9]) || !double.IsFinite(Data[z + 10]) || !double.IsFinite(Data[z + 11]) || !double.IsFinite(Data[z + 12]))
                Data[z + 8] = 0;
            if (Data[z + 8] > 0) HasSkips = true;
        }
    }

    public static int Capacity(int iterations)
    {
        int count = 1;
        while (count < Math.Max(1, iterations - 1)) count *= 2;
        return count;
    }

    private void Store(int node, ComplexPair a, ComplexPair b, double radius)
    {
        int offset = node * Stride;
        if (!double.IsFinite(a.Real.High) || !double.IsFinite(a.Real.Low) || !double.IsFinite(a.Imaginary.High) || !double.IsFinite(a.Imaginary.Low)
            || !double.IsFinite(b.Real.High) || !double.IsFinite(b.Real.Low) || !double.IsFinite(b.Imaginary.High) || !double.IsFinite(b.Imaginary.Low)
            || !double.IsFinite(radius)) return;
        Data[offset] = a.Real.High;
        Data[offset + 1] = a.Real.Low;
        Data[offset + 2] = a.Imaginary.High;
        Data[offset + 3] = a.Imaginary.Low;
        Data[offset + 4] = b.Real.High;
        Data[offset + 5] = b.Real.Low;
        Data[offset + 6] = b.Imaginary.High;
        Data[offset + 7] = b.Imaginary.Low;
        Data[offset + 8] = radius;
    }

    private ComplexPair Read(int node, int component)
    {
        int offset = node * Stride + component;
        return new(new(Data[offset], Data[offset + 1]), new(Data[offset + 2], Data[offset + 3]));
    }

    private readonly record struct ComplexPair(DoubleDouble Real, DoubleDouble Imaginary)
    {
        public double Norm => Math.Sqrt(Real.High * Real.High + Imaginary.High * Imaginary.High);
        public static ComplexPair operator +(ComplexPair a, ComplexPair b) => new(a.Real + b.Real, a.Imaginary + b.Imaginary);
        public static ComplexPair operator *(ComplexPair a, ComplexPair b) => new(a.Real * b.Real - a.Imaginary * b.Imaginary, a.Real * b.Imaginary + a.Imaginary * b.Real);
    }
}
