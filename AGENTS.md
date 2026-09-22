# Build Regression Requirement

For every future application build, run the renderer regression suite before
reporting the build as successful or ready to use:

```powershell
dotnet run --configuration Debug --project tests/RendererChecks/RendererChecks.csproj --no-restore
```

Use the configuration of the application build (for example, substitute
`Release` for `Debug` for a Release build). The regression command also builds
the application through its project reference. If restore is needed, omit
`--no-restore`.

This suite includes sparse dispatch, buffer reuse, retry merging, dispatch
limits at 4K resolution, raw FP64/double-double rebasing and BLA comparisons
against 768-bit MPFR, and complete rendering/repair pipeline validation.

If tests fail, resolve the failure and rerun them before declaring the build
ready. If GPU or native dependencies prevent running the suite, explicitly
report the build as compiled but regression validation blocked; do not imply
the tests passed. Documentation-only changes do not require a build.
