using System.Numerics;

namespace PixelModifier.Core;

internal enum SpriteLayer : byte
{
    Torso, Head, ArmAUpper, ArmALower, ArmBUpper, ArmBLower,
    LegAUpper, LegALower, LegBUpper, LegBLower, Coat
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
        for (int y = 0; y < height; y++)
        {
            cancellation.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                if (source.Image.Pixels[(y * width + x) * 4 + 3] == 0) continue;
                var p = new Vector2((x + .5f) / width, (y + .5f) / height);
                var layer = source.Regions.At(p.X, p.Y) switch
                {
                    BodyPart.Head => SpriteLayer.Head,
                    BodyPart.Coat => SpriteLayer.Coat,
                    BodyPart.ArmA => Limb(p, Joint.ShoulderA, Joint.ElbowA, Joint.HandA, SpriteLayer.ArmAUpper, SpriteLayer.ArmALower),
                    BodyPart.ArmB => Limb(p, Joint.ShoulderB, Joint.ElbowB, Joint.HandB, SpriteLayer.ArmBUpper, SpriteLayer.ArmBLower),
                    BodyPart.LegA => Limb(p, Joint.Hip, Joint.KneeA, Joint.FootA, SpriteLayer.LegAUpper, SpriteLayer.LegALower),
                    BodyPart.LegB => Limb(p, Joint.Hip, Joint.KneeB, Joint.FootB, SpriteLayer.LegBUpper, SpriteLayer.LegBLower),
                    _ => SpriteLayer.Torso
                };
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

        SpriteLayer Limb(Vector2 p, Joint root, Joint middle, Joint end, SpriteLayer upper, SpriteLayer lower)
        {
            float upperDistance = Rigging.SegmentDistance(p, source.Rig[root].Vector, source.Rig[middle].Vector);
            float lowerDistance = Rigging.SegmentDistance(p, source.Rig[middle].Vector, source.Rig[end].Vector);
            return upperDistance <= lowerDistance ? upper : lower;
        }
    }
}
