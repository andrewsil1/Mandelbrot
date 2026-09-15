namespace MandelbrotGpu;

// Owns a pair of MPFR floats that represent one complex-plane coordinate. This
// type follows the same ownership rule as MpfrFloat: callers dispose it when
// they are finished with the native allocations.
public sealed record MpfrComplex(MpfrFloat Real, MpfrFloat Imaginary) : IDisposable
{
    public void Dispose()
    {
        Real.Dispose();
        Imaginary.Dispose();
    }
}
