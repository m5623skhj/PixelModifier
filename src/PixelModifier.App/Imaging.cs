using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PixelModifier.Core;

namespace PixelModifier.App;

public static class Imaging
{
    public static PixelImage LoadPng(string path)
    {
        using var stream = File.OpenRead(path);
        byte[] signature = new byte[8];
        if (stream.Read(signature) != 8 || !signature.AsSpan().SequenceEqual(new byte[] {137, 80, 78, 71, 13, 10, 26, 10}))
            throw new InvalidDataException("PNG 파일만 입력할 수 있습니다.");
        stream.Position = 0;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        if ((long)frame.PixelWidth * frame.PixelHeight > 32_000_000)
            throw new InvalidDataException("입력 이미지가 너무 큽니다. 최대 3,200만 픽셀입니다.");
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        SwapRedBlue(pixels);
        var image = new PixelImage(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
        if (!image.HasTransparency()) throw new InvalidDataException("투명 배경이 있는 PNG를 사용해 주세요.");
        image.Bounds();
        return image;
    }
    public static BitmapSource Bitmap(PixelImage image)
    {
        byte[] pixels = image.CopyPixels();
        SwapRedBlue(pixels);
        var bitmap = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Bgra32,
            null, pixels, image.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }
    public static void SavePng(PixelImage image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Bitmap(image)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
    private static void SwapRedBlue(byte[] pixels)
    {
        for (int i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
    }
}
