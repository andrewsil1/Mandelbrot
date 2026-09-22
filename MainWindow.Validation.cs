using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace MandelbrotGpu;

public partial class MainWindow
{
    // Validation-only startup fixture and shortcuts; absent in ordinary sessions.
    private string? validationFixture;
    private string? validationUiLog;

    private void ConfigureUiValidation()
    {
        validationFixture = Environment.GetEnvironmentVariable("MANDELBROT_UI_FIXTURE");
        if (validationFixture is null) return;
        if (validationFixture is not ("transition" or "tip"))
            throw new ArgumentException("MANDELBROT_UI_FIXTURE must be transition or tip.");
        string path = Environment.GetEnvironmentVariable("MANDELBROT_VIEWPORT_LOG")
            ?? throw new ArgumentException("UI validation requires MANDELBROT_VIEWPORT_LOG.");
        validationUiLog = path + ".ui.jsonl";
        Title = "Mandelbrot GPU Explorer - UI validation";
        MinWidth = 400; MinHeight = 300;
        Width = 700; Height = 450;
        KeyDown += async (_, e) =>
        {
            if (gpuRenderingSuspended || e.Key is not (Key.F6 or Key.F7)) return;
            validationFixture = e.Key == Key.F6 ? "transition" : "tip";
            history.Clear();
            ApplyValidationFixture();
            e.Handled = true;
            await RenderAsync();
        };
    }

    private void ApplyValidationFixture()
    {
        if (validationFixture is null) return;
        bool tip = validationFixture == "tip";
        using MpfrComplex center = new(MpfrFloat.FromDouble(tip ? -2 : -0.67323438570448868, 384),
            MpfrFloat.FromDouble(tip ? 0 : 0.35743485497289235, 384));
        view = MandelbrotViewport.FullSet(imageWidth, imageHeight).Zoom(center, tip ? 1E-28 : Math.ScaleB(1, -40));
    }

    private void LogUiValidation(string phase, MandelbrotViewport snapshot, int width, int height, object? detail = null)
    {
        if (validationUiLog is null) return;
        File.AppendAllText(validationUiLog, JsonSerializer.Serialize(new
        {
            phase, utc = DateTime.UtcNow, pid = Environment.ProcessId, width, height,
            fixture = validationFixture, scale = snapshot.Scale,
            x = snapshot.CenterX.ToExactBinary64Terms(), y = snapshot.CenterY.ToExactBinary64Terms(),
            span = snapshot.Height.ToExactBinary64Terms(),
            aspectBits = BitConverter.DoubleToUInt64Bits(snapshot.Aspect).ToString("X16"), detail
        }) + Environment.NewLine);
    }
}
