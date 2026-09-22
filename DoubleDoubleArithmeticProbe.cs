using ComputeSharp;

namespace MandelbrotGpu;

// Small harness-only dispatch that executes the production shader's actual helpers.
[ThreadGroupSize(64, 1, 1)]
[CompileOptions(CompileOptions.Default | CompileOptions.IeeeStrictness)]
[RequiresDoublePrecisionSupport]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct DoubleDoubleArithmeticProbe(
    ReadOnlyBuffer<double> inputs, ReadWriteBuffer<double> output) : IComputeShader
{
    public void Execute()
    {
        int p = ThreadIds.X * 4;
        int o = ThreadIds.X * 10;
        double ah = inputs[p], al = inputs[p + 1], bh = inputs[p + 2], bl = inputs[p + 3];
        MandelbrotPerturbationDoubleDoubleShader.Multiply(ah, al, bh, bl, out double rh, out double rl);
        MandelbrotPerturbationDoubleDoubleShader.Square(ah, al, out double sh, out double sl);
        output[o] = rh;
        output[o + 1] = rl;
        output[o + 2] = sh;
        output[o + 3] = sl;
        MandelbrotPerturbationDoubleDoubleShader.ScaleByPowerOfTwo(ah, al, 2, out rh, out rl);
        output[o + 4] = rh; output[o + 5] = rl;
        MandelbrotPerturbationDoubleDoubleShader.ScaleByPowerOfTwo(ah, al, 0.5, out rh, out rl);
        output[o + 6] = rh; output[o + 7] = rl;
        MandelbrotPerturbationDoubleDoubleShader.ScaleByPowerOfTwo(ah, al, 0.25, out rh, out rl);
        output[o + 8] = rh; output[o + 9] = rl;
    }
}
