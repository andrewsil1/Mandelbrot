using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MandelbrotGpu;

public partial class MainWindow : Window
{
    private const int InitialImageWidth = 1600;
    private const int InitialImageHeight = 1050;
    private const double ZoomInFactor = 0.25;
    private const double ZoomOutFactor = 4.0;

    private readonly Stack<MandelbrotView> history = [];
    private readonly DispatcherTimer resizeTimer;

    private MandelbrotView view = MandelbrotView.FullSet(InitialImageWidth, InitialImageHeight);
    private WriteableBitmap? bitmap;
    private int imageWidth = InitialImageWidth;
    private int imageHeight = InitialImageHeight;
    private bool isRendering;

    // A resize or click can arrive while the GPU is still busy. Coalesce those
    // changes into one follow-up render instead of starting overlapping jobs.
    private bool renderQueued;

    public MainWindow()
    {
        InitializeComponent();

        resizeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        resizeTimer.Tick += ResizeTimer_Tick;

        Loaded += async (_, _) =>
        {
            EnsureRenderTarget(resetView: true);
            await RenderAsync();
        };
    }

    private async Task RenderAsync()
    {
        if (isRendering)
        {
            renderQueued = true;
            return;
        }

        if (bitmap is null)
        {
            return;
        }

        isRendering = true;
        Mouse.OverrideCursor = Cursors.Wait;
        StatusText.Text = "Rendering...";
        UpdateViewText();

        int renderWidth = imageWidth;
        int renderHeight = imageHeight;
        MandelbrotView renderView = view;
        WriteableBitmap renderBitmap = bitmap;

        try
        {
            int maxIterations = IterationBudget.ForScale(renderView.Scale);
            Stopwatch stopwatch = Stopwatch.StartNew();

            // Keep the UI thread responsive while ComputeSharp dispatches and
            // reads back the GPU work.
            int[] pixels = await Task.Run(() =>
            {
                MandelbrotRenderer renderer = new(renderWidth, renderHeight);
                return renderer.Render(renderView, maxIterations);
            });
            stopwatch.Stop();

            // A resize may replace the bitmap while this render is running. If
            // that happened, discard the stale pixels and immediately queue a
            // render for the newest dimensions.
            if (ReferenceEquals(renderBitmap, bitmap) && renderWidth == imageWidth && renderHeight == imageHeight)
            {
                renderBitmap.WritePixels(
                    new Int32Rect(0, 0, renderWidth, renderHeight),
                    pixels,
                    renderWidth * sizeof(int),
                    0);
            }
            else
            {
                renderQueued = true;
            }

            StatusText.Text = $"{renderWidth}x{renderHeight}  {maxIterations:n0} iter  {stopwatch.ElapsedMilliseconds:n0} ms";
            BackButton.IsEnabled = history.Count > 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            isRendering = false;

            if (renderQueued)
            {
                renderQueued = false;
                await RenderAsync();
            }
        }
    }

    private async void FractalImage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetComplexPoint(e.GetPosition(FractalImage), out double centerX, out double centerY))
        {
            return;
        }

        history.Push(view);
        view = view.Zoom(centerX, centerY, ZoomInFactor);
        await RenderAsync();
    }

    private async void FractalImage_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!TryGetComplexPoint(e.GetPosition(FractalImage), out double centerX, out double centerY))
        {
            return;
        }

        history.Push(view);
        view = view.Zoom(centerX, centerY, ZoomOutFactor);
        await RenderAsync();
    }

    private async void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        history.Clear();
        EnsureRenderTarget(resetView: true);
        await RenderAsync();
    }

    private async void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (history.TryPop(out MandelbrotView previous))
        {
            view = previous.WithAspect((double)imageWidth / imageHeight);
            await RenderAsync();
        }
    }

    private void RenderHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded)
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
        view = resetView
            ? MandelbrotView.FullSet(imageWidth, imageHeight)
            : view.WithAspect((double)imageWidth / imageHeight);

        UpdateViewText();
        return true;
    }

    private (int Width, int Height) GetRenderSize()
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);

        // WPF reports device-independent units; the bitmap uses physical
        // pixels so high-DPI displays still get a crisp computed image.
        int width = Math.Max(64, (int)Math.Round(RenderHost.ActualWidth * dpi.DpiScaleX));
        int height = Math.Max(64, (int)Math.Round(RenderHost.ActualHeight * dpi.DpiScaleY));

        return (width, height);
    }

    private bool TryGetComplexPoint(Point position, out double x, out double y)
    {
        x = 0;
        y = 0;

        if (FractalImage.ActualWidth <= 0 || FractalImage.ActualHeight <= 0)
        {
            return false;
        }

        double normalizedX = position.X / FractalImage.ActualWidth;
        double normalizedY = position.Y / FractalImage.ActualHeight;

        if (normalizedX is < 0 or > 1 || normalizedY is < 0 or > 1)
        {
            return false;
        }

        x = view.Left + normalizedX * view.Width;
        y = view.Top - normalizedY * view.Height;
        return true;
    }

    private void UpdateViewText()
    {
        ViewText.Text = $"center=({view.CenterX:G17}, {view.CenterY:G17})  scale={view.Scale:E3}";
    }
}
