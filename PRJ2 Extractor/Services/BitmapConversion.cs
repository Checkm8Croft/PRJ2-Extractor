using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PRJ2_Extractor.Core;

namespace PRJ2_Extractor.Services;

/// <summary>Conversions between the toolkit-free <see cref="PixelBuffer"/> of the Core and the bitmaps the window shows.</summary>
public static class BitmapConversion
{
    /// <summary>The atlas as a bitmap the preview can draw (blue, green, red plus an opaque alpha byte per pixel).</summary>
    public static Bitmap ToBitmap(PixelBuffer source)
    {
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var frame = bitmap.Lock();

        var row = new byte[source.Width * 4];
        for (int y = 0; y < source.Height; y++)
        {
            int from = y * source.Stride;
            for (int x = 0, i = 0; x < source.Width; x++, i += 4)
            {
                row[i] = source.Pixels[from + x * 3];
                row[i + 1] = source.Pixels[from + x * 3 + 1];
                row[i + 2] = source.Pixels[from + x * 3 + 2];
                row[i + 3] = 255;
            }
            Marshal.Copy(row, 0, frame.Address + y * frame.RowBytes, row.Length);
        }
        return bitmap;
    }

    /// <summary>A PNG file as a bitmap; null when it cannot be decoded.</summary>
    public static Bitmap? FromPng(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The pixels of a PNG file without its alpha channel, for saving it as a .tga; null when it cannot be decoded.</summary>
    public static PixelBuffer? PngToPixelBuffer(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var decoded = new Bitmap(stream);
            int width = decoded.PixelSize.Width, height = decoded.PixelSize.Height;

            using var copy = new WriteableBitmap(decoded.PixelSize, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using (var frame = copy.Lock())
                decoded.CopyPixels(frame, AlphaFormat.Unpremul);

            var result = new PixelBuffer(width, height);
            var row = new byte[width * 4];
            using var locked = copy.Lock();
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(locked.Address + y * locked.RowBytes, row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    result.Pixels[y * result.Stride + x * 3] = row[x * 4];
                    result.Pixels[y * result.Stride + x * 3 + 1] = row[x * 4 + 1];
                    result.Pixels[y * result.Stride + x * 3 + 2] = row[x * 4 + 2];
                }
            }
            return result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
