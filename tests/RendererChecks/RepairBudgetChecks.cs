using System.Reflection;
using MandelbrotGpu;

internal static class RepairBudgetChecks
{
    public static void Run()
    {
        using MpfrFloat a = MpfrFloat.FromDouble(-0.67323438570448868, 384);
        using MpfrFloat b = MpfrFloat.FromDouble(1E-28, 384);
        using MpfrFloat exact = a.Add(b);
        MpfrFloat rebuilt = MpfrFloat.FromDouble(0, 384);
        foreach (string bits in exact.ToExactBinary64Terms())
        {
            var next = rebuilt.Add(BitConverter.UInt64BitsToDouble(Convert.ToUInt64(bits, 16)));
            rebuilt.Dispose(); rebuilt = next;
        }
        using (rebuilt)
        using (MpfrFloat difference = exact.Subtract(rebuilt))
            if (!difference.IsZero) throw new Exception("Exact coordinate export lost MPFR bits.");
        var limit = typeof(MandelbrotRenderer).GetMethod("GetFinalRepairLimit", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var repair = typeof(MandelbrotRenderer).GetMethod("RepairGlitches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MandelbrotRenderer renderer = new(1100, 1);
        // High iteration ceilings must not be overridden by a minimum pixel count.
        if ((int)limit.Invoke(renderer, [120_000_000])! != 1 ||
            (int)limit.Invoke(renderer, [120_000_001])! != 0)
            throw new Exception("Repair iteration ceiling was exceeded.");
        // c > 2 escapes immediately: test bounded selection without doing huge orbits.
        using MpfrComplex center = new(MpfrFloat.FromDouble(4, 384), MpfrFloat.FromDouble(0, 384));
        MandelbrotViewport view = MandelbrotViewport.FullSet(1100, 1).Zoom(center, 1E-8);
        foreach (int tail in new[] { 1023, 1024, 1025, 1040 })
        {
            int[] values = Enumerable.Repeat(-2, tail).Concat(Enumerable.Repeat(7, 1100 - tail)).ToArray();
            int repaired = (int)repair.Invoke(renderer, [view, values, 1024])!;
            if (repaired != tail || values.Take(tail).Any(x => x < 0) || values.Skip(tail).Any(x => x != 7))
                throw new Exception("Repair cliff or trusted-pixel mutation.");
        }
        var boundedRepair = typeof(MandelbrotRenderer).GetMethod("RepairWithinBudget", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (int tail in new[] { 9, 10, 11, 40 })
        {
            int[] indices = Enumerable.Range(0, tail).ToArray();
            Func<int, int> cheap = _ => 1;
            var cheapResult = ((int Count, long Work))boundedRepair.Invoke(null, [indices, 10, 100L, cheap])!;
            if (cheapResult.Count != tail || cheapResult.Work != tail * 2)
                throw new Exception("Early-escape work was not refunded across repair waves.");
            Func<int, int> interior = _ => -1;
            var slowResult = ((int Count, long Work))boundedRepair.Invoke(null, [indices, 10, 100L, interior])!;
            if (slowResult.Count != Math.Min(tail, 10) || slowResult.Work > 100)
                throw new Exception("Repair exceeded its hard work budget.");
        }
        Console.WriteLine("Repair budget passed: 1023/1024/1025/1040 tails, partial repair, hard iteration ceiling, trusted preservation.");
    }
}

