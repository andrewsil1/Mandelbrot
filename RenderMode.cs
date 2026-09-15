namespace MandelbrotGpu;

public enum RenderMode
{
    // Direct one-thread-per-pixel FP64 iteration.
    DirectFloat64,

    // MPFR reference orbit plus FP64 perturbation deltas on the GPU.
    PerturbationFloat64,

    // MPFR reference orbit plus software double-double perturbation on the GPU.
    PerturbationDoubleDouble
}
