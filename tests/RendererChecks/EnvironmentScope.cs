// Process-wide settings must be restored even when an assertion throws.
internal sealed class EnvironmentScope : IDisposable
{
    private readonly (string Name, string? Value)[] previous;

    public EnvironmentScope(params string[] names)
        => previous = names.Distinct().Select(name => (name, Environment.GetEnvironmentVariable(name))).ToArray();

    public void Dispose()
    {
        foreach (var (name, value) in previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}
