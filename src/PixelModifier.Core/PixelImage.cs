namespace PixelModifier.Core;

public readonly record struct PixelBounds(int X, int Y, int Width, int Height);

/// <summary>RGBA source pixels are read-only after construction and safe to share with rendering workers.</summary>
public sealed class PixelImage
{
    private readonly byte[] pixels;
    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Pixels => pixels;
    public PixelImage(int width, int height, byte[] rgba)
    {
        if (width < 1 || height < 1 || rgba.Length != checked(width * height * 4))
            throw new ArgumentException("이미지 크기와 픽셀 데이터가 일치하지 않습니다.");
        Width = width; Height = height; pixels = rgba;
    }
    public byte[] CopyPixels() => (byte[])pixels.Clone();
    /// <summary>Fits the complete canvas without moving its anchor. Enlargements use whole pixel blocks.</summary>
    public PixelImage FitCell(int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (width == Width && height == Height) return this;
        float scale = Math.Min((float)width / Width, (float)height / Height);
        if (scale >= 1) scale = MathF.Floor(scale);
        int fittedWidth = Math.Max(1, (int)(Width * scale));
        int fittedHeight = Math.Max(1, (int)(Height * scale));
        int offsetX = (width - fittedWidth) / 2, offsetY = (height - fittedHeight) / 2;
        var output = new byte[checked(width * height * 4)];
        for (int y = 0; y < fittedHeight; y++)
            for (int x = 0; x < fittedWidth; x++)
            {
                int sx = Math.Min(Width - 1, (int)((x + .5f) * Width / fittedWidth));
                int sy = Math.Min(Height - 1, (int)((y + .5f) * Height / fittedHeight));
                pixels.AsSpan((sy * Width + sx) * 4, 4).CopyTo(
                    output.AsSpan(((y + offsetY) * width + x + offsetX) * 4, 4));
            }
        return new(width, height, output);
    }
    public PixelBounds Bounds()
    {
        int left = Width, top = Height, right = -1, bottom = -1;
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (pixels[(y * Width + x) * 4 + 3] != 0)
                { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < 0) throw new InvalidDataException("이미지가 모두 투명합니다.");
        return new(left, top, right - left + 1, bottom - top + 1);
    }
    public bool HasTransparency()
    {
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] < 255) return true;
        return false;
    }
    /// <summary>Aligns alpha bounds into a padded cell; nearest sampling preserves the source palette.</summary>
    public PixelImage Normalize(int width, int height)
    {
        var bounds = Bounds();
        float scale = Math.Min(width * .72f / bounds.Width, height * .80f / bounds.Height);
        float targetWidth = bounds.Width * scale;
        float targetHeight = bounds.Height * scale;
        float offsetX = (width - targetWidth) * .5f;
        float offsetY = height * .90f - targetHeight;
        var output = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                float u = (x + .5f - offsetX) / scale;
                float v = (y + .5f - offsetY) / scale;
                if (u < 0 || v < 0 || u >= bounds.Width || v >= bounds.Height) continue;
                int sx = bounds.X + (int)u, sy = bounds.Y + (int)v;
                pixels.AsSpan((sy * Width + sx) * 4, 4).CopyTo(output.AsSpan((y * width + x) * 4, 4));
            }
        return new(width, height, output);
    }
}
