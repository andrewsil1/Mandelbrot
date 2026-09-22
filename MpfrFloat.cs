using System.Runtime.InteropServices;

namespace MandelbrotGpu;

public sealed class MpfrFloat : IDisposable
{
    // MPFR uses an opaque C struct whose size is ABI-specific. The Windows
    // package used by this project fits in this allocation; all access is
    // through MPFR functions, never by reading fields from managed code.
    private const int StructBytes = 64;

    private bool disposed;

    private MpfrFloat(uint precisionBits)
    {
        PrecisionBits = precisionBits;
        // Allocate the native mpfr_t storage and initialize it with the target
        // precision before any MPFR operation touches the handle.
        Handle = Marshal.AllocHGlobal(StructBytes);
        NativeMpfr.mpfr_init2(Handle, precisionBits);
    }

    internal IntPtr Handle { get; }

    public uint PrecisionBits { get; }

    public static MpfrFloat FromDouble(double value, uint precisionBits)
    {
        MpfrFloat result = new(precisionBits);
        NativeMpfr.mpfr_set_d(result.Handle, value, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Clone()
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_set(result.Handle, Handle, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Add(MpfrFloat other)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_add(result.Handle, Handle, other.Handle, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Add(double value)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_add_d(result.Handle, Handle, value, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Subtract(MpfrFloat other)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_sub(result.Handle, Handle, other.Handle, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Subtract(double value)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_sub_d(result.Handle, Handle, value, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Multiply(MpfrFloat other)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_mul(result.Handle, Handle, other.Handle, NativeMpfr.RoundToNearest);
        return result;
    }

    public MpfrFloat Multiply(double value)
    {
        MpfrFloat result = new(PrecisionBits);
        NativeMpfr.mpfr_mul_d(result.Handle, Handle, value, NativeMpfr.RoundToNearest);
        return result;
    }

    public double ToDouble()
    {
        return NativeMpfr.mpfr_get_d(Handle, NativeMpfr.RoundToNearest);
    }

    public bool IsGreaterThan(double value)
    {
        return NativeMpfr.mpfr_cmp_d(Handle, value) > 0;
    }

    public bool IsZero => NativeMpfr.mpfr_cmp_d(Handle, 0) == 0;

    // Exact sum of IEEE-754 bit patterns, including coordinate bits omitted by
    // the status display. Refuse values outside the finite expansion range.
    public string[] ToExactBinary64Terms()
    {
        List<string> parts = [];
        MpfrFloat remainder = Clone();
        try
        {
            while (!remainder.IsZero)
            {
                double part = remainder.ToDouble();
                if (!double.IsFinite(part) || part == 0 || parts.Count > 32)
                    throw new InvalidOperationException("Coordinate cannot be exported losslessly as binary64 summands.");
                parts.Add(BitConverter.DoubleToUInt64Bits(part).ToString("X16"));
                MpfrFloat next = remainder.Subtract(part);
                remainder.Dispose();
                remainder = next;
            }
            return parts.ToArray();
        }
        finally { remainder.Dispose(); }
    }

    public DoubleDouble ToDoubleDouble()
    {
        // Preserve more than one double of information for GPU code by taking
        // the nearest double as the high component, then converting the MPFR
        // residual to the low component.
        double high = ToDouble();
        using MpfrFloat residual = Subtract(high);

        return new DoubleDouble(high, residual.ToDouble());
    }

    public string ToDisplayString()
    {
        return ToDouble().ToString("G17");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        // mpfr_clear releases MPFR-owned limbs; FreeHGlobal releases the C
        // struct storage allocated by the wrapper.
        NativeMpfr.mpfr_clear(Handle);
        Marshal.FreeHGlobal(Handle);
        disposed = true;
    }
}
