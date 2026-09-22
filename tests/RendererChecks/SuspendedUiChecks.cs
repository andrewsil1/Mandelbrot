using System.Reflection;
using System.Runtime.ExceptionServices;
using MandelbrotGpu;

internal static class SuspendedUiChecks
{
    public static void Run()
    {
        Exception? failure = null;
        Thread thread = new(() =>
        {
            object? window = null;
            Type type = typeof(MandelbrotRenderer).Assembly.GetType("MandelbrotGpu.MainWindow")!;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            object? Field(string name) => type.GetField(name, flags)!.GetValue(window);
            void Call(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(window, args);
            try
            {
                // Never show the window or fire Loaded: allocate only its
                // bitmap, then test suspended handlers without a GPU render.
                window = Activator.CreateInstance(type)!;
                Call("EnsureRenderTarget", true);
                object? bitmap = Field("bitmap");
                object? viewport = Field("view");
                type.GetField("gpuRenderingSuspended", flags)!.SetValue(window, true);
                foreach (string handler in new[] { "ResetButton_Click", "BackButton_Click",
                    "FractalImage_MouseLeftButtonDown", "FractalImage_MouseRightButtonDown",
                    "RenderHost_SizeChanged", "ResizeTimer_Tick" })
                {
                    Call(handler, null, null);
                    if (!ReferenceEquals(bitmap, Field("bitmap")) || !ReferenceEquals(viewport, Field("view"))
                        || (bool)Field("isRendering")! || (bool)Field("renderQueued")!)
                        throw new Exception($"Suspended handler {handler} changed the image/view or queued work.");
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try { if (window is not null) type.GetMethod("Close")!.Invoke(window, null); }
                catch (Exception ex) { failure ??= ex; }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("GPU-free suspended UI checks passed: Reset/Back/click/resize preserve the bitmap/view and submit no work.");
    }
}
