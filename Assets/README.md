# Application icon

`Mandelbrot.png` is a square adaptation of the user-supplied Mandelbrot image,
created with the built-in imagegen tool. It is an AI adaptation, not a pixel-exact
crop of the original.

Generation prompt:

> Create a square Windows application icon directly from the attached Mandelbrot image. Preserve the exact orientation, recognizable black Mandelbrot silhouette, and orange, purple, lavender and white contour palette. Closely preserve the original artwork; only adjust the framing to a square so the entire central black shape and its satellite circles are visible with balanced margins. Flat edge-to-edge artwork, no text, no border, no shadow, no mockup, no added objects. Output a 1024x1024 PNG suitable for downsampling to a Windows ICO.

Regenerate `Mandelbrot.ico` from the saved PNG with:

```powershell
./tools/New-ApplicationIcon.ps1
```

The ICO contains 32-bit PNG frames at 16, 24, 32, 48, 64, 128, and 256 pixels.
Both executable projects embed it through `ApplicationIcon`; the WPF main
window also uses the icon as an embedded resource.
