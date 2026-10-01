namespace PixelModifier.Core;

public sealed record PixelQuality(float Score, float FragmentRatio, float ExtraComponents,
    float HoleRatio, float AreaLoss, float AbruptSilhouette, float ClippingRatio);

/// <summary>Conservative silhouette diagnostics on the working grid; these are not aesthetic judgments.</summary>
public static class PixelQualityEvaluator
{
    private readonly record struct Shape(int Area, int Fragments, int Components, int Holes, int Edge);

    public static PixelQuality Evaluate(IReadOnlyList<PixelImage> frames, IReadOnlyList<RenderSource> sources,
        MotionCycle cycle, CancellationToken cancellation = default)
    {
        if (frames.Count != cycle.Frames.Count || frames.Count == 0)
            throw new ArgumentException("평가할 프레임과 동작 계획이 일치하지 않습니다.");
        var references = sources.Select(s => Measure(s.Image, Mask(s.Image), cancellation)).ToArray();
        float fragments = 0, components = 0, holes = 0, areaLoss = 0, clipping = 0;
        var steps = new float[frames.Count];
        bool[]? first = null, previous = null;
        int previousArea = 0, firstArea = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = frames[i];
            if (image.Width != cycle.WorkingSize || image.Height != cycle.WorkingSize)
                throw new ArgumentException("도트 평가는 작업 해상도에서 수행해야 합니다.");
            var mask = Mask(image);
            var shape = Measure(image, mask, cancellation);
            var reference = references[cycle.Frames[i].SourceIndex];
            float area = Math.Max(1, reference.Area);
            fragments += Math.Max(0, shape.Fragments - reference.Fragments) / area;
            components += Math.Max(0, shape.Components - reference.Components);
            holes += Math.Max(0, shape.Holes - reference.Holes) / area;
            areaLoss += Math.Max(0, Math.Abs(shape.Area / area - 1) - .20f);
            clipping += Math.Max(0, shape.Edge - reference.Edge) / area;
            if (previous != null) steps[i - 1] = Difference(previous, mask) / (float)Math.Max(1, previousArea + shape.Area);
            else { first = mask; firstArea = shape.Area; }
            previous = mask; previousArea = shape.Area;
        }
        steps[^1] = Difference(previous!, first!) / (float)Math.Max(1, previousArea + firstArea);
        var ordered = steps.Order().ToArray();
        float typicalStep = ordered[ordered.Length / 2];
        float abrupt = steps.Average(s => Math.Max(0, s - typicalStep * 1.8f - .04f));
        float count = frames.Count;
        fragments /= count; components /= count; holes /= count; areaLoss /= count; clipping /= count;
        float penalty = 24 * fragments + .8f * components + 12 * holes + 3 * areaLoss + 8 * abrupt + 20 * clipping;
        return new(100 / (1 + penalty), fragments, components, holes, areaLoss, abrupt, clipping);
    }

    private static bool[] Mask(PixelImage image)
    {
        var mask = new bool[image.Width * image.Height];
        for (int i = 0; i < mask.Length; i++) mask[i] = image.Pixels[i * 4 + 3] >= 128;
        return mask;
    }

    private static int Difference(bool[] a, bool[] b)
    {
        int difference = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) difference++;
        return difference;
    }

    private static Shape Measure(PixelImage image, bool[] mask, CancellationToken cancellation)
    {
        int width = image.Width, height = image.Height, area = mask.Count(p => p);
        int small = Math.Max(2, (int)(area * .002f));
        int fragments = 0, components = 0, holes = 0, edge = 0;
        var visited = new bool[mask.Length];
        var queue = new int[mask.Length];
        for (int i = 0; i < mask.Length; i++)
        {
            if (!mask[i]) continue;
            if (i % width == 0 || i % width == width - 1 || i < width || i >= (height - 1) * width) edge++;
            if (visited[i]) continue;
            var region = Flood(i, true, true);
            if (region.Count <= small) fragments += region.Count; else components++;
        }
        Array.Clear(visited);
        // Transparent regions touching the canvas border are not holes. Large pose openings are ignored.
        for (int i = 0; i < mask.Length; i++)
        {
            if (mask[i] || visited[i]) continue;
            var region = Flood(i, false, false);
            if (!region.Border && region.Count <= Math.Max(4, area / 100)) holes += region.Count;
        }
        return new(area, fragments, components, holes, edge);

        (int Count, bool Border) Flood(int start, bool foreground, bool diagonals)
        {
            int read = 0, write = 1;
            bool border = false;
            queue[0] = start; visited[start] = true;
            while (read < write)
            {
                if ((read & 255) == 0) cancellation.ThrowIfCancellationRequested();
                int index = queue[read++], x = index % width, y = index / width;
                border |= x == 0 || y == 0 || x == width - 1 || y == height - 1;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0 || !diagonals && Math.Abs(dx) + Math.Abs(dy) != 1) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int next = ny * width + nx;
                        if (visited[next] || mask[next] != foreground) continue;
                        visited[next] = true; queue[write++] = next;
                    }
            }
            return (write, border);
        }
    }
}
