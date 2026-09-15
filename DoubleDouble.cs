namespace MandelbrotGpu;

// Represents a number as the unevaluated sum High + Low. This is enough to
// extend the useful GPU perturbation depth without moving arbitrary-precision
// arithmetic onto the GPU.
public readonly record struct DoubleDouble(double High, double Low);
