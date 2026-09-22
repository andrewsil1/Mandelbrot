using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace MandelbrotGpu;

// Submission happens on the render thread, not Task.Run. Each Task owns the
// library's asynchronous fence/allocator lifecycle; consume it exactly once.
// The queue must die before the resources referenced by its command lists.
internal sealed class BoundedGpuQueue(int capacity, Action? checkDevice = null) : IDisposable
{
    private sealed class Pending(long submitted, Func<long> timestamp,
        Action<double, double> complete, Action<Exception> fail)
    {
        public Task Completion = null!;
        public readonly Action<double, double> Complete = complete;
        public readonly Action<Exception> Fail = fail;
        public double CompletionMilliseconds;

        public void Observe(Task task)
        {
            // Capture completion on its continuation, not when the render
            // thread eventually consumes the FIFO after recording more work.
            CompletionMilliseconds = Stopwatch.GetElapsedTime(submitted, timestamp()).TotalMilliseconds;
            task.GetAwaiter().GetResult();
        }
    }
    private readonly Queue<Pending> pending = new();
    private bool stopped;
    public int Count => pending.Count;
    public int Peak { get; private set; }
    public bool MeasureWait { get; init; } = true;
    // The production clock is monotonic; injection makes delay tests deterministic.
    public Func<long> Timestamp { get; init; } = Stopwatch.GetTimestamp;

    public void MakeRoom()
    {
        if (stopped) throw new InvalidOperationException("GPU queue is stopped.");
        if (pending.Count >= capacity) CompleteOldest();
    }

    public void Add(Task completion, Action<double> complete, Action<Exception> fail)
        => AddTimed(completion, Timestamp(), (wait, _) => complete(wait), fail);

    public void AddTimed(Task completion, long submitted, Action<double, double> complete, Action<Exception> fail)
    {
        if (stopped || pending.Count >= capacity || capacity is < 1 or > 2)
            throw new InvalidOperationException("GPU submission window exceeded or stopped.");
        Pending item = new(submitted, Timestamp, complete, fail);
        // No Task.Run or additional submissions. The observer task preserves
        // native failures and ensures the captured duration is ready before use.
        item.Completion = completion.ContinueWith(static (task, state) => ((Pending)state!).Observe(task),
            item, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        pending.Enqueue(item);
        Peak = Math.Max(Peak, pending.Count);
    }

    private void CompleteOldest()
    {
        Pending item = pending.Dequeue();
        long start = MeasureWait ? Stopwatch.GetTimestamp() : 0;
        try
        {
            item.Completion.GetAwaiter().GetResult();
            checkDevice?.Invoke();
            item.Complete(MeasureWait ? Stopwatch.GetElapsedTime(start).TotalMilliseconds : 0, item.CompletionMilliseconds);
        }
        catch (Exception ex)
        {
            stopped = true;
            try { item.Fail(ex); } catch { }
            throw;
        }
    }

    public void Drain()
    {
        try { while (pending.Count > 0) CompleteOldest(); }
        catch (Exception ex)
        {
            // No new work or retries after failure. Still consume already-issued
            // completions before callers release their buffers; preserve first error.
            Dispose();
            ExceptionDispatchInfo.Capture(ex).Throw();
            throw;
        }
    }

    public void Dispose()
    {
        stopped = true;
        // Explicit Drain is mandatory on successful paths. Here we are unwinding
        // a submission/logging/readback failure; cleanup must not replace it.
        while (pending.Count > 0)
        {
            try { pending.Dequeue().Completion.GetAwaiter().GetResult(); }
            catch { }
        }
    }
}
