using System.Runtime.InteropServices;

namespace MandelbrotGpu;

internal static partial class NativeMpfr
{
    // MPFR_RNDN: round to nearest, ties to even. Keeping one rounding mode
    // throughout avoids subtle disagreement between reference generation,
    // click mapping, and final repair.
    public const int RoundToNearest = 0;

    // These imports intentionally bind only the small MPFR surface needed by
    // the renderer. The NuGet package supplies libmpfr-4.dll and libgmp-10.dll
    // as native payloads copied next to the application at build time.
    [LibraryImport("libmpfr-4.dll")]
    public static partial void mpfr_init2(IntPtr value, uint precisionBits);

    [LibraryImport("libmpfr-4.dll")]
    public static partial void mpfr_clear(IntPtr value);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_set(IntPtr result, IntPtr value, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_set_d(IntPtr result, double value, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial double mpfr_get_d(IntPtr value, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_cmp_d(IntPtr left, double right);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_add(IntPtr result, IntPtr left, IntPtr right, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_add_d(IntPtr result, IntPtr left, double right, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_sub(IntPtr result, IntPtr left, IntPtr right, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_sub_d(IntPtr result, IntPtr left, double right, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_mul(IntPtr result, IntPtr left, IntPtr right, int rounding);

    [LibraryImport("libmpfr-4.dll")]
    public static partial int mpfr_mul_d(IntPtr result, IntPtr left, double right, int rounding);
}
