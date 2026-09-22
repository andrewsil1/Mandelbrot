using ComputeSharp;
using ComputeSharp.Descriptors;
using MandelbrotGpu;

internal static class DoubleDoubleArithmeticChecks
{
    public static void Run()
    {
        if (!Source<MandelbrotPerturbationDoubleDoubleShader>().Contains("fma("))
            throw new Exception("Production DD shader does not emit explicit FMA.");
        List<double> values = [];
        void Add(double ah, double al, double bh, double bl) => values.AddRange([ah, al, bh, bl]);
        // The first product has a nonzero residual that an unfused a*b-p loses.
        Add(1 + Math.ScaleB(1, -27), 0, 1 - Math.ScaleB(1, -27), 0);
        foreach (double v in new[] { 0, -0.0, double.Epsilon, -double.Epsilon, Math.ScaleB(1, -1022),
            Math.ScaleB(1, -511), Math.ScaleB(1, 511), 1E290, double.MaxValue, double.PositiveInfinity, double.NaN })
            Add(v, 0, 0.5, 0);
        Random random = new(0xF1A);
        Add(1, Math.ScaleB(1, -53), -1, Math.ScaleB(1, -54));
        Add(Math.ScaleB(1, -1022), double.Epsilon, 2, 0);
        for (int i = 0; i < 1024; i++)
        {
            int e = random.Next(-1022, 1022);
            var a = new DoubleDouble(Math.ScaleB(random.NextDouble() + 0.5, e) * (i % 2 == 0 ? 1 : -1), 0)
                + new DoubleDouble(Math.ScaleB(random.NextDouble() - 0.5, e - 53), 0);
            Add(a.High, a.Low, 0.5, 0);
        }
        int normalStart = values.Count;
        for (int i = 0; i < 2048; i++)
        {
            int e = random.Next(-400, 401), f = random.Next(-400, 401);
            var a = new DoubleDouble(Math.ScaleB(random.NextDouble() + 0.5, e), 0) + new DoubleDouble(Math.ScaleB(random.NextDouble() - 0.5, e - 53), 0);
            var b = new DoubleDouble(Math.ScaleB(random.NextDouble() + 0.5, f) * (i % 2 == 0 ? 1 : -1), 0) + new DoubleDouble(Math.ScaleB(random.NextDouble() - 0.5, f - 53), 0);
            Add(a.High, a.Low, b.High, b.Low);
        }
        var data = values.ToArray();
        using var inputs = GraphicsDevice.GetDefault().AllocateReadOnlyBuffer(data);
        using var output = GraphicsDevice.GetDefault().AllocateReadWriteBuffer<double>(data.Length / 4 * 10);
        GraphicsDevice.GetDefault().For(data.Length / 4, new DoubleDoubleArithmeticProbe(inputs, output));
        double[] result = output.ToArray();
        if (result[1] != -Math.ScaleB(1, -54)) throw new Exception("GPU product residual does not demonstrate fused arithmetic.");
        int mpfr = 0;
        for (int p = 0; p < data.Length; p += 4)
        {
            int o = p / 4 * 10;
            double ah = data[p], al = data[p + 1], bh = data[p + 2], bl = data[p + 3];
            double product = ah * bh;
            double error = Math.FusedMultiplyAdd(ah, bh, -product) + ah * bl + al * bh;
            double rh = product + error, rl = error - (rh - product);
            double square = ah * ah, cross = ah * al;
            double se = Math.FusedMultiplyAdd(ah, ah, -square) + cross + cross;
            double sh = square + se, sl = se - (sh - square);
            double[] expected = [rh, rl, sh, sl];
            for (int j = 0; j < 4; j++)
                if (result[o + j] != expected[j] && !(double.IsNaN(result[o + j]) && double.IsNaN(expected[j])))
                    throw new Exception($"GPU/CPU FMA arithmetic mismatch at {p / 4}:{j}.");
            double[] factors = [2, 0.5, 0.25];
            for (int j = 0; j < factors.Length; j++)
            {
                double factor = factors[j];
                bool Normal(double v) => v == 0 || (Math.Abs(v) >= 8.900295434028806E-308 && Math.Abs(v) <= 1E290);
                double eh, el;
                if (Normal(ah) && Normal(al)) { eh = ah * factor; el = al * factor; }
                else
                {
                    double sp = ah * factor;
                    double err = Math.FusedMultiplyAdd(ah, factor, -sp) + ah * 0 + al * factor;
                    eh = sp + err; el = err - (eh - sp);
                }
                bool Equal(double a, double b) => a == b || (double.IsNaN(a) && double.IsNaN(b));
                if (!Equal(result[o + 4 + j * 2], eh) || !Equal(result[o + 5 + j * 2], el))
                    throw new Exception($"GPU/CPU binary scaling mismatch at {p / 4}, factor {factor}.");
            }
            if (p < normalStart) continue; // extremes are semantic checks, not relative-error claims
            using var a0 = MpfrFloat.FromDouble(ah, 768); using var a = a0.Add(al);
            using var b0 = MpfrFloat.FromDouble(bh, 768); using var b = b0.Add(bl);
            using var ep = a.Multiply(b); using var es = a.Multiply(a);
            CheckError(ep, result[o], result[o + 1]); CheckError(es, result[o + 2], result[o + 3]); mpfr += 2;
            for (int j = 0; j < factors.Length; j++)
            {
                using var scaled = a.Multiply(factors[j]);
                CheckError(scaled, result[o + 4 + j * 2], result[o + 5 + j * 2]); mpfr++;
            }
        }
        Console.WriteLine($"Production FMA arithmetic passed: {data.Length / 4} GPU pairs, {mpfr} MPFR product/square/scaling checks, explicit fused residual.");
    }

    private static string Source<T>() where T : struct, IComputeShader, IComputeShaderDescriptor<T> => T.HlslSource;

    private static void CheckError(MpfrFloat expected, double high, double low)
    {
        using var a = MpfrFloat.FromDouble(high, 768); using var actual = a.Add(low); using var error = actual.Subtract(expected);
        if (!double.IsFinite(high) || !double.IsFinite(low) || Math.Abs(error.ToDouble()) > Math.Abs(expected.ToDouble()) * Math.ScaleB(1, -100))
            throw new Exception("FMA DD arithmetic exceeded 2^-100 relative error.");
    }
}
