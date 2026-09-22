using System.Reflection;
using System.Text.Json;
using MandelbrotGpu;

internal static class JournalChecks
{
    private static readonly Assembly Assembly = typeof(MandelbrotRenderer).Assembly;
    private static readonly Type Group = Assembly.GetType("MandelbrotGpu.DispatchJournalGroup")!;

    public static void Run()
    {
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES");
        try
        {
            MethodInfo limit = Group.GetMethod("GroupSlices")!;
            Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", null);
            if ((int)limit.Invoke(null, null)! != 8) throw new Exception("Default journal grouping failed.");
            foreach (string valid in new[] { "1", "8" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", valid);
                if ((int)limit.Invoke(null, null)! != int.Parse(valid)) throw new Exception("Journal group limit failed.");
            }
            foreach (string invalid in new[] { "0", "9", "-1", "invalid" })
            {
                Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", invalid);
                try { limit.Invoke(null, null); throw new Exception("Invalid journal group limit accepted."); }
                catch (TargetInvocationException ex) when (ex.InnerException is ArgumentOutOfRangeException) { }
            }
            MethodInfo due = Group.GetMethod("CheckpointDue")!;
            if ((bool)due.Invoke(null, [99.9])! || !(bool)due.Invoke(null, [100.0])!)
                throw new Exception("Checkpoint target boundary failed.");
            Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", "8");
            CheckLifecycle();
            CheckPendingWindow();
            CheckWriteFailure();
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", previous); }
        Console.WriteLine("GPU-free journal group checks passed: durable intent, bounded checkpoints, early exit, failure, abort, write failure.");
    }

    private static string NewPath() => Path.Combine(AppContext.BaseDirectory, "journal-checks", $"{Guid.NewGuid():N}.jsonl");
    private static IDisposable NewGroup(string path) => (IDisposable)Activator.CreateInstance(Group, [1024, 128, null, path])!;
    private static void Call(IDisposable group, string method, params object[] args) => Group.GetMethod(method)!.Invoke(group, args);
    private static object BeginEntry(int start) => new { phase = "probe-begin", sliceStart = start };
    private static List<JsonElement> Read(string path) => File.ReadLines(path).Select(line =>
    {
        using JsonDocument document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }).ToList();

    private static void CheckLifecycle()
    {
        string path = NewPath();
        using (IDisposable group = NewGroup(path))
        {
            for (int i = 0; i < 8; i++)
            {
                Call(group, "Begin", BeginEntry(i * 128), i * 128);
                // Before GPU authorization, the intent is already readable;
                // buffered slice records do not become durable individually.
                if (Read(path).Count != 1 || Read(path)[0].GetProperty("phase").GetString() != "group-begin")
                    throw new Exception("Slice submitted without a durable intent or checkpointed prematurely.");
                Call(group, "Complete", new { phase = "probe-end" });
                // Count bound is deterministic even on a slow test machine;
                // exercise Checkpoint on the eighth completion only.
            }
            try { Call(group, "Begin", BeginEntry(1024), 1024); throw new Exception("Group exceeded eight slices."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            Call(group, "Checkpoint", false);
            List<JsonElement> entries = Read(path);
            if (entries.Count != 18 || entries[^1].GetProperty("completedSubmissions").GetInt32() != 8)
                throw new Exception("Bounded checkpoint lost records.");
            group.Dispose();
            group.Dispose();
            if (Read(path).Count != 18) throw new Exception("Dispose repeated a completed checkpoint.");
        }
        foreach (string exit in new[] { "Checkpoint", "Fail", "Stop", "Dispose" })
        {
            path = NewPath();
            using IDisposable group = NewGroup(path);
            Call(group, "Begin", BeginEntry(0), 0);
            if (exit != "Fail") Call(group, "Complete", new { phase = "probe-end" });
            if (exit == "Checkpoint") Call(group, exit, true);
            else if (exit == "Fail") Call(group, exit, new { phase = "probe-failed", error = "injected" });
            else Call(group, exit);
            string expected = exit switch
            {
                "Checkpoint" => "group-end", "Fail" => "group-failed",
                "Stop" => "group-stopped", _ => "group-aborted"
            };
            if (Read(path)[^1].GetProperty("phase").GetString() != expected)
                throw new Exception("Partial group exit did not record its status.");
        }
    }

    private static void CheckWriteFailure()
    {
        string path = NewPath();
        using IDisposable group = NewGroup(path);
        Call(group, "Begin", BeginEntry(0), 0);
        Call(group, "Complete", new { phase = "probe-end" });
        using (FileStream locked = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            try { Call(group, "Checkpoint", true); throw new Exception("Journal write failure was ignored."); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
            group.Dispose(); // Must not retry a failed write during cleanup.
        }
        if (Read(path).Count != 1) throw new Exception("Failed journal checkpoint was retried.");

        string directory = Path.Combine(AppContext.BaseDirectory, "journal-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using IDisposable failedBegin = NewGroup(directory);
        bool authorized = false;
        try { Call(failedBegin, "Begin", BeginEntry(0), 0); authorized = true; }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException or IOException) { }
        if (authorized) throw new Exception("Failed durable intent authorized a submission.");
    }

    private static void CheckPendingWindow()
    {
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_INFLIGHT");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", "2");
            string path = NewPath();
            using IDisposable group = NewGroup(path);
            Call(group, "Begin", BeginEntry(0), 0);
            Call(group, "Begin", BeginEntry(128), 128);
            try { Call(group, "Begin", BeginEntry(256), 256); throw new Exception("Journal exceeded its pending window."); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
            for (int completed = 0; completed < 2; completed++)
            {
                try { Call(group, "Checkpoint", true); throw new Exception("Executing work was checkpointed as complete."); }
                catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
                if (Read(path).Count != 1) throw new Exception("Pending journal records flushed before drain.");
                Call(group, "Complete", new { phase = "probe-end" });
            }
            Call(group, "Checkpoint", true);
            if (Read(path).Count != 6 || Read(path)[^1].GetProperty("completedSubmissions").GetInt32() != 2)
                throw new Exception("Drained submission window did not checkpoint both completions.");
        }
        finally { Environment.SetEnvironmentVariable("MANDELBROT_INFLIGHT", previous); }
    }

    public static void CheckLog()
    {
        string path = (string)Assembly.GetType("MandelbrotGpu.DispatchJournal")!.GetProperty("LogPath")!.GetValue(null)!;
        Dictionary<Guid, JsonElement> groups = [];
        Dictionary<Guid, int> begins = [], ends = [], nextSlices = [];
        Dictionary<Guid, Queue<Guid>> completionOrder = [];
        HashSet<Guid> closed = [], pending = [];
        int submissions = 0;
        foreach (JsonElement entry in Read(path))
        {
            string? phase = entry.GetProperty("phase").GetString();
            if (phase == "group-begin")
            {
                Guid id = entry.GetProperty("group").GetGuid();
                if (!groups.TryAdd(id, entry) || entry.GetProperty("schemaVersion").GetInt32() != 2
                    || entry.GetProperty("maxSubmissions").GetInt32() is < 1 or > 8)
                    throw new Exception("Invalid or duplicate durable journal intent.");
                begins[id] = ends[id] = 0;
                completionOrder[id] = new();
                nextSlices[id] = entry.GetProperty("plannedSliceStart").GetInt32();
            }
            else if (phase is "begin" or "end")
            {
                Guid submission = entry.GetProperty("submission").GetGuid();
                if (phase == "begin")
                {
                    if (!pending.Add(submission)) throw new Exception("Duplicate submission begin.");
                    submissions++;
                }
                else if (!pending.Remove(submission)) throw new Exception("Completion lacks its begin record.");
                if (!entry.TryGetProperty("journalGroup", out JsonElement group) || group.ValueKind == JsonValueKind.Null) continue;
                Guid id = group.GetGuid();
                if (!groups.TryGetValue(id, out JsonElement intent) || closed.Contains(id))
                    throw new Exception("Slice lacks its preceding durable group intent.");
                if (phase == "begin")
                {
                    JsonElement context = intent.GetProperty("context");
                    int limit = context.TryGetProperty("inFlightLimit", out JsonElement capacity) ? capacity.GetInt32() : 1;
                    if (++begins[id] > intent.GetProperty("maxSubmissions").GetInt32()
                        || limit is < 1 or > 2 || begins[id] - ends[id] > limit
                        || entry.GetProperty("sliceStart").GetInt32() != nextSlices[id]
                        || entry.GetProperty("sliceEnd").GetInt32() > intent.GetProperty("plannedSliceEnd").GetInt32())
                        throw new Exception("Group exceeded its authorized slice range/count or in-flight bound.");
                    foreach (string field in new[] { "renderId", "referencePass", "mode", "width", "height", "offset", "count", "iterationBudget" })
                        if (entry.GetProperty(field).ToString() != context.GetProperty(field).ToString())
                            throw new Exception("Group crossed a batch/reference/mode boundary.");
                    nextSlices[id] = entry.GetProperty("sliceEnd").GetInt32();
                    completionOrder[id].Enqueue(submission);
                }
                else
                {
                    if (completionOrder[id].Count == 0 || completionOrder[id].Dequeue() != submission)
                        throw new Exception("Group completion crossed an intent or violated FIFO order.");
                    ends[id]++;
                }
            }
            else if (phase == "group-end")
            {
                Guid id = entry.GetProperty("group").GetGuid();
                if (!groups.ContainsKey(id) || !closed.Add(id) || begins[id] != ends[id]
                    || ends[id] != entry.GetProperty("completedSubmissions").GetInt32())
                    throw new Exception("Group completion does not match its actual slices.");
            }
            else if (phase is "failed" or "group-failed" or "group-stopped" or "group-aborted")
                throw new Exception("Journal contains an unsuccessful submission/group.");
        }
        if (submissions == 0 || pending.Count != 0 || groups.Count != closed.Count)
            throw new Exception("Journal contains incomplete submissions or groups.");
        Console.WriteLine($"Journal validation passed: {submissions:n0} submissions, {groups.Count:n0} bounded groups, durable intents and matching completions.");
    }

    public static void CheckEquivalence()
    {
        string? previous = Environment.GetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES");
        string? previousSlices = Environment.GetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS");
        try
        {
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", "128");
            foreach (double scale in new[] { Math.ScaleB(1, -40), 1E-27 })
            {
                using MpfrComplex center = new(MpfrFloat.FromDouble(-0.34562335012588691, 384), MpfrFloat.FromDouble(0.625450590999726, 384));
                MandelbrotViewport view = MandelbrotViewport.FullSet(13, 9).Zoom(center, scale);
                Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", "1");
                RenderResult baseline = PrecisionProfilingChecks.Render(new(13, 9), view, 6912, scale < 1E-26);
                Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", "8");
                RenderResult grouped = PrecisionProfilingChecks.Render(new(13, 9), view, 6912, scale < 1E-26);
                TimingChecks.CheckResult(baseline);
                TimingChecks.CheckResult(grouped);
                if (!baseline.Pixels.SequenceEqual(grouped.Pixels)
                    || baseline.Mode != grouped.Mode || baseline.ReferencePasses != grouped.ReferencePasses
                    || baseline.RepairedCount != grouped.RepairedCount
                    || baseline.UnresolvedGlitchCount != grouped.UnresolvedGlitchCount
                    || baseline.Timings.DispatchCount != grouped.Timings.DispatchCount
                    || grouped.Validation is not { Mismatches: 0, Unresolved: 0 }
                    || grouped.Timings.JournalGroupCount >= baseline.Timings.JournalGroupCount)
                    throw new Exception("Grouped journaling changed numerical results, dispatches, or failed to reduce checkpoints.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("MANDELBROT_JOURNAL_GROUP_SLICES", previous);
            Environment.SetEnvironmentVariable("MANDELBROT_SLICE_ITERATIONS", previousSlices);
        }
        Console.WriteLine("FP64/DD journal group equivalence passed: identical images/dispatches, fewer checkpoints, MPFR agreement.");
    }
}
