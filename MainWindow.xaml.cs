using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MandelbrotGpu;

public partial class MainWindow : Window
{
    // The starting bitmap size is only a fallback. Once the window is loaded,
    // rendering uses the actual WPF host size multiplied by the display DPI.
    private const int InitialImageWidth = 1600;
    private const int InitialImageHeight = 1050;

    // Left and right click zoom symmetrically by four so a right click roughly
    // backs out one left-click step around the selected point.
    private const double ZoomInFactor = 0.25;
    private const double ZoomOutFactor = 4.0;

    private readonly Stack<MandelbrotViewport> history = [];
    private readonly DispatcherTimer resizeTimer;

    private MandelbrotViewport view = MandelbrotViewport.FullSet(InitialImageWidth, InitialImageHeight);
    private WriteableBitmap? bitmap;
    private int[]? histogramPalette;
    private int imageWidth = InitialImageWidth;
    private int imageHeight = InitialImageHeight;
    private bool isRendering;
    private bool gpuRenderingSuspended;
    private bool isClosed;

    // A resize or click can arrive while the GPU is still busy. Coalesce those
    // changes into one follow-up render instead of starting overlapping jobs.
    private bool renderQueued;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureUiValidation();

        // Resize can fire dozens of times during a corner drag. Debouncing
        // keeps the app responsive and renders only the final settled size.
        resizeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        resizeTimer.Tick += ResizeTimer_Tick;
        Closed += (_, _) =>
        {
            isClosed = true;
            renderQueued = false;
            resizeTimer.Stop();
            ClearHistory();
            view.Dispose();
        };

