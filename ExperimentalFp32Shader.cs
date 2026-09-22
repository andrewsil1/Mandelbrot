using ComputeSharp;

namespace MandelbrotGpu;

// Bounded research kernel, never selected by the application. Scalar rescaled
// perturbation only: dz = scale*w, dc = scale*d. No BLA is approximated here.
// All arithmetic/state/reference loads in this kernel are FP32. Rejected pixels
// restart in the unchanged FP64/DD/MPFR pipeline rather than trusting a preview.
[ThreadGroupSize(64, 1, 1)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct ExperimentalFp32Shader(
    ReadWriteBuffer<int> iterations, ReadOnlyBuffer<float> referenceReal,
    ReadOnlyBuffer<float> referenceImaginary, ReadWriteBuffer<float> state,
    float initialScale, float left, float top, float stepX, float stepY,
    int referenceLength, int width, int offset, int sliceStart, int sliceEnd,
    int maxIterations) : IComputeShader
{
    // Engineering bounds with headroom for input conversion and non-FMA shader
    // arithmetic. These are not interval proofs. FTZ losses are covered by Floor.
    private const float Epsilon = 2E-6f;
    private const float Floor = 1E-30f;

    public void Execute()
    {
        int index = offset + ThreadIds.X;
        bool first = sliceStart == 0;
        if (!first && iterations[index] != EscapeClassification.Pending) return;
        int slot = ThreadIds.X * 8;
        float px = (index % width) + 0.5f;
        float py = (index / width) + 0.5f;
        float initialDr = left + px * stepX;
        float initialDi = top - py * stepY;
        float initialCe = Epsilon * (Abs(left) + Abs(top) + Abs(px * stepX) + Abs(py * stepY)) + Floor;
        float wr = first ? 0 : state[slot];
        float wi = first ? 0 : state[slot + 1];
        float error = first ? 0 : state[slot + 2];
        float scale = first ? initialScale : state[slot + 3];
        float dr = first ? initialDr : state[slot + 4];
        float di = first ? initialDi : state[slot + 5];
        float ce = first ? initialCe : state[slot + 6];
        int r = first ? 0 : (int)state[slot + 7];
        for (int i = sliceStart; i < sliceEnd; i++)
        {
            float rr = referenceReal[r];
            float ri = referenceImaginary[r];
            float dzr = scale * wr;
            float dzi = scale * wi;
            float zr = rr + dzr;
            float zi = ri + dzi;
            float norm = Abs(zr) + Abs(zi);
            float zm = zr * zr + zi * zi;
            float refNorm = Abs(rr) + Abs(ri);
            float absoluteError = scale * error + Epsilon * (refNorm + Abs(dzr) + Abs(dzi)) + Floor;
            // Ordered comparisons also reject NaN/Inf before accepting escapes.
            if (!(absoluteError < 1E-3f) || !(norm < 32) || !(error < 1E30f))
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }
            if (i > 0 && Abs(zm - 4) <= 32 * absoluteError + 8E-6f)
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }
            if (zm > 4)
            {
                iterations[index] = i;
                return;
            }
            float dm = dzr * dzr + dzi * dzi;
            if (r > 0 && (zm < dm || r + 1 >= referenceLength))
            {
                // Rebase at z_ref[0]=0 and renormalize at unit scale.
                wr = zr; wi = zi; scale = 1; error = absoluteError;
                dr = initialDr * initialScale;
                di = initialDi * initialScale;
                ce = initialCe * initialScale + Epsilon * (Abs(dr) + Abs(di)) + Floor;
                r = 0; rr = 0; ri = 0; refNorm = 0;
            }
            else if (i > 8 && zm < 1E-6f * (rr * rr + ri * ri))
            {
                iterations[index] = EscapeClassification.Glitch;
                return;
            }
            // Binary rescaling is exact for normal components; Floor accounts
            // conservatively for flushing tiny d/error components to zero.
            if (Abs(wr) + Abs(wi) > 4096)
            {
                wr *= 0.000244140625f; wi *= 0.000244140625f;
                dr *= 0.000244140625f; di *= 0.000244140625f;
                error = error * 0.000244140625f + Floor;
                ce = ce * 0.000244140625f + Floor;
                scale *= 4096;
            }
            float wn = Abs(wr) + Abs(wi);
            float roundoff = Epsilon * (2 * refNorm * wn + scale * wn * wn + Abs(dr) + Abs(di)) + ce + Floor;
            float nextReal = 2 * (rr * wr - ri * wi) + scale * (wr * wr - wi * wi) + dr;
            float nextImaginary = 2 * (rr * wi + ri * wr) + 2 * scale * wr * wi + di;
            error = ((2 * norm + scale * error) * error + roundoff) * (1 + Epsilon) + Floor;
            wr = nextReal; wi = nextImaginary;
            r++;
        }
        if (sliceEnd < maxIterations)
        {
            state[slot] = wr; state[slot + 1] = wi; state[slot + 2] = error;
            state[slot + 3] = scale; state[slot + 4] = dr; state[slot + 5] = di;
            state[slot + 6] = ce; state[slot + 7] = r;
            iterations[index] = EscapeClassification.Pending;
        }
        else iterations[index] = scale * error <= 1E-6f ? EscapeClassification.Interior : EscapeClassification.Glitch;
    }

    private static float Abs(float value) => value < 0 ? -value : value;
}
