using System.ComponentModel;

namespace MandelbrotGpu;

internal static class GpuDeviceFailure
{
    public static bool TryGetCode(Exception exception, out int code)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            code = current is Win32Exception native ? native.NativeErrorCode : current.HResult;
            if (code is unchecked((int)0x887A0005) or unchecked((int)0x887A0006)
                or unchecked((int)0x887A0007) or unchecked((int)0x887A0020)) return true;
        }
        code = 0;
        return false;
    }
}
