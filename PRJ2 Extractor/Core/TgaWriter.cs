using System.IO;

namespace PRJ2_Extractor.Core;

public static class TgaWriter
{
    public static void Save(PixelBuffer source, string path)
    {
        int width = source.Width;
        int height = source.Height;
        var pixels = source.Pixels;

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
