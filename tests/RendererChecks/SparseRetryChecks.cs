using System.Reflection;
using MandelbrotGpu;

internal static class SparseRetryChecks
{
    public static void Run()
    {
        var select = typeof(MandelbrotRenderer).GetMethod("FindWorstGlitchTile", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Random random = new(17013);
        int checkedMasks = 0;
        foreach (var (width, height) in new[] { (1, 1), (33, 35), (129, 97), (1024, 520) })
        {
            var renderer = new MandelbrotRenderer(width, height);
            int columns = (width + 31) / 32;
            int tileCount = columns * ((height + 31) / 32);
            for (int trial = 0; trial < 12; trial++)
            {
                int[] pixels = Enumerable.Range(0, width * height)
                    .Select(_ => trial == 0 ? 7 : trial == 1 || random.Next(100) < trial * 7 ? -2 : 7).ToArray();
                int[] original = (int[])pixels.Clone();
                HashSet<int> excluded = Enumerable.Range(0, tileCount)
                    .Where(_ => trial == 2 || (trial > 2 && random.Next(3) == 0)).ToHashSet();
                // Independent grouping/sorting oracle preserves the original
                // raster-order tie breaks, tile centroid, and actual glitch pixel.
                var best = Enumerable.Range(0, pixels.Length).Where(i => pixels[i] == -2)
                    .GroupBy(i => ((i / width) / 32) * columns + (i % width) / 32)
                    .Where(g => !excluded.Contains(g.Key))
                    .OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
                object? actual = select.Invoke(renderer, [pixels, excluded]);
                if (best is null)
                {
                    if (actual is not null) throw new Exception("Selected an excluded or empty glitch tile.");
                }
                else
                {
                    long cx = best.Sum(i => (long)(i % width)) / best.Count();
                    long cy = best.Sum(i => (long)(i / width)) / best.Count();
                    int expected = best.OrderBy(i => Math.Pow(i % width - cx, 2) + Math.Pow(i / width - cy, 2))
                        .ThenBy(i => i).First();
                    int Field(string name) => (int)actual!.GetType().GetProperty(name)!.GetValue(actual)!;
                    if (actual is null || Field("TileIndex") != best.Key || Field("GlitchCount") != best.Count()
                        || Field("ReferenceX") != expected % width || Field("ReferenceY") != expected / width)
                        throw new Exception("Sparse tile selector changed the reference or tie break.");
                }
                if (!pixels.SequenceEqual(original)) throw new Exception("Tile selection changed trusted pixels.");
                checkedMasks++;
            }
        }
        Console.WriteLine($"Sparse retry selector passed: {checkedMasks} masks, centroid/tie/exclusion oracle, trusted preservation.");
    }
}
