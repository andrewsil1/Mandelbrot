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
