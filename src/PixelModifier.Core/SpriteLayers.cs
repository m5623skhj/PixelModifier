using System.Numerics;

namespace PixelModifier.Core;

internal enum SpriteLayer : byte
{
    Torso, Head, ArmAUpper, ArmALower, ArmBUpper, ArmBLower,
    LegAUpper, LegALower, LegBUpper, LegBLower,
    ElbowA, ElbowB, KneeA, KneeB, ShoulderA, ShoulderB, Hip, Coat
}

/// <summary>Immutable, mutually exclusive ownership of visible source pixels; scoped to one rendering job.</summary>
public sealed class LayerMap
{
    private readonly byte[] owners;
    private readonly PixelBounds[] bounds;
    private readonly int width;
    internal ReadOnlySpan<PixelBounds> Bounds => bounds;
    internal byte OwnerAt(int x, int y) => owners[y * width + x];
    internal bool HasLayer(int layer) => bounds[layer].Width > 0;

    private LayerMap(int width, byte[] owners, PixelBounds[] bounds)
    { this.width = width; this.owners = owners; this.bounds = bounds; }

    internal static LayerMap Create(RenderSource source, CancellationToken cancellation)
    {
        int width = source.Image.Width, height = source.Image.Height;
        var owners = new byte[checked(width * height)];
        Array.Fill(owners, byte.MaxValue);
        var left = new int[Renderer.LayerCount]; var top = new int[Renderer.LayerCount];
        var right = new int[Renderer.LayerCount]; var bottom = new int[Renderer.LayerCount];
        Array.Fill(left, width); Array.Fill(top, height);
        Array.Fill(right, -1); Array.Fill(bottom, -1);
        // Reserve a disk of existing pixels at each joint. Its rotation follows both adjoining bones.
        var caps = new[]
        {
            (Joint.ElbowA, Joint.ShoulderA, Joint.HandA, BodyPart.ArmA, SpriteLayer.ElbowA),
            (Joint.ElbowB, Joint.ShoulderB, Joint.HandB, BodyPart.ArmB, SpriteLayer.ElbowB),
            (Joint.KneeA, Joint.Hip, Joint.FootA, BodyPart.LegA, SpriteLayer.KneeA),
            (Joint.KneeB, Joint.Hip, Joint.FootB, BodyPart.LegB, SpriteLayer.KneeB),
            (Joint.ShoulderA, Joint.Neck, Joint.ElbowA, BodyPart.ArmA, SpriteLayer.ShoulderA),
            (Joint.ShoulderB, Joint.Neck, Joint.ElbowB, BodyPart.ArmB, SpriteLayer.ShoulderB),
            (Joint.Hip, Joint.Neck, Joint.KneeA, BodyPart.Torso, SpriteLayer.Hip)
        };
        var radii = caps.Select(cap => Radius(cap.Item1, cap.Item2, cap.Item3, cap.Item4)).ToArray();
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if (source.Image.Pixels[(y * width + x) * 4 + 3] == 0) continue;
                var p = new Vector2((x + .5f) / width, (y + .5f) / height);
                var part = source.Regions.At(p.X, p.Y);
                var layer = part switch
                {
                    BodyPart.Head => SpriteLayer.Head,
                    BodyPart.Coat => SpriteLayer.Coat,
                    BodyPart.ArmA => Limb(p, Joint.ShoulderA, Joint.ElbowA, Joint.HandA, SpriteLayer.ArmAUpper, SpriteLayer.ArmALower),
                    BodyPart.ArmB => Limb(p, Joint.ShoulderB, Joint.ElbowB, Joint.HandB, SpriteLayer.ArmBUpper, SpriteLayer.ArmBLower),
                    BodyPart.LegA => Limb(p, Joint.Hip, Joint.KneeA, Joint.FootA, SpriteLayer.LegAUpper, SpriteLayer.LegALower),
                    BodyPart.LegB => Limb(p, Joint.Hip, Joint.KneeB, Joint.FootB, SpriteLayer.LegBUpper, SpriteLayer.LegBLower),
                    _ => SpriteLayer.Torso
                };
                float closest = 1;
                for (int capIndex = 0; capIndex < caps.Length; capIndex++)
                {
                    var cap = caps[capIndex];
                    bool allowed = part == cap.Item4 ||
                        ((cap.Item1 == Joint.ShoulderA || cap.Item1 == Joint.ShoulderB) && part == BodyPart.Torso) ||
                        (cap.Item1 == Joint.Hip && part is BodyPart.Coat or BodyPart.LegA or BodyPart.LegB);
                    if (!allowed || radii[capIndex] <= 0) continue;
                    float distance = Vector2.DistanceSquared(p, source.Rig[cap.Item1].Vector) /
                        (radii[capIndex] * radii[capIndex]);
                    if (distance >= closest) continue;
                    closest = distance; layer = cap.Item5;
                }
                int index = (int)layer;
                owners[y * width + x] = (byte)index;
                left[index] = Math.Min(left[index], x); top[index] = Math.Min(top[index], y);
                right[index] = Math.Max(right[index], x); bottom[index] = Math.Max(bottom[index], y);
            }
        }
        var bounds = new PixelBounds[Renderer.LayerCount];
        for (int i = 0; i < bounds.Length; i++)
            if (right[i] >= 0) bounds[i] = new(left[i], top[i], right[i] - left[i] + 1, bottom[i] - top[i] + 1);
        return new(width, owners, bounds);

        float Radius(Joint joint, Joint before, Joint after, BodyPart part)
        {
            cancellation.ThrowIfCancellationRequested();
            var center = source.Rig[joint].Vector;
            var axis = center - source.Rig[before].Vector;
            float length = Math.Min(axis.Length(), Vector2.Distance(center, source.Rig[after].Vector));
            if (length < 1e-5f) return 0;
            axis = Vector2.Normalize(axis);
            float pixel = 1f / Math.Min(width, height);
            float maximum = Math.Max(pixel * .85f, Math.Min(.045f, length * .3f));
            var distances = new List<float>();
            int minX = Math.Max(0, (int)((center.X - maximum) * width));
            int maxX = Math.Min(width - 1, (int)((center.X + maximum) * width));
            int minY = Math.Max(0, (int)((center.Y - maximum) * height));
            int maxY = Math.Min(height - 1, (int)((center.Y + maximum) * height));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    if (source.Image.Pixels[(y * width + x) * 4 + 3] < 128) continue;
                    var p = new Vector2((x + .5f) / width, (y + .5f) / height);
                    if (source.Regions.At(p.X, p.Y) != part) continue;
                    var delta = p - center;
                    if (Math.Abs(Vector2.Dot(delta, axis)) <= pixel * 1.5f)
                        distances.Add(Math.Abs(delta.X * axis.Y - delta.Y * axis.X));
                }
            if (distances.Count == 0) return 0;
            distances.Sort();
            return Math.Clamp(distances[(distances.Count - 1) * 3 / 4] + pixel * .5f, pixel * .85f, maximum);
        }

        SpriteLayer Limb(Vector2 p, Joint root, Joint middle, Joint end, SpriteLayer upper, SpriteLayer lower)
        {
            float upperDistance = Rigging.SegmentDistance(p, source.Rig[root].Vector, source.Rig[middle].Vector);
            float lowerDistance = Rigging.SegmentDistance(p, source.Rig[middle].Vector, source.Rig[end].Vector);
            return upperDistance <= lowerDistance ? upper : lower;
        }
    }
}
