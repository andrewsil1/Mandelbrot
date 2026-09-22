namespace MandelbrotGpu;

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
