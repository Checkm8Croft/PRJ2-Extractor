using System.IO;

namespace PRJ2_Extractor.Core;

/// <summary>One chunk of the TRNG/NGLE tail appended to a .tr4: the type tag and its payload words.</summary>
public sealed record NgChunk(ushort Type, ushort[] Data);

/// <summary>
/// Reader for the "extra NG header" that NGLE / Tomb Editor appends after the TR4 data. Layout (verified against
/// TombLib Compilers/Ng.cs and real TRNG levels): the file ends with the ASCII tag "NGLE" and a uint32 holding the size
/// of the whole header; the header starts with the word "NG", followed by chunks [size][type][data] where size counts
/// words (itself and type included; if bit 15 of the first word is set the size is a 31-bit value over two words and
/// the type is the third word). A size of 0 ends the sequence.
/// </summary>
public sealed class NgTail
{
    public const ushort TagAnimatedTextures = 0x8002;
    public const ushort TagRemapObjects = 0x8005;
    public const ushort TagExtraInfoRooms = 0x8009;
    public const ushort TagLevelFlags = 0x800D;
    public const ushort TagRemapTails = 0x8018;
    public const ushort TagRemapStatics = 0x8021;
    public const ushort TagVersionHeader = 0x8024;
    public const ushort TagRemapRooms = 0x8037;

    /// <summary>Bit 0 of the 0x800D chunk (FLT_NEW_TRIGGERS in TRNG-CE): the level uses NG triggers.</summary>
    public const int FlagNewTriggers = 0x1;

    public IReadOnlyList<NgChunk> Chunks { get; }
    public string? Version { get; }
    public int? LevelFlags { get; }

    /// <summary>
    /// True when FloorData uses the NG trigger encoding (FlipEffect and ActionNg actions carry an extra word).
    /// Requires the tail to be present; if the flags chunk exists, its NewTriggers bit must be set.
    /// </summary>
    public bool UsesNgTriggers => LevelFlags is null || (LevelFlags.Value & FlagNewTriggers) != 0;

    private NgTail(List<NgChunk> chunks)
    {
        Chunks = chunks;
        var flags = chunks.FirstOrDefault(c => c.Type == TagLevelFlags);
        if (flags is { Data.Length: >= 2 }) LevelFlags = flags.Data[0] | (flags.Data[1] << 16);
        var ver = chunks.FirstOrDefault(c => c.Type == TagVersionHeader);
        if (ver is { Data.Length: >= 4 }) Version = $"{ver.Data[0]}.{ver.Data[1]}.{ver.Data[2]}.{ver.Data[3]}";
    }

    public NgChunk? Find(ushort type) => Chunks.FirstOrDefault(c => c.Type == type);

    /// <summary>Returns the parsed tail, or null if the file has no (valid) NG tail.</summary>
    public static NgTail? TryRead(byte[] file)
    {
        if (file.Length < 16) return null;
        if (BitConverter.ToUInt32(file, file.Length - 8) != 0x454C474E) return null; // "NGLE"
        uint size = BitConverter.ToUInt32(file, file.Length - 4);
        if (size < 12 || size > file.Length) return null;
        int start = file.Length - (int)size;
        if (BitConverter.ToUInt16(file, start) != 0x474E) return null; // "NG"

        int wordCount = (int)(size - 2 - 8) / 2;
        var words = new ushort[wordCount];
        for (int i = 0; i < wordCount; i++) words[i] = BitConverter.ToUInt16(file, start + 2 + 2 * i);

        var chunks = new List<NgChunk>();
        int idx = 0;
        while (idx < wordCount)
        {
            int n, extra, type;
            if ((words[idx] & 0x8000) != 0)
            {
                if (idx + 2 >= wordCount) break;
                n = ((words[idx] & 0x7FFF) << 16) | words[idx + 1]; extra = 3; type = words[idx + 2];
            }
            else
            {
                if (idx + 1 >= wordCount) break;
                n = words[idx]; extra = 2; type = words[idx + 1];
            }
            if (n == 0) break;
            if (n < extra || idx + n > wordCount) return chunks.Count > 0 ? new NgTail(chunks) : null; // truncated: keep what is sound
            var data = new ushort[n - extra];
            Array.Copy(words, idx + extra, data, 0, data.Length);
            chunks.Add(new NgChunk((ushort)type, data));
            idx += n;
        }
        return new NgTail(chunks);
    }
}
