namespace MandelbrotGpu;

public sealed class GpuRenderSuspendedException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);
