using System.Numerics;

namespace PixelModifier.Core;

public sealed record RenderSource(PixelImage Image, Rig Rig, RegionMap Regions, float Phase);
public sealed record FramePlan(int SourceIndex, Rig Pose, float SecondaryWeight);

public static class Timeline
{
    /// <summary>Interpolates cyclic landmark poses; nearest reference supplies colors without cross-fading.</summary>
    public static FramePlan Plan(IReadOnlyList<RenderSource> sources, float phase,
        MotionVariant variant, GenerationSettings settings)
    {
        if (sources.Count == 0) throw new ArgumentException("기준 이미지가 없습니다.");
        phase -= MathF.Floor(phase);
        if (sources.Count == 1) return new(0, Motion.Pose(sources[0].Rig, phase, variant, settings), 1);
        var ordered = sources.Select((s, index) => (s: s with
        {
            Phase = Math.Clamp((int)MathF.Round(s.Phase * settings.FrameCount, MidpointRounding.AwayFromZero),
                0, settings.FrameCount - 1) / (float)settings.FrameCount
        }, index)).OrderBy(p => p.s.Phase).ToArray();
        for (int i = 1; i < ordered.Length; i++)
            if (ordered[i].s.Phase - ordered[i - 1].s.Phase < .0001f)
                throw new InvalidDataException("기준 이미지 두 장이 같은 출력 프레임에 놓입니다. 주기 위치나 프레임 수를 조정해 주세요.");
        int left = ordered.Length - 1;
        for (int i = 0; i < ordered.Length; i++) if (ordered[i].s.Phase <= phase) left = i;
        int right = (left + 1) % ordered.Length;
        float a = ordered[left].s.Phase, b = ordered[right].s.Phase;
        if (b <= a) b += 1;
        float p = phase < a ? phase + 1 : phase;
        float t = Math.Clamp((p - a) / (b - a), 0, 1);
        float smooth = t * t * (3 - 2 * t);
        var rest = new Rig();
        foreach (Joint joint in Enum.GetValues<Joint>())
            rest[joint] = Point2.Lerp(ordered[left].s.Rig[joint], ordered[right].s.Rig[joint], smooth);
        // Authored poses dominate; procedural motion is secondary and zero at each supplied key pose.
        float weight = .22f * 16 * t * t * (1 - t) * (1 - t);
        int source = t < .5f ? ordered[left].index : ordered[right].index;
        return new(source, Motion.Pose(rest, phase, variant, settings, weight), weight);
    }
}

public static class Renderer
{
    private const int GRID = 40;
    internal const int LayerCount = (int)SpriteLayer.Coat + 1;
    private readonly record struct Vertex(Vector2 Source, Vector2 Baseline, Vector2 Destination);
    private readonly record struct Triangle(Vertex A, Vertex B, Vertex C);
    private sealed record Mesh(Vertex[] Vertices, int Columns, int Rows);

    /// <summary>Transforms exclusive body layers so limbs can rotate and overlap without reducing the whole pose.</summary>
    public static PixelImage Render(IReadOnlyList<RenderSource> sources, float phase, MotionVariant variant,
        GenerationSettings settings, int width, int height, CancellationToken cancellation = default)
    {
        var plan = Timeline.Plan(sources, phase, variant, settings);
        var source = sources[plan.SourceIndex];
        var layers = LayerMap.Create(source, cancellation);
        return RenderFrame(source, layers, plan, phase, variant,
            settings.WorkingSize, settings.WorkingSize, null, cancellation).FitCell(width, height);
    }

    public static PixelImage Render(IReadOnlyList<RenderSource> sources, MotionCycle cycle, int frame,
        MotionVariant variant, int width, int height, CancellationToken cancellation = default)
    {
        if (frame < 0 || frame >= cycle.Frames.Count) throw new ArgumentOutOfRangeException(nameof(frame));
        var plan = cycle.Frames[frame];
        return RenderFrame(sources[plan.SourceIndex], cycle.SourceLayers[plan.SourceIndex], plan,
            (float)frame / cycle.Frames.Count, variant, cycle.WorkingSize, cycle.WorkingSize,
            cycle.LayerScales, cancellation).FitCell(width, height);
    }

