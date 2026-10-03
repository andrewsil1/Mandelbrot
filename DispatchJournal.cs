using System.Text;
using System.IO;
using System.Text.Json;
using System.Diagnostics;

namespace MandelbrotGpu;

internal static class DispatchJournal
{
    private static readonly object Gate = new();
    public static string LogPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("MANDELBROT_LOG_DIRECTORY") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MandelbrotGpu"),
        $"dispatch-{Environment.ProcessId}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

    // Flush intent before submitting work. Grouped slice records are checkpointed
    // together; a crash can leave an unmatched durable group intent.
    // File I/O failure aborts before dispatch rather than silently losing evidence.
    public static JournalWriteTiming Write(object entry) => WriteMany([entry]);

    public static JournalWriteTiming WriteMany(IReadOnlyList<object> entries, string? path = null)
    {
        if (entries.Count == 0) throw new ArgumentException("Journal checkpoint cannot be empty.", nameof(entries));
        path ??= LogPath;
        long start = Stopwatch.GetTimestamp();
        double serializationMilliseconds;
        double fileMilliseconds;
        lock (Gate)
        {
            long serializationStart = Stopwatch.GetTimestamp();
            StringBuilder lines = new();
            foreach (object entry in entries) lines.Append(JsonSerializer.Serialize(entry)).Append('\n');
            byte[] bytes = Encoding.UTF8.GetBytes(lines.ToString());
            serializationMilliseconds = Stopwatch.GetElapsedTime(serializationStart).TotalMilliseconds;
            long fileStart = Stopwatch.GetTimestamp();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (FileStream stream = new(path, FileMode.Append, FileAccess.Write,
                FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            // Include open, durable write/flush, and close, not just Stream.Write.
            fileMilliseconds = Stopwatch.GetElapsedTime(fileStart).TotalMilliseconds;
        }
        return new(Stopwatch.GetElapsedTime(start).TotalMilliseconds,
            serializationMilliseconds, fileMilliseconds);
    }

    public static bool ShouldSuspend(double milliseconds) => milliseconds >= 1000;
}

internal readonly record struct JournalWriteTiming(
    double TotalMilliseconds, double SerializationMilliseconds, double FileMilliseconds);

// Crash-evidence groups are independent of the bounded submission window.
// All issued command lists must complete before checkpointing or closing a group.
internal sealed class DispatchJournalGroup(
    int iterationBudget, int sliceIterations,
    Action<JournalWriteTiming, int, bool>? recordTiming = null, string? logPath = null) : IDisposable
{
    public const int MaxSlices = 8;
    public const double CheckpointTargetMilliseconds = 100;
    private readonly int limit = GroupSlices();
    private readonly List<object> records = [];
    private long started;
    private int completed;
    private int awaitingCompletion;
    private bool disposed;
    public Guid Id { get; private set; } = Guid.NewGuid();
    public bool CanBegin => completed + awaitingCompletion < limit
        && awaitingCompletion < GpuDispatchPolicy.InFlightSubmissions();
    public bool NeedsCheckpoint => records.Count > 0 && (completed + awaitingCompletion >= limit
        || CheckpointDue(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
    public bool HasPending => awaitingCompletion != 0;

    public static int GroupSlices()
    {
        string? configured = Environment.GetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES");
        if (configured is null) return MaxSlices;
        if (!int.TryParse(configured, out int value) || value < 1 || value > MaxSlices)
            throw new ArgumentOutOfRangeException("MANDELBROT_JOURNAL_GROUP_SLICES");
        return value;
    }

    public void Begin(object entry, int sliceStart)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!CanBegin)
            throw new InvalidOperationException("Journal group must be checkpointed before another submission.");
        if (records.Count == 0)
        {
            // Intent must reach disk BEFORE the first GPU call. Later buffered
            // begin/end records are evidence, not authorization to submit.
            object intent = new
            {
                phase = "group-begin", schemaVersion = 2, group = Id, utc = DateTime.UtcNow,
                maxSubmissions = limit, checkpointTargetMilliseconds = CheckpointTargetMilliseconds,
                plannedSliceStart = sliceStart,
                plannedSliceEnd = (int)Math.Min(iterationBudget, (long)sliceStart + (long)limit * sliceIterations),
                context = entry
            };
            Write([intent], true);
            started = Stopwatch.GetTimestamp();
        }
        records.Add(entry);
        awaitingCompletion++;
    }

    public void Complete(object entry)
    {
        if (awaitingCompletion == 0) throw new InvalidOperationException("No journal submission is pending.");
        records.Add(entry);
        awaitingCompletion--;
        completed++;
    }

    public void Checkpoint(bool batchComplete)
    {
        if (HasPending) throw new InvalidOperationException("Cannot checkpoint an executing slice.");
        if (batchComplete || completed >= limit || CheckpointDue(Stopwatch.GetElapsedTime(started).TotalMilliseconds))
            Flush("group-end");
    }

    public static bool CheckpointDue(double elapsedMilliseconds) => elapsedMilliseconds >= CheckpointTargetMilliseconds;

    public void Fail(object entry)
    {
        records.Add(entry);
        awaitingCompletion = 0;
        Flush("group-failed");
    }

    public void Stop() => Flush("group-stopped");

    private void Flush(string phase)
    {
        if (records.Count == 0) return;
        records.Add(new { phase, schemaVersion = 2, group = Id, utc = DateTime.UtcNow, completedSubmissions = completed });
        try { Write(records, false); }
        finally
        {
            // A failed write must abort rendering, not be retried during Dispose
            // and replace the original native/file exception with a second one.
            records.Clear();
            completed = 0;
            awaitingCompletion = 0;
            Id = Guid.NewGuid();
        }
    }

    private void Write(IReadOnlyList<object> entries, bool intent)
    {
        JournalWriteTiming timing = DispatchJournal.WriteMany(entries, logPath);
        recordTiming?.Invoke(timing, entries.Count, intent);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // Normal batch exits explicitly checkpoint. Pending records here mean
        // an exception (for example readback failure). Preserve that exception
        // even if recording its partial-completion evidence also fails.
        try { Flush("group-aborted"); }
        catch { }
    }
}
