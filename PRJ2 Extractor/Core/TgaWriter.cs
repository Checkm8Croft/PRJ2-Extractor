using System.IO;
using System.Windows.Media.Imaging;

namespace PRJ2_Extractor.Core;

public static class TgaWriter
{
    public static void Save(BitmapSource source, string path)
    {
        if (source.Format != System.Windows.Media.PixelFormats.Bgr24 &&
            source.Format != System.Windows.Media.PixelFormats.Bgr32 &&
            source.Format != System.Windows.Media.PixelFormats.Pbgra32)
        {
            source = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgr24, null, 0);
        }

        int width = source.PixelWidth;
        int height = source.PixelHeight;
        int stride = width * 3;
        var pixels = new byte[stride * height];
        source.CopyPixels(pixels, stride, 0);

        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((byte)0); // ID length
        bw.Write((byte)0); // color map type
        bw.Write((byte)2); // image type - uncompressed true-color
        bw.Write((ushort)0); // color map origin
        bw.Write((ushort)0); // color map length
        bw.Write((byte)0); // color map entry size
        bw.Write((ushort)0); // x origin
        bw.Write((ushort)0); // y origin
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((byte)24); // bits per pixel
        bw.Write((byte)0x20); // top-left origin
        bw.Write(pixels);
    }
}