    internal static float[] LayerLimits(RenderSource source, LayerMap layers, FramePlan plan, float phase,
        MotionVariant variant, CancellationToken cancellation)
    {
        var limits = new float[LayerCount];
        Array.Fill(limits, 1);
        // Bone layers are affine and cannot fold. Only the cloth's additional warp needs a mesh limit.
        if (layers.HasLayer((int)SpriteLayer.Coat))
            limits[(int)SpriteLayer.Coat] = SafeMeshAmount(
                CreateMesh(source, layers, SpriteLayer.Coat, plan, phase, variant, cancellation), cancellation);
        return limits;
    }

    private static Mesh CreateMesh(RenderSource source, LayerMap layers, SpriteLayer layer,
        FramePlan plan, float phase, MotionVariant variant, CancellationToken cancellation)
    {
        var bounds = layers.Bounds[(int)layer];
        if (bounds.Width == 0) return new([], 0, 0);
        int minX = Math.Clamp((int)MathF.Floor((float)bounds.X / source.Image.Width * GRID), 0, GRID - 1);
        int minY = Math.Clamp((int)MathF.Floor((float)bounds.Y / source.Image.Height * GRID), 0, GRID - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling((float)(bounds.X + bounds.Width) / source.Image.Width * GRID), minX + 1, GRID);
        int maxY = Math.Clamp((int)MathF.Ceiling((float)(bounds.Y + bounds.Height) / source.Image.Height * GRID), minY + 1, GRID);
        int columns = maxX - minX, rows = maxY - minY;
        var vertices = new Vertex[(columns + 1) * (rows + 1)];
        for (int y = 0; y <= rows; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x <= columns; x++)
            {
                var p = new Vector2((float)(minX + x) / GRID, (float)(minY + y) / GRID);
                var baseline = Transform(p, layer, source.Rig, plan.Pose);
                var warped = baseline;
                if (layer == SpriteLayer.Coat)
                    warped.X += variant.Cloth * MathF.Sin(2 * MathF.PI * phase - (p.Y - source.Rig[Joint.Hip].Y) * 5) *
                        Math.Clamp((p.Y - source.Rig[Joint.Hip].Y) * 3, 0, 1) * plan.SecondaryWeight;
                vertices[y * (columns + 1) + x] = new(p, baseline, warped);
            }
        }
        return new(vertices, columns, rows);
    }

    private static PixelImage RenderFrame(RenderSource source, LayerMap layers, FramePlan plan, float phase,
        MotionVariant variant, int width, int height, IReadOnlyList<float>? limits, CancellationToken cancellation)
    {
        var output = new byte[checked(width * height * 4)];
        ReadOnlySpan<SpriteLayer> order =
        [
            SpriteLayer.ArmBUpper, SpriteLayer.ArmBLower, SpriteLayer.ElbowB,
            SpriteLayer.LegBUpper, SpriteLayer.LegBLower, SpriteLayer.KneeB,
            SpriteLayer.Coat, SpriteLayer.Torso, SpriteLayer.ShoulderB,
            SpriteLayer.LegAUpper, SpriteLayer.LegALower, SpriteLayer.KneeA, SpriteLayer.Hip,
            SpriteLayer.ArmAUpper, SpriteLayer.ArmALower, SpriteLayer.ElbowA, SpriteLayer.ShoulderA, SpriteLayer.Head
        ];
        foreach (var layer in order)
        {
            cancellation.ThrowIfCancellationRequested();
            var mesh = CreateMesh(source, layers, layer, plan, phase, variant, cancellation);
            if (mesh.Columns == 0) continue;
            float safeAmount = layer == SpriteLayer.Coat ? SafeMeshAmount(mesh, cancellation) : 1;
            float amount = limits == null ? safeAmount : Math.Min(limits[(int)layer], safeAmount);
            if (amount < 1)
                for (int i = 0; i < mesh.Vertices.Length; i++)
                {
                    var vertex = mesh.Vertices[i];
                    mesh.Vertices[i] = vertex with
                    { Destination = amount <= 0 ? vertex.Baseline : Vector2.Lerp(vertex.Baseline, vertex.Destination, amount) };
                }
            for (int y = 0; y < mesh.Rows; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < mesh.Columns; x++)
                {
                    int i = y * (mesh.Columns + 1) + x;
                    var a = mesh.Vertices[i]; var b = mesh.Vertices[i + 1];
                    var c = mesh.Vertices[i + mesh.Columns + 1]; var d = mesh.Vertices[i + mesh.Columns + 2];
                    Rasterize(new(a, b, d), source.Image, layers, layer, output, width, height);
                    Rasterize(new(a, d, c), source.Image, layers, layer, output, width, height);
                }
            }
        }
        return new(width, height, output);
    }

    /// <summary>Limits only the secondary warp around the already posed, affine layer.</summary>
    private static float SafeMeshAmount(Mesh mesh, CancellationToken cancellation)
    {
        float amount = 1;
        for (int y = 0; y < mesh.Rows; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < mesh.Columns; x++)
            {
                int i = y * (mesh.Columns + 1) + x;
                var a = mesh.Vertices[i]; var b = mesh.Vertices[i + 1];
                var c = mesh.Vertices[i + mesh.Columns + 1]; var d = mesh.Vertices[i + mesh.Columns + 2];
                amount = Math.Min(amount, SafeAmount(a, b, d));
                amount = Math.Min(amount, SafeAmount(a, d, c));
            }
        }
        return amount < 1 ? amount * .98f : 1;
    }

    private static float SafeAmount(Vertex a, Vertex b, Vertex c)
    {
        if (!float.IsFinite(a.Destination.X) || !float.IsFinite(a.Destination.Y) ||
            !float.IsFinite(b.Destination.X) || !float.IsFinite(b.Destination.Y) ||
            !float.IsFinite(c.Destination.X) || !float.IsFinite(c.Destination.Y)) return 0;
        var u = b.Baseline - a.Baseline;
        var v = c.Baseline - a.Baseline;
        var du = (b.Destination - a.Destination) - u;
        var dv = (c.Destination - a.Destination) - v;
        float constant = Cross(u, v) * .85f;
        float linear = Cross(du, v) + Cross(u, dv);
        float quadratic = Cross(du, dv);
        if (Math.Abs(quadratic) < 1e-10f)
            return linear < 0 ? Math.Clamp(-constant / linear, 0, 1) : 1;
        float discriminant = linear * linear - 4 * quadratic * constant;
        if (discriminant < 0) return 1;
        float sqrt = MathF.Sqrt(discriminant);
        float q = -.5f * (linear + MathF.CopySign(sqrt, linear));
        if (q == 0) return 1;
        float first = q / quadratic, second = constant / q;
        float amount = 1;
        if (first > 0) amount = Math.Min(amount, first);
        if (second > 0) amount = Math.Min(amount, second);
        return amount;
    }

    private static Vector2 Transform(Vector2 p, SpriteLayer layer, Rig rest, Rig pose)
    {
        var cap = layer switch
        {
            SpriteLayer.ElbowA => (Joint.ElbowA, Joint.ShoulderA, Joint.HandA),
            SpriteLayer.ElbowB => (Joint.ElbowB, Joint.ShoulderB, Joint.HandB),
            SpriteLayer.KneeA => (Joint.KneeA, Joint.Hip, Joint.FootA),
            SpriteLayer.KneeB => (Joint.KneeB, Joint.Hip, Joint.FootB),
            SpriteLayer.ShoulderA => (Joint.ShoulderA, Joint.Neck, Joint.ElbowA),
            SpriteLayer.ShoulderB => (Joint.ShoulderB, Joint.Neck, Joint.ElbowB),
            SpriteLayer.Hip => (Joint.Hip, Joint.Neck, Joint.Neck),
            _ => ((Joint)(-1), Joint.Neck, Joint.Neck)
        };
        if ((int)cap.Item1 >= 0)
        {
            float first = BoneAngle(cap.Item1, cap.Item2);
            float second = BoneAngle(cap.Item1, cap.Item3);
            float capAngle = first + MathF.Atan2(MathF.Sin(second - first), MathF.Cos(second - first)) * .5f;
            return pose[cap.Item1].Vector + Motion.Rotate(p - rest[cap.Item1].Vector, capAngle);
        }
        var (root, end) = layer switch
        {
            SpriteLayer.Head => (Joint.Neck, Joint.Head),
            SpriteLayer.ArmAUpper => (Joint.ShoulderA, Joint.ElbowA),
            SpriteLayer.ArmALower => (Joint.ElbowA, Joint.HandA),
            SpriteLayer.ArmBUpper => (Joint.ShoulderB, Joint.ElbowB),
            SpriteLayer.ArmBLower => (Joint.ElbowB, Joint.HandB),
            SpriteLayer.LegAUpper => (Joint.Hip, Joint.KneeA),
            SpriteLayer.LegALower => (Joint.KneeA, Joint.FootA),
            SpriteLayer.LegBUpper => (Joint.Hip, Joint.KneeB),
            SpriteLayer.LegBLower => (Joint.KneeB, Joint.FootB),
            _ => (Joint.Hip, Joint.Neck)
        };
        var from = rest[end].Vector - rest[root].Vector;
        var to = pose[end].Vector - pose[root].Vector;
        if (from.LengthSquared() < 1e-10f) return pose[root].Vector + p - rest[root].Vector;
        float angle = MathF.Atan2(to.Y, to.X) - MathF.Atan2(from.Y, from.X);
        float scale = Math.Clamp(to.Length() / from.Length(), .65f, 1.5f);
        return pose[root].Vector + Motion.Rotate(p - rest[root].Vector, angle) * scale;

        float BoneAngle(Joint start, Joint finish)
        {
            var originalDirection = rest[finish].Vector - rest[start].Vector;
            var posedDirection = pose[finish].Vector - pose[start].Vector;
            if (originalDirection.LengthSquared() < 1e-10f || posedDirection.LengthSquared() < 1e-10f) return 0;
            return MathF.Atan2(posedDirection.Y, posedDirection.X) - MathF.Atan2(originalDirection.Y, originalDirection.X);
        }
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static bool TopLeft(Vector2 edge) => edge.Y < 0 || (edge.Y == 0 && edge.X > 0);
    private static bool Inside(float edge, bool topLeft, float tolerance) =>
        edge > tolerance || (edge >= -tolerance && topLeft);

    private static void Rasterize(Triangle triangle, PixelImage source, LayerMap layers, SpriteLayer layer,
        byte[] output, int width, int height)
    {
        float cell = Math.Min(width, height);
        var scale = new Vector2(cell);
        var offset = new Vector2((width - cell) * .5f, (height - cell) * .5f);
        var a = triangle.A.Destination * scale + offset; var b = triangle.B.Destination * scale + offset;
        var c = triangle.C.Destination * scale + offset;
        float area = Cross(b - a, c - a);
        if (!float.IsFinite(area) || area <= .0001f) return;
        int minX = Math.Clamp((int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
        int minY = Math.Clamp((int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
        bool ab = TopLeft(b - a), bc = TopLeft(c - b), ca = TopLeft(a - c);
        float tolerance = area * .000001f;
        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var point = new Vector2(x + .5f, y + .5f);
                // Half-open shared edges avoid writing translucent pixels twice within a layer.
                if (!Inside(Cross(b - a, point - a), ab, tolerance) ||
                    !Inside(Cross(c - b, point - b), bc, tolerance) ||
                    !Inside(Cross(a - c, point - c), ca, tolerance)) continue;
                float u = Cross(point - a, c - a) / area;
                float v = Cross(b - a, point - a) / area;
                var uv = triangle.A.Source * (1 - u - v) + triangle.B.Source * u + triangle.C.Source * v;
                int sx = Math.Clamp((int)(uv.X * source.Width), 0, source.Width - 1);
                int sy = Math.Clamp((int)(uv.Y * source.Height), 0, source.Height - 1);
                if (layers.OwnerAt(sx, sy) != (byte)layer) continue;
                int si = (sy * source.Width + sx) * 4;
                if (source.Pixels[si + 3] == 0) continue;
                Composite(source.Pixels.Slice(si, 4), output.AsSpan((y * width + x) * 4, 4));
            }
    }

    private static void Composite(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        int alpha = source[3];
        if (alpha == 255 || destination[3] == 0) { source.CopyTo(destination); return; }
        int inverse = 255 - alpha;
        int denominator = alpha * 255 + destination[3] * inverse;
        for (int channel = 0; channel < 3; channel++)
            destination[channel] = (byte)((source[channel] * alpha * 255 +
                destination[channel] * destination[3] * inverse + denominator / 2) / denominator);
        destination[3] = (byte)((denominator + 127) / 255);
    }
}
