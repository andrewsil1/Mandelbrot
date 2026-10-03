using ComputeSharp;

namespace MandelbrotGpu;

// One frame owns one workspace. Reference retries reuse these resources; only
// the active prefix of the pixel/output buffers is transferred each time.
internal sealed class PerturbationBuffers : IDisposable
{
    private readonly ResourceOwnership resources = new();

    public PerturbationBuffers(GraphicsDevice device, int pixelCount, int maxIterations)
    {
        try
        {
            Output = resources.Own(device.AllocateReadWriteBuffer<int>(pixelCount));
            PixelIndices = resources.Own(device.AllocateReadOnlyBuffer<int>(pixelCount));
            RealHigh = resources.Own(device.AllocateReadOnlyBuffer<double>(maxIterations));
            RealLow = resources.Own(device.AllocateReadOnlyBuffer<double>(maxIterations));
            ImaginaryHigh = resources.Own(device.AllocateReadOnlyBuffer<double>(maxIterations));
            ImaginaryLow = resources.Own(device.AllocateReadOnlyBuffer<double>(maxIterations));
            Bla = resources.Own(device.AllocateReadOnlyBuffer<double>(2 * BlaTable.Capacity(maxIterations) * BlaTable.Stride));
            MetricsEnabled = RendererDiagnostics.Enabled && Environment.GetEnvironmentVariable("MANDELBROT_METRICS") == "1";
#if DEBUG
            BlaProfileEnabled = MetricsEnabled && BlaPassProfile.Enabled;
#endif
            Metrics = resources.Own(device.AllocateReadWriteBuffer<int>(MetricsEnabled ? pixelCount * (BlaProfileEnabled ? BlaPassProfile.MetricStride : 3) : 1));
            int batchPixels = GpuDispatchPolicy.BatchPixels(RenderMode.PerturbationDoubleDouble,
                GpuDispatchPolicy.SliceIterations(maxIterations));
            State = resources.Own(device.AllocateReadWriteBuffer<double>(Math.Min(pixelCount, batchPixels) * 7));
            Readback = resources.Own(device.AllocateReadBackBuffer<int>(Math.Min(pixelCount, batchPixels)));
        }
        catch (Exception allocationFailure)
        {
            try { resources.Dispose(); }
            catch (Exception cleanupFailure) { throw new AggregateException(allocationFailure, cleanupFailure); }
            throw;
        }
    }

    public ReadWriteBuffer<int> Output { get; }
    public ReadOnlyBuffer<int> PixelIndices { get; }
    public ReadOnlyBuffer<double> RealHigh { get; }
    public ReadOnlyBuffer<double> RealLow { get; }
    public ReadOnlyBuffer<double> ImaginaryHigh { get; }
    public ReadOnlyBuffer<double> ImaginaryLow { get; }
    public ReadOnlyBuffer<double> Bla { get; }
    public ReadWriteBuffer<int> Metrics { get; }
    public ReadWriteBuffer<double> State { get; }
    public ReadBackBuffer<int> Readback { get; }
    public bool MetricsEnabled { get; }
    public bool BlaProfileEnabled { get; }

    public void Dispose()
    {
        resources.Dispose();
    }
}

// Registers each successful allocation immediately, including during construction.
internal sealed class ResourceOwnership : IDisposable
{
    private readonly List<IDisposable> resources = [];

    public T Own<T>(T resource) where T : IDisposable
    {
        resources.Add(resource);
        return resource;
    }

    public void Dispose()
    {
        List<Exception> errors = [];
        for (int index = resources.Count - 1; index >= 0; index--)
        {
            try { resources[index].Dispose(); }
            catch (Exception ex) { errors.Add(ex); }
        }
        resources.Clear();
        if (errors.Count > 0) throw new AggregateException(errors);
    }
}
