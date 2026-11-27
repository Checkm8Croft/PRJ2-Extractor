namespace PRJ2_Extractor.Core;

/// <summary>
/// An uncompressed image: 3 bytes per pixel (blue, green, red), top row first, no padding between rows. It is what the loaders
/// produce for the texture atlas, so that nothing outside the user interface depends on a UI toolkit.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 3;

    public PixelBuffer(int width, int height) : this(width, height, new byte[width * height * 3]) { }

    public PixelBuffer(int width, int height, byte[] bgr)
    {
        if (width < 0 || height < 0 || bgr.Length < width * height * 3)
            throw new ArgumentException("The pixel array is smaller than width x height x 3 bytes.");
        Width = width;
        Height = height;
        Pixels = bgr;
    }

    /// <summary>Copies <paramref name="rows"/> full rows starting at <paramref name="sourceRow"/> into this buffer at <paramref name="destinationRow"/>.</summary>
    public void CopyRowsFrom(PixelBuffer source, int sourceRow, int destinationRow, int rows)
    {
        if (source.Width != Width) throw new ArgumentException("The buffers must have the same width.");
        Buffer.BlockCopy(source.Pixels, sourceRow * source.Stride, Pixels, destinationRow * Stride, rows * Stride);
    }
}
