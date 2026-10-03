using MandelbrotGpu;

internal static class SparseRetryChecks
{
    public static void Run()
    {
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
                var actual = renderer.FindWorstGlitchTile(pixels, excluded);
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
                    if (actual is null || actual.Value.TileIndex != best.Key || actual.Value.GlitchCount != best.Count()
                        || actual.Value.ReferenceX != expected % width || actual.Value.ReferenceY != expected / width)
                        throw new Exception("Sparse tile selector changed the reference or tie break.");
                }
                if (!pixels.SequenceEqual(original)) throw new Exception("Tile selection changed trusted pixels.");
                checkedMasks++;
            }
        }
        Console.WriteLine($"Sparse retry selector passed: {checkedMasks} masks, centroid/tie/exclusion oracle, trusted preservation.");
    }
}