        Loaded += async (_, _) =>
        {
            EnsureRenderTarget(resetView: true);
            ApplyValidationFixture();
            await RenderAsync();
        };
    }

    private async Task RenderAsync()
    {
        // A fresh application session is required after a GPU safety failure.
        // Do not let a queued resize/click silently recreate and stress the device.
        if (gpuRenderingSuspended || isClosed) return;
        UpdateViewText();
        BackButton.IsEnabled = history.Count > 0;
        LogUiValidation("request", view, imageWidth, imageHeight);
        if (isRendering)
        {
            // Do not overlap render jobs. ComputeSharp work plus readback can
            // hold GPU resources; coalescing produces one newest-frame render.
            renderQueued = true;
            return;
        }

        if (bitmap is null)
        {
            return;
        }

        isRendering = true;
        try
        {
            // One frame task at a time; completed frames do not remain on a
            // recursive async chain while newer requests are rendered.
            do
            {
                renderQueued = false;
                await RenderFrameAsync();
            }
            while (renderQueued && !gpuRenderingSuspended && !isClosed);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            isRendering = false;
        }
    }

    private async Task RenderFrameAsync()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Rendering...";
        UpdateViewText();

        int renderWidth = imageWidth;
        int renderHeight = imageHeight;
        MandelbrotViewport sourceView = view;
        // UI navigation owns the live/history viewports. The worker owns a
        // separate snapshot until all GPU work, repairs and logging finish.
        using MandelbrotViewport renderView = view.WithAspect(view.Aspect);
        WriteableBitmap renderBitmap = bitmap!;
        LogUiValidation("render-start", renderView, renderWidth, renderHeight);

        int maxIterations = IterationBudget.ForScale(renderView.Scale);
        ProgressiveFrame progress = new(renderWidth * renderHeight, maxIterations, histogramPalette);
        DispatcherTimer presentationTimer = new(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        presentationTimer.Tick += (_, _) =>
        {
            if (!isClosed && ReferenceEquals(sourceView, view) && ReferenceEquals(renderBitmap, bitmap))
                progress.Apply(renderBitmap);
        };
        presentationTimer.Start();
        try
        {
            // Capture all mutable UI state before the background task starts.
            // Later resize/click events update fields and queue another render.
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Keep the UI thread responsive while ComputeSharp dispatches and
            // reads back the GPU work.
            RenderResult result = await Task.Run(() =>
            {
                MandelbrotRenderer renderer = new(renderWidth, renderHeight) { PublishPixels = progress.Publish };
                return renderer.Render(renderView, maxIterations);
            });
            stopwatch.Stop();
            if (isClosed) return;

            // A resize may replace the bitmap while this render is running. If
            // that happened, discard the stale pixels and immediately queue a
            // render for the newest dimensions.
            if (ReferenceEquals(sourceView, view) && ReferenceEquals(renderBitmap, bitmap) && renderWidth == imageWidth && renderHeight == imageHeight)
            {
                // WPF expects BGRA32 packed into Int32 pixels. The renderer has
                // already converted escape counts through the histogram palette.
                renderBitmap.WritePixels(
                    new Int32Rect(0, 0, renderWidth, renderHeight),
                    result.Pixels,
                    renderWidth * sizeof(int),
                    0);
                histogramPalette = result.HistogramPalette;
            }
            else
            {
                renderQueued = true;
            }

            string repairCapText = result.UnresolvedGlitchCount > 0
                ? $"  repair cap={result.FinalRepairLimit:n0}"
                : string.Empty;
            StatusText.Text = $"{renderWidth}x{renderHeight}  {result.Mode}  refs={result.ReferencePasses:n0}  {maxIterations:n0} iter  repaired={result.RepairedCount:n0}  unresolved={result.UnresolvedGlitchCount:n0}/{result.InitialGlitchCount:n0}{repairCapText}  {stopwatch.ElapsedMilliseconds:n0} ms";
            // Keep detailed measurements accessible without crowding the bar.
            StatusText.ToolTip = RendererDiagnostics.Enabled ? result.Timings.ToString() : null;
            if (result.Validation is { } validation)
            {
                StatusText.Text += $"  check={validation.Mismatches}/{validation.Samples - validation.Unresolved} mismatches";
                StatusText.ToolTip += $"; validation unresolved: {validation.Unresolved}";
            }
            BackButton.IsEnabled = history.Count > 0;
            LogUiValidation("render-complete", renderView, renderWidth, renderHeight,
                new { applied = ReferenceEquals(sourceView, view) && ReferenceEquals(renderBitmap, bitmap), latestView = ReferenceEquals(sourceView, view),
                    queued = renderQueued, elapsedMs = stopwatch.Elapsed.TotalMilliseconds, status = StatusText.Text });
        }
        catch (GpuRenderSuspendedException ex)
        {
            LogUiValidation("suspended", renderView, renderWidth, renderHeight, ex.Message);
            gpuRenderingSuspended = true;
            renderQueued = false;
            resizeTimer.Stop();
            ResetButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            StatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            LogUiValidation("error", renderView, renderWidth, renderHeight, ex.ToString());
            StatusText.Text = ex.Message;
        }
        finally
        {
            presentationTimer.Stop();
        }
    }

    private async void FractalImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (gpuRenderingSuspended) return;
        if (!TryGetComplexPoint(e.GetPosition(FractalImage), out MpfrComplex? center) || center is null)
        {
            return;
        }

        // Save the exact MPFR viewport so Back restores deep coordinates, not
        // just their rounded display representation.
        try
        {
            ScaleZoomPreview(e.GetPosition(FractalImage), ZoomInFactor);
            MandelbrotViewport next = view.Zoom(center, ZoomInFactor);
            history.Push(view);
            view = next;
        }
        finally
        {
            center.Dispose();
        }

        await RenderAsync();
    }

    private async void FractalImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (gpuRenderingSuspended) return;
        if (!TryGetComplexPoint(e.GetPosition(FractalImage), out MpfrComplex? center) || center is null)
        {
            return;
        }

        try
        {
            ScaleZoomPreview(e.GetPosition(FractalImage), ZoomOutFactor);
            MandelbrotViewport next = view.Zoom(center, ZoomOutFactor);
            history.Push(view);
            view = next;
        }
        finally
        {
            center.Dispose();
        }

        await RenderAsync();
    }

    private void ScaleZoomPreview(Point click, double factor)
    {
        if (bitmap is null) return;
        // Snapshot the currently visible composite, including partial updates.
        // Drawing in physical pixel coordinates also handles high-DPI displays.
        double x = click.X / FractalImage.ActualWidth * imageWidth;
        double y = click.Y / FractalImage.ActualHeight * imageHeight;
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(2, 3, 8)), null,
                new Rect(0, 0, imageWidth, imageHeight));
            drawing.DrawImage(bitmap, new Rect(imageWidth * 0.5 - x / factor,
                imageHeight * 0.5 - y / factor, imageWidth / factor, imageHeight / factor));
        }
        RenderTargetBitmap preview = new(imageWidth, imageHeight, 96, 96, PixelFormats.Pbgra32);
        preview.Render(visual);
        bitmap.Lock();
        try
        {
            preview.CopyPixels(Int32Rect.Empty, bitmap.BackBuffer,
                checked(bitmap.BackBufferStride * imageHeight), bitmap.BackBufferStride);
            bitmap.AddDirtyRect(new Int32Rect(0, 0, imageWidth, imageHeight));
        }
        finally { bitmap.Unlock(); }
    }

    private void ClearHistory()
    {
        while (history.TryPop(out MandelbrotViewport? previous)) previous.Dispose();
    }

    private void ReplaceView(MandelbrotViewport next)
    {
        MandelbrotViewport previous = view;
        view = next;
        previous.Dispose();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        if (gpuRenderingSuspended) return;
        ClearHistory();
        EnsureRenderTarget(resetView: true);
        await RenderAsync();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (gpuRenderingSuspended) return;
        if (history.TryPop(out MandelbrotViewport? previous))
        {
            // The saved view may have been captured at a different window
            // aspect. Restore center/zoom, then adapt to the current shape.
            using (previous)
                ReplaceView(previous.WithAspect((double)imageWidth / imageHeight));
            await RenderAsync();
        }
    }

    private void RenderHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || gpuRenderingSuspended)
        {
            return;
        }

        // Corner dragging emits many SizeChanged events; waiting briefly avoids
        // flooding the GPU with intermediate frame sizes.
        resizeTimer.Stop();
        resizeTimer.Start();
    }

    private async void ResizeTimer_Tick(object? sender, EventArgs e)
    {
        resizeTimer.Stop();

        if (gpuRenderingSuspended) return;

        if (EnsureRenderTarget(resetView: false))
        {
            await RenderAsync();
        }
    }

    private bool EnsureRenderTarget(bool resetView)
    {
        (int width, int height) = GetRenderSize();

        if (!resetView && bitmap is not null && width == imageWidth && height == imageHeight)
        {
            return false;
        }

        imageWidth = width;
        imageHeight = height;

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        bitmap = new WriteableBitmap(
            imageWidth,
            imageHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Bgra32,
            null);

        FractalImage.Source = bitmap;

        // Reset returns to the full set. Resize preserves the current vertical
        // complex-plane span and only changes the horizontal span for aspect.
        ReplaceView(resetView
            ? MandelbrotViewport.FullSet(imageWidth, imageHeight)
            : view.WithAspect((double)imageWidth / imageHeight));

        UpdateViewText();
        return true;
    }

    private (int Width, int Height) GetRenderSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        // WPF reports device-independent units; the bitmap uses physical
        // pixels so high-DPI displays still get a crisp computed image.
        int width = global::System.Math.Max(64, (int)global::System.Math.Round(RenderHost.ActualWidth * dpi.DpiScaleX));
        int height = global::System.Math.Max(64, (int)global::System.Math.Round(RenderHost.ActualHeight * dpi.DpiScaleY));

        return (width, height);
    }

    private bool TryGetComplexPoint(Point position, out MpfrComplex? point)
    {
        point = null;

        if (FractalImage.ActualWidth <= 0 || FractalImage.ActualHeight <= 0)
        {
            return false;
        }

        double normalizedX = position.X / FractalImage.ActualWidth;
        double normalizedY = position.Y / FractalImage.ActualHeight;

        // Ignore clicks in letterboxed or otherwise invalid image space. The
        // current layout should normally keep the image aligned with the host,
        // but this protects against transient WPF sizing states.
        if (normalizedX is < 0 or > 1 || normalizedY is < 0 or > 1)
        {
            return false;
        }

        point = view.PointAt(normalizedX, normalizedY);
        return true;
    }

    private void UpdateViewText()
    {
        ViewText.Text = view.Describe();
    }
}
