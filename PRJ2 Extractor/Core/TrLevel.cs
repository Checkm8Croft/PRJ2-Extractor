using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PRJ2_Extractor.Models;

namespace PRJ2_Extractor.Core;

public class TrLevel : IDisposable
{
    private const uint Tr4Signature = 0x00345254;
    private const uint Tr4Encrypted = 0x63345254;

    public WriteableBitmap? TextureBitmap;
    public uint FileVersion;
    public ushort NumRoomTextiles, NumObjTextiles, NumBumpTextiles;
    public uint NumSounds;
    public ushort NumRooms;
    public uint NumAnimations, NumStateChanges, NumAnimDispatches, NumAnimCommands;
    public uint NumMeshtrees, SizeKeyframes, NumMoveables, NumStatics;
    public uint NumFloorData;
    public uint NumBoxes;
    public List<LevelSoundSource> SoundSources = [];
    public List<LevelCamera> Cameras = [];
    public List<LevelFlybyCamera> FlybyCameras = [];
    public HashSet<int> CameraFloorDataIndices = [];
    public HashSet<int> SinkFloorDataIndices = [];
    public ushort[] FloorData = [];
    public LevelRoom[] Rooms = [];
    public LevelBox[] Boxes = [];
    public ObjectTexture[] ObjectTextures = [];

    public void Dispose()
    {
        foreach (var room in Rooms)
        foreach (var sector in room.Sectors)
            sector.FloorInfo?.Clear();
    }

    private void ParseFloorData(ushort fdIndex, List<ParsedFloorData> list)
    {
        static void ParseFloorType(ushort arg, out int f, out int sub, out int e)
        {
            f = arg & 0x001F;
            sub = (arg & 0x7F00) >> 8;
            e = (arg & 0x8000) >> 15;
        }

        if (fdIndex == 0)
        {
            list.Add(new ParsedFloorData { Tipo = FloorType.Floor });
            return;
        }

        int e = 0, k = 0;
        while (e == 0 && fdIndex + k < NumFloorData)
        {
            ushort data = FloorData[fdIndex + k];
            k++;
            ParseFloorType(data, out int f, out int sub, out e);
            var fd = new ParsedFloorData { Tipo = (FloorType)f };
            if (fd.Tipo == FloorType.Door)
            {
                fd.ToRoom = FloorData[fdIndex + k];
                k++;
            }
            else if (fd.Tipo is FloorType.Tilt or FloorType.Roof)
            {
                fd.AddX = (sbyte)((FloorData[fdIndex + k] & 0xFF00) >> 8);
                fd.AddZ = (sbyte)(FloorData[fdIndex + k] & 0x00FF);
                k++;
            }
            else if (fd.Tipo == FloorType.Trigger)
            {
                bool isFirst = true;
                do
                {
                    data = FloorData[fdIndex + k];
                    k++;
                    if (isFirst) { isFirst = false; continue; } // TriggerSetup word, not an ActionList entry

                    int trigAction = (data & 0x7C00) >> 10;
                    int parameter = data & 0x03FF;
                    if (trigAction == 0x01) // Camera: uses Parameter as Cameras[] index, plus one extra word
                    {
                        CameraFloorDataIndices.Add(parameter & 0x7F);
                        data = FloorData[fdIndex + k]; // the end/cont bit for 2-word actions lives here, not on the entry word above
                        k++;
                    }
                    else if (trigAction == 0x02) // Underwater Current (Sink): Parameter is Cameras[] index
                    {
                        SinkFloorDataIndices.Add(parameter);
                    }
                    else if (trigAction == 0x0C) // Flyby: also has one extra word
                    {
                        data = FloorData[fdIndex + k];
                        k++;
                    }
                } while ((data & 0x8000) != 0x8000);
            }
            else if (fd.Tipo == FloorType.Climb)
            {
                fd.E = (sub & 0x0001) == 0x0001;
                fd.S = (sub & 0x0002) == 0x0002;
                fd.W = (sub & 0x0004) == 0x0004;
                fd.N = (sub & 0x0008) == 0x0008;
            }
            else if (fd.Tipo is >= FloorType.Split1 and <= FloorType.Nocol8)
            {
                fd.TriHLo = (data & 0x03E0) >> 5;
                fd.TriHHi = (data & 0x7C00) >> 10;
                data = FloorData[fdIndex + k];
                k++;
                fd.Corners[0] = (ushort)(data & 0x000F);
                fd.Corners[1] = (ushort)((data & 0x00F0) >> 4);
                fd.Corners[2] = (ushort)((data & 0x0F00) >> 8);
                fd.Corners[3] = (ushort)((data & 0xF000) >> 12);
            }
            list.Add(fd);
        }
    }

    public byte Load(string filename, IProgress<int>? progress = null)
    {
        progress?.Report(0);
        uint version = 0;
        byte result = 0;

        if (File.Exists(filename))
        {
            using var fs = File.OpenRead(filename);
            using var br = new BinaryReader(fs);
            version = br.ReadUInt32();
            if (version != Tr4Signature && version != Tr4Encrypted)
            {
                version = 0;
                result = 2;
            }
        }
        else
        {
            result = 1;
        }

        if (version == Tr4Encrypted)
        {
            result = 3;
            version = 0;
        }

        progress?.Report(1);
        if (result != 0) return result;

        if (Path.GetExtension(filename).Equals(".trc", StringComparison.OrdinalIgnoreCase))
        {
            byte r5 = LoadTr5(filename, progress);
            if (r5 == 0) PostProcessRooms();
            return r5;
        }

        var memfile = new MemoryStream(File.ReadAllBytes(filename));
        try
        {
            using var br = new BinaryReader(memfile, Encoding.UTF8, leaveOpen: true);
            long fileSize = memfile.Length;

            FileVersion = br.ReadUInt32();
            NumRoomTextiles = br.ReadUInt16();
            NumObjTextiles = br.ReadUInt16();
            NumBumpTextiles = br.ReadUInt16();
            memfile.Seek(4, SeekOrigin.Current);
            uint size = br.ReadUInt32();
            var compressedTex = br.ReadBytes((int)size);
            progress?.Report((int)(memfile.Position * 100 / fileSize));

            var tex32 = new MemoryStream();
            using (var geometry1 = new MemoryStream(compressedTex))
            using (var zlib = new ZLibStream(geometry1, CompressionMode.Decompress))
                zlib.CopyTo(tex32);
            tex32.Position = 0;
            // Atlas layout MUST mirror the unified tile numbering that ObjectTexture.TileAndFlag
            // indexes into: room tiles first (0..NumRoomTextiles-1), then object tiles
            // (NumRoomTextiles..NumRoomTextiles+NumObjTextiles-1), then bump tiles. Object tiles
            // were previously skipped entirely (never read into TextureBitmap at all), which is
            // wrong: room-geometry faces can and do legitimately reference tiles in the object-tile
            // range (verified: alexhub2 has room faces using tiles up to index 12 against only 4
            // room tiles) -- skipping them left ToPrjTexInfo's Y = tile*256+minY pointing at missing
            // or (once bump tiles were appended) wrong image data, which is what Tomb Editor was
            // reporting as "TEXTURE OUT OF BOUNDS" / garbled textures.
            int totalHeight = (NumRoomTextiles + NumObjTextiles) * 256;
            if (NumBumpTextiles > 0)
                totalHeight += (NumBumpTextiles / 2) * 256;

            var allPixels = new byte[256 * totalHeight * 3];
            using (var br3 = new BinaryReader(tex32, Encoding.UTF8, leaveOpen: true))
            {
                int offset = 0;
                int roomAndObjRows = (NumRoomTextiles + NumObjTextiles) * 256;
                for (int i = 0; i < roomAndObjRows; i++)
                {
                    if (i % (256 * 2) == 0) progress?.Report(Math.Min(99, (int)(memfile.Position * 100 / fileSize) + 1));
                    for (int j = 0; j < 256; j++)
                    {
                        byte bl = br3.ReadByte(), gr = br3.ReadByte(), rd = br3.ReadByte(), al = br3.ReadByte();
                        if (al == 0) { bl = 255; rd = 255; gr = 0; }
                        allPixels[offset++] = bl;
                        allPixels[offset++] = gr;
                        allPixels[offset++] = rd;
                    }
                }
                if (NumBumpTextiles > 0)
                {
                    for (int i = roomAndObjRows; i < totalHeight; i++)
                    {
                        if (i % (256 * 2) == 0) progress?.Report(Math.Min(99, (int)(memfile.Position * 100 / fileSize) + 1));
                        for (int j = 0; j < 256; j++)
                        {
                            byte bl = br3.ReadByte(), gr = br3.ReadByte(), rd = br3.ReadByte(), al = br3.ReadByte();
                            if (al == 0) { bl = 255; rd = 255; gr = 0; }
                            allPixels[offset++] = bl;
                            allPixels[offset++] = gr;
                            allPixels[offset++] = rd;
                        }
                    }
                }
            }
            tex32.Dispose();

            var bmp = new WriteableBitmap(256, totalHeight, 96, 96, PixelFormats.Bgr24, null);
            bmp.WritePixels(new Int32Rect(0, 0, 256, totalHeight), allPixels, 256 * 3, 0);
            TextureBitmap = bmp;

            memfile.Seek(4, SeekOrigin.Current);
            size = br.ReadUInt32();
            memfile.Seek(size, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));
            memfile.Seek(4, SeekOrigin.Current);
            size = br.ReadUInt32();
            memfile.Seek(size, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));

            br.ReadUInt32(); // size2 unused
            size = br.ReadUInt32();
            var compressedGeo = br.ReadBytes((int)size);

            var geometry = new MemoryStream();
            using (var geometry1 = new MemoryStream(compressedGeo))
            using (var zlib = new ZLibStream(geometry1, CompressionMode.Decompress))
                zlib.CopyTo(geometry);
            geometry.Position = 0;

            NumSounds = br.ReadUInt32();
            using var br2 = new BinaryReader(geometry, Encoding.UTF8, leaveOpen: true);
            geometry.Seek(4, SeekOrigin.Current);
            NumRooms = br2.ReadUInt16();
            Rooms = new LevelRoom[NumRooms];

            for (int i = 0; i < NumRooms; i++)
            {
                progress?.Report(Math.Min(99, (int)(memfile.Position * 100 / fileSize) + 1));
                var r = new LevelRoom
                {
                    // TRosettaStone tr4_room_info: file order is X, Z, YBottom, YTop.
                    X = br2.ReadInt32(),
                    Z = br2.ReadInt32(),
                    YBottom = br2.ReadInt32(),
                    YTop = br2.ReadInt32()
                };
                size = br2.ReadUInt32();
                var roomDataEnd = geometry.Position + size * 2;
                ReadRoomData(br2, r);
                geometry.Position = roomDataEnd;
                r.NumPortals = br2.ReadUInt16();
                r.Portals = new Portal[r.NumPortals];
                for (int j = 0; j < r.NumPortals; j++)
                    r.Portals[j] = ReadPortal(br2);
                // Per TRosettaStone (tr_room struct): the file stores NumZsectors FIRST,
                // then NumXsectors SECOND. Read order matches that here; the sector-copy
                // loop below is X-major (idx = X_idx*NumZ + Z_idx) to match spec ordering.
                r.NumZ = br2.ReadUInt16();
                r.NumX = br2.ReadUInt16();
                r.Sectors = new LevelSector[r.NumX * r.NumZ];
                for (int j = 0; j < r.NumX * r.NumZ; j++)
                {
                    r.Sectors[j] = ReadSector(br2);
                    r.Sectors[j].HasFd = false;
                }
                r.Colour.B = br2.ReadByte();
                r.Colour.G = br2.ReadByte();
                r.Colour.R = br2.ReadByte();
                r.Colour.A = br2.ReadByte();
                ushort lightCount = br2.ReadUInt16();
                for (int li = 0; li < lightCount; li++)
                    r.Lights.Add(ReadLight(br2));
                ushort staticCount = br2.ReadUInt16();
                geometry.Seek(staticCount * 20, SeekOrigin.Current);
                r.AltRoom = br2.ReadInt16();
                r.Flags = br2.ReadUInt16();
                r.WaterScheme = br2.ReadByte();
                r.Reverb = br2.ReadByte();
                r.AltGroup = br2.ReadByte();
                Rooms[i] = r;
            }

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            NumFloorData = br2.ReadUInt32();
            FloorData = new ushort[NumFloorData];
            for (int i = 0; i < NumFloorData; i++)
                FloorData[i] = br2.ReadUInt16();

            size = br2.ReadUInt32();
            geometry.Seek(size * 2, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));
            size = br2.ReadUInt32();
            geometry.Seek(size * 4, SeekOrigin.Current);
            NumAnimations = br2.ReadUInt32();
            geometry.Seek(NumAnimations * 40, SeekOrigin.Current);
            NumStateChanges = br2.ReadUInt32();
            geometry.Seek(NumStateChanges * 6, SeekOrigin.Current);
            NumAnimDispatches = br2.ReadUInt32();
            geometry.Seek(NumAnimDispatches * 8, SeekOrigin.Current);
            NumAnimCommands = br2.ReadUInt32();
            geometry.Seek(NumAnimCommands * 2, SeekOrigin.Current);
            NumMeshtrees = br2.ReadUInt32();
            geometry.Seek(NumMeshtrees * 4, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));
            SizeKeyframes = br2.ReadUInt32();
            geometry.Seek(SizeKeyframes * 2, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));
            NumMoveables = br2.ReadUInt32();
            geometry.Seek(NumMoveables * 18, SeekOrigin.Current);
            NumStatics = br2.ReadUInt32();
            geometry.Seek(NumStatics * 32, SeekOrigin.Current);

            var s = $"{br2.ReadChar()}{br2.ReadChar()}{br2.ReadChar()}".ToLowerInvariant();
            if (s != "spr")
            {
                MessageBox.Show("SPR landmark not read correctly!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                result = 4;
            }

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            size = br2.ReadUInt32();
            geometry.Seek(size * 16, SeekOrigin.Current);
            size = br2.ReadUInt32();
            geometry.Seek(size * 8, SeekOrigin.Current);
            size = br2.ReadUInt32();
            Cameras = new List<LevelCamera>((int)size);
            for (int i = 0; i < size; i++)
                Cameras.Add(new LevelCamera
                {
                    X = br2.ReadInt32(),
                    Y = br2.ReadInt32(),
                    Z = br2.ReadInt32(),
                    Room = br2.ReadInt16(),
                    Flags = br2.ReadUInt16(),
                });
            size = br2.ReadUInt32();
            FlybyCameras = new List<LevelFlybyCamera>((int)size);
            for (int i = 0; i < size; i++)
                FlybyCameras.Add(new LevelFlybyCamera
                {
                    X = br2.ReadInt32(),
                    Y = br2.ReadInt32(),
                    Z = br2.ReadInt32(),
                    DirX = br2.ReadInt32(),
                    DirY = br2.ReadInt32(),
                    DirZ = br2.ReadInt32(),
                    Sequence = br2.ReadByte(),
                    Index = br2.ReadByte(),
                    Fov = br2.ReadUInt16(),
                    Roll = br2.ReadInt16(),
                    Timer = br2.ReadUInt16(),
                    Speed = br2.ReadUInt16(),
                    Flags = br2.ReadUInt16(),
                    RoomId = br2.ReadUInt32(),
                });
            size = br2.ReadUInt32();
            SoundSources = new List<LevelSoundSource>((int)size);
            for (int i = 0; i < size; i++)
                SoundSources.Add(new LevelSoundSource
                {
                    X = br2.ReadInt32(),
                    Y = br2.ReadInt32(),
                    Z = br2.ReadInt32(),
                    SoundId = br2.ReadUInt16(),
                    Flags = br2.ReadUInt16(),
                });
            NumBoxes = br2.ReadUInt32();
            Boxes = new LevelBox[NumBoxes];
            for (int i = 0; i < NumBoxes; i++)
                Boxes[i] = ReadBox(br2);

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            size = br2.ReadUInt32();
            geometry.Seek(size * 2, SeekOrigin.Current);
            geometry.Seek(NumBoxes * 20, SeekOrigin.Current);
            size = br2.ReadUInt32();
            geometry.Seek(size * 2, SeekOrigin.Current);
            geometry.Seek(1, SeekOrigin.Current);
            s = $"{br2.ReadChar()}{br2.ReadChar()}{br2.ReadChar()}".ToLowerInvariant();
            if (s != "tex")
            {
                MessageBox.Show("TEX landmark not read correctly!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                result = 4;
            }
            size = br2.ReadUInt32();
            ObjectTextures = new ObjectTexture[size];
            for (int i = 0; i < ObjectTextures.Length; i++)
                ObjectTextures[i] = ReadObjectTexture(br2);
            progress?.Report(100);
        }
        finally
        {
            memfile.Dispose();
        }

        if (result == 0) PostProcessRooms();
        return result;
    }

    /// <summary>
    /// Shared tail step for both the TR4 (.tr4) and TR5 (.trc) loaders: resolves each sector's raw
    /// FDindex into parsed FloorData entries, and links flip/alternate rooms. FloorData itself
    /// (function/subfunction bitfield layout) is byte-identical across TR3, TR4 and TR5 per
    /// TRosettaStone, so ParseFloorData needs no version-specific branching.
    /// </summary>
    private void PostProcessRooms()
    {
        for (int i = 0; i < Rooms.Length; i++)
        {
            for (int j = 0; j < Rooms[i].Sectors.Length; j++)
            {
                if (Rooms[i].Sectors[j].FdIndex == 0) continue;
                var sectorFd = new List<ParsedFloorData>();
                Rooms[i].Sectors[j].HasFd = true;
                ParseFloorData(Rooms[i].Sectors[j].FdIndex, sectorFd);
                Rooms[i].Sectors[j].FloorInfo = sectorFd;
            }
            if (Rooms[i].AltRoom != -1 && Rooms[i].AltRoom <= Rooms.Length - 1)
            {
                Rooms[Rooms[i].AltRoom].IsFlipRoom = true;
                Rooms[Rooms[i].AltRoom].OriginalRoom = (short)i;
            }
        }
    }

    /// <summary>
    /// Loads a TR5 (.trc) level. Unlike TR4, TR5's room/animation/object data is NOT wrapped in a
    /// single zlib chunk (TRosettaStone: "In TR5, those chunks aren't compressed anymore" -- verified
    /// against trlevel's Level_tr5_pc.cpp, which reads everything directly and sequentially after the
    /// textiles). Textiles themselves are still zlib-compressed exactly as in TR4 (read_textiles_tr4_5
    /// is literally shared between TR4 and TR5 in trlevel), so that part of the existing TR4 code is
    /// reused verbatim below. Field layouts and read order (level header, room header/lights/fog
    /// bulbs/sectors/portals/static meshes/layers/polys/vertices, model/object-texture sizes, marker
    /// skip widths) were cross-verified against trlevel (github.com/chreden/trview,
    /// trlevel/Level_tr5_pc.cpp and trtypes.h) rather than TRosettaStone alone, since TRosettaStone's
    /// TR5 section disagreed with trlevel on some struct sizes (e.g. fog bulb: 36 vs trlevel's
    /// verified-working 40 bytes) and trlevel is actual code exercised against real level files.
    /// </summary>
    private byte LoadTr5(string filename, IProgress<int>? progress)
    {
        byte result = 0;
        var memfile = new MemoryStream(File.ReadAllBytes(filename));
        try
        {
            using var br = new BinaryReader(memfile, Encoding.UTF8, leaveOpen: true);
            long fileSize = memfile.Length;

            // --- Textiles: identical compressed-block layout to TR4 (shared read_textiles_tr4_5). ---
            FileVersion = br.ReadUInt32();
            NumRoomTextiles = br.ReadUInt16();
            NumObjTextiles = br.ReadUInt16();
            NumBumpTextiles = br.ReadUInt16();
            memfile.Seek(4, SeekOrigin.Current);
            uint size = br.ReadUInt32();
            var compressedTex = br.ReadBytes((int)size);
            progress?.Report((int)(memfile.Position * 100 / fileSize));

            var tex32 = new MemoryStream();
            using (var geometry1 = new MemoryStream(compressedTex))
            using (var zlib = new ZLibStream(geometry1, CompressionMode.Decompress))
                zlib.CopyTo(tex32);
            tex32.Position = 0;
            // Same fix as the TR4 loader above: atlas must include object tiles (not skip them) so
            // it mirrors the unified tile numbering ObjectTexture.TileAndFlag indexes into.
            int totalHeight = (NumRoomTextiles + NumObjTextiles) * 256;
            if (NumBumpTextiles > 0)
                totalHeight += (NumBumpTextiles / 2) * 256;

            var allPixels = new byte[256 * totalHeight * 3];
            using (var br3 = new BinaryReader(tex32, Encoding.UTF8, leaveOpen: true))
            {
                int offset = 0;
                int roomAndObjRows = (NumRoomTextiles + NumObjTextiles) * 256;
                for (int i = 0; i < roomAndObjRows; i++)
                {
                    for (int j = 0; j < 256; j++)
                    {
                        byte bl = br3.ReadByte(), gr = br3.ReadByte(), rd = br3.ReadByte(), al = br3.ReadByte();
                        if (al == 0) { bl = 255; rd = 255; gr = 0; }
                        allPixels[offset++] = bl;
                        allPixels[offset++] = gr;
                        allPixels[offset++] = rd;
                    }
                }
                if (NumBumpTextiles > 0)
                {
                    for (int i = roomAndObjRows; i < totalHeight; i++)
                    {
                        for (int j = 0; j < 256; j++)
                        {
                            byte bl = br3.ReadByte(), gr = br3.ReadByte(), rd = br3.ReadByte(), al = br3.ReadByte();
                            if (al == 0) { bl = 255; rd = 255; gr = 0; }
                            allPixels[offset++] = bl;
                            allPixels[offset++] = gr;
                            allPixels[offset++] = rd;
                        }
                    }
                }
            }
            tex32.Dispose();

            var bmp = new WriteableBitmap(256, totalHeight, 96, 96, PixelFormats.Bgr24, null);
            bmp.WritePixels(new Int32Rect(0, 0, 256, totalHeight), allPixels, 256 * 3, 0);
            TextureBitmap = bmp;

            // Skip 16-bit fallback compressed textile block.
            memfile.Seek(4, SeekOrigin.Current);
            size = br.ReadUInt32();
            memfile.Seek(size, SeekOrigin.Current);
            // Skip 2-tile misc compressed textile block.
            memfile.Seek(4, SeekOrigin.Current);
            size = br.ReadUInt32();
            memfile.Seek(size, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));

            // --- TR5-only fields between textiles and the room array (absent in TR4). ---
            br.ReadUInt16(); // LaraType
            br.ReadUInt16(); // WeatherType
            memfile.Seek(28, SeekOrigin.Current); // unknown/padding

            // Vestigial in TR5 (the data that follows is NOT actually compressed -- trlevel's own
            // comment reads "unused in Tomb5"); real files only use these, at the very end, to
            // relocate to the sound-samples section, which is well past what we parse here.
            br.ReadUInt32(); // uncompressed_size
            br.ReadUInt32(); // compressed_size
            br.ReadUInt32(); // unused value

            uint numRooms = br.ReadUInt32();
            if (numRooms > ushort.MaxValue)
            {
                MessageBox.Show("TR5 room count exceeds supported range!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return 4;
            }
            NumRooms = (ushort)numRooms;
            Rooms = new LevelRoom[NumRooms];
            for (int i = 0; i < NumRooms; i++)
            {
                progress?.Report(Math.Min(99, (int)(memfile.Position * 100 / fileSize) + 1));
                Rooms[i] = ReadTr5Room(br, memfile);
            }

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            NumFloorData = br.ReadUInt32();
            FloorData = new ushort[NumFloorData];
            for (int i = 0; i < NumFloorData; i++)
                FloorData[i] = br.ReadUInt16();

            size = br.ReadUInt32();
            memfile.Seek(size * 2, SeekOrigin.Current); // mesh data (words)
            size = br.ReadUInt32();
            memfile.Seek(size * 4, SeekOrigin.Current); // mesh pointers (dwords)
            NumAnimations = br.ReadUInt32();
            memfile.Seek(NumAnimations * 40, SeekOrigin.Current); // tr4_animation, same 40 bytes as TR4
            NumStateChanges = br.ReadUInt32();
            memfile.Seek(NumStateChanges * 6, SeekOrigin.Current);
            NumAnimDispatches = br.ReadUInt32();
            memfile.Seek(NumAnimDispatches * 8, SeekOrigin.Current);
            NumAnimCommands = br.ReadUInt32();
            memfile.Seek(NumAnimCommands * 2, SeekOrigin.Current);
            NumMeshtrees = br.ReadUInt32();
            memfile.Seek(NumMeshtrees * 4, SeekOrigin.Current);
            progress?.Report((int)(memfile.Position * 100 / fileSize));
            SizeKeyframes = br.ReadUInt32();
            memfile.Seek(SizeKeyframes * 2, SeekOrigin.Current);
            NumMoveables = br.ReadUInt32();
            // tr5_model = tr_model (18 bytes) + a 2-byte filler = 20 bytes (trtypes.h: struct tr5_model
            // { tr_model model; uint16_t filler; };), unlike TR4's plain 18-byte tr_model.
            memfile.Seek(NumMoveables * 20, SeekOrigin.Current);
            NumStatics = br.ReadUInt32();
            memfile.Seek(NumStatics * 32, SeekOrigin.Current);

            // SPR marker: trlevel skips 4 bytes unconditionally here for TR5 (vs. TR4's validated
            // 3-byte "SPR" text skip), so we don't attempt a text check like the TR4 path does.
            memfile.Seek(4, SeekOrigin.Current);

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            size = br.ReadUInt32();
            memfile.Seek(size * 16, SeekOrigin.Current); // sprite textures
            size = br.ReadUInt32();
            memfile.Seek(size * 8, SeekOrigin.Current); // sprite sequences

            size = br.ReadUInt32();
            Cameras = new List<LevelCamera>((int)size);
            for (int i = 0; i < size; i++)
                Cameras.Add(new LevelCamera
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    Room = br.ReadInt16(),
                    Flags = br.ReadUInt16(),
                });

            size = br.ReadUInt32();
            FlybyCameras = new List<LevelFlybyCamera>((int)size);
            for (int i = 0; i < size; i++)
                FlybyCameras.Add(new LevelFlybyCamera
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    DirX = br.ReadInt32(),
                    DirY = br.ReadInt32(),
                    DirZ = br.ReadInt32(),
                    Sequence = br.ReadByte(),
                    Index = br.ReadByte(),
                    Fov = br.ReadUInt16(),
                    Roll = br.ReadInt16(),
                    Timer = br.ReadUInt16(),
                    Speed = br.ReadUInt16(),
                    Flags = br.ReadUInt16(),
                    RoomId = br.ReadUInt32(),
                });

            size = br.ReadUInt32();
            SoundSources = new List<LevelSoundSource>((int)size);
            for (int i = 0; i < size; i++)
                SoundSources.Add(new LevelSoundSource
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    SoundId = br.ReadUInt16(),
                    Flags = br.ReadUInt16(),
                });

            NumBoxes = br.ReadUInt32();
            Boxes = new LevelBox[NumBoxes];
            for (int i = 0; i < NumBoxes; i++)
                Boxes[i] = ReadBox(br);

            progress?.Report((int)(memfile.Position * 100 / fileSize));
            size = br.ReadUInt32();
            memfile.Seek(size * 2, SeekOrigin.Current); // overlaps
            memfile.Seek(NumBoxes * 20, SeekOrigin.Current); // zones
            size = br.ReadUInt32();
            memfile.Seek(size * 2, SeekOrigin.Current); // animated textures
            memfile.Seek(1, SeekOrigin.Current); // animated texture uv count byte

            // TEX marker: 4-byte skip in TR5 (vs TR4's validated 3-byte "TEX" text), per trlevel.
            memfile.Seek(4, SeekOrigin.Current);

            size = br.ReadUInt32();
            ObjectTextures = new ObjectTexture[size];
            for (int i = 0; i < ObjectTextures.Length; i++)
                ObjectTextures[i] = ReadObjectTexture(br, isTr5: true);

            progress?.Report(100);
        }
        finally
        {
            memfile.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Reads one TR5 room. Unlike TR1-4's single sequential room block, a TR5 room is a fixed
    /// 208-byte header (tr5_room_header) followed by several sub-blocks addressed by BYTE OFFSETS
    /// relative to the position right after the header ("dataStart"), rather than laid out strictly
    /// in file order. Field layout/order verified against trlevel's Level_tr5_pc.cpp
    /// (load_tr5_pc_room) and trtypes.h (tr5_room_header/tr5_room_light/tr5_fog_bulb/tr5_room_layer/
    /// tr5_room_vertex/tr4_mesh_face3/tr4_mesh_face4).
    /// </summary>
    private static LevelRoom ReadTr5Room(BinaryReader br, MemoryStream stream)
    {
        stream.Seek(4, SeekOrigin.Current);        // "XELA" room marker
        uint roomDataSize = br.ReadUInt32();
        long roomEnd = stream.Position + roomDataSize;

        var r = new LevelRoom();

        // --- tr5_room_header (208 bytes) ---
        stream.Seek(4, SeekOrigin.Current);        // separator
        br.ReadUInt32();                            // end_sd_offset (unused: sector count comes from num_x/z_sectors)
        uint startSdOffset = br.ReadUInt32();
        stream.Seek(4, SeekOrigin.Current);        // separator
        uint endPortalOffset = br.ReadUInt32();

        // tr_room_info: x, y, z, yBottom, yTop (5 int32). "y" (the room's own world-Y offset,
        // distinct from yBottom/yTop) has no TR1-4 equivalent and LevelRoom doesn't model it --
        // discarded, matching how TR1-4 rooms (which lack this field entirely) are handled.
        r.X = br.ReadInt32();
        br.ReadInt32();                             // y (unused)
        r.Z = br.ReadInt32();
        r.YBottom = br.ReadInt32();
        r.YTop = br.ReadInt32();

        r.NumZ = br.ReadUInt16();
        r.NumX = br.ReadUInt16();
        // Raw colour read byte-by-byte into the same B,G,R,A field order the TR4 path uses, so
        // downstream consumers (Prj2Exporter) see identical semantics either way.
        r.Colour.B = br.ReadByte();
        r.Colour.G = br.ReadByte();
        r.Colour.R = br.ReadByte();
        r.Colour.A = br.ReadByte();
        ushort numLights = br.ReadUInt16();
        ushort numStaticMeshes = br.ReadUInt16();
        r.Reverb = br.ReadByte();
        r.AltGroup = br.ReadByte();
        r.WaterScheme = (byte)br.ReadUInt16();
        stream.Seek(20, SeekOrigin.Current);       // filler/separator block
        r.AltRoom = br.ReadInt16();
        r.Flags = br.ReadUInt16();
        stream.Seek(20, SeekOrigin.Current);       // filler/separator block
        br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // room_x/y/z float duplicates of info.x/y/z (unused)
        stream.Seek(24, SeekOrigin.Current);       // filler/separator block
        br.ReadUInt32();                            // num_room_triangles (unused, layers give us this)
        br.ReadUInt32();                            // num_room_rectangles (unused)
        br.ReadUInt32();                            // room_lights pointer (runtime-only)
        br.ReadUInt32();                            // fog_bulbs pointer (runtime-only)
        br.ReadUInt32();                            // num_lights2 (duplicate of numLights)
        uint numFogBulbs = br.ReadUInt32();
        br.ReadSingle();                            // room_y_top (unused)
        br.ReadSingle();                            // room_y_bottom (unused)
        uint numLayers = br.ReadUInt32();
        uint layerOffset = br.ReadUInt32();
        uint verticesOffset = br.ReadUInt32();
        uint polyOffset = br.ReadUInt32();
        br.ReadUInt32();                            // poly_offset2 (unused)
        br.ReadUInt32();                            // vertices_size (unused, byte size of the vertex block)
        stream.Seek(16, SeekOrigin.Current);       // trailing separator[4]

        long dataStart = stream.Position;          // the offsets above are relative to here

        // --- Lights (immediately after the header, sequentially -- NOT offset-addressed). ---
        // IMPORTANT: this layout is NOT the vanilla Core Design/trlevel tr5_room_light struct (92
        // bytes, 4-float colour, int position/direction twins as verbatim duplicates). It is TombLib's
        // OWN encoding, verified directly against its compiler source
        // (TombLib/LevelData/Compilers/Structs.cs, PrjRoom.WriteTr5): a distinct, TombLib-specific
        // 88-byte layout, since this extractor's real-world input is Tomb-Editor-built TR5 levels.
        // Per-light record (88 bytes), in file order:
        //   X,Y,Z (float,12) | ColourR,G,B (float,12, already Color/255.0f normalized) |
        //   ShadowIntensityOrSentinel (uint32,4: (int)((Intensity/8191)*255) for LightType==3
        //     [Shadow], else the sentinel 0xCDCDCDCD) | In (float,4) | Out (float,4) |
        //   SpotInAngle2x (float,4: Acos(In)*2 for Spot else 0, redundant with In -- discarded) |
        //   SpotOutAngle2x (float,4, redundant -- discarded) | CutOff (float,4) |
        //   -DirectionX,-DirectionY,-DirectionZ (float,12) | X,Y,Z again as int32 (12, redundant
        //     duplicate of the float position -- discarded) | fixed-point direction*16384 as int32
        //     (12, redundant -- discarded) | LightType (byte,1) | 0xCD filler (byte x3).
        // No FogBulb placeholder slots are interleaved in the lights array at all: TombLib splits
        // lights (LightType != 4) and bulbs (LightType == 4) into two separate, cleanly sequential
        // arrays (numLights above already excludes bulbs; numFogBulbs below is the bulb array's own
        // count), unlike the vanilla format's inline-sentinel scheme.
        for (int i = 0; i < numLights; i++)
        {
            float posX = br.ReadSingle();
            float posY = br.ReadSingle();
            float posZ = br.ReadSingle();
            float colR = br.ReadSingle();
            float colG = br.ReadSingle();
            float colB = br.ReadSingle();
            uint shadowOrSentinel = br.ReadUInt32();
            float inVal = br.ReadSingle();
            float outVal = br.ReadSingle();
            br.ReadSingle(); br.ReadSingle();       // spot in/out angle*2 (redundant with In/Out -- discarded)
            float cutOff = br.ReadSingle();
            float dirX = br.ReadSingle();           // -DirectionX as TombLib wrote it
            float dirY = br.ReadSingle();           // -DirectionY
            float dirZ = br.ReadSingle();           // -DirectionZ
            br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); // int32 twin of X,Y,Z (redundant -- discarded)
            br.ReadInt32(); br.ReadInt32(); br.ReadInt32(); // fixed-point (*16384) twin of direction (redundant -- discarded)
            byte lightType = br.ReadByte();
            br.ReadByte(); br.ReadByte(); br.ReadByte();    // 0xCD filler

            // TombLib apparently leaves orphaned/deleted light slots in its own array with a fixed
            // debug-fill pattern rather than compacting them out: LightType 205 (0xCD, the classic
            // "uninitialized memory" fill byte) shows up consistently with the same garbage
            // coordinates across many rooms in real Tomb-Editor-built levels. Valid LightType is 0-4;
            // anything else is one of these placeholder slots -- skip it rather than importing a
            // bogus light at a billions-of-units-away position.
            if (lightType > 4) continue;

            var light = new LevelLight
            {
                X = (int)MathF.Round(posX), Y = (int)MathF.Round(posY), Z = (int)MathF.Round(posZ),
                // TombLib normalizes by /255.0f at TR5-write time (verified in WriteTr5); *255 here
                // recovers the original 0-255 byte scale that Prj2Exporter's shared (TR4-designed)
                // ExportLights expects (it divides ColourR/G/B by 128.0f itself).
                ColourR = (byte)Math.Clamp(MathF.Round(colR * 255f), 0, 255),
                ColourG = (byte)Math.Clamp(MathF.Round(colG * 255f), 0, 255),
                ColourB = (byte)Math.Clamp(MathF.Round(colB * 255f), 0, 255),
                LightType = lightType,
                In = inVal,
                Out = outVal,
                // Spot's InnerRange (Prj2Exporter reads l.Length) has no dedicated field in TombLib's
                // TR5 write -- only CutOff (outer) is stored. Falling back to CutOff for both is a
                // genuine format limitation (TombLib itself only round-trips the outer distance for
                // TR5 spots), not a parsing guess.
                Length = cutOff,
                CutOff = cutOff,
                // Derived by solving Prj2Exporter's existing (TR4-designed, unchanged) SetDirection/
                // ApplyDirection(-dx, dy, -dz) against TombLib's TR5 write of (-DirectionX, -DirectionY,
                // -DirectionZ), so the same call recovers TombLib's original DirectionX/Y/Z unchanged:
                // only the Y component needs negating here, X and Z pass through as read.
                DirX = dirX, DirY = -dirY, DirZ = dirZ,
            };

            // Shadow-type lights store their real intensity in the field vanilla TR5 uses as a spare
            // colour channel; every other type leaves the 0xCDCDCDCD sentinel there (meaningless).
            if (lightType == 3)
                light.Intensity = (ushort)Math.Clamp(MathF.Round((int)shadowOrSentinel / 255.0f * 8191.0f), 0, 8191);
            else
                // No dedicated intensity scalar for non-Shadow TR5 lights (brightness lives in the
                // colour floats) -- default to "full", matching Prj2Exporter's Intensity/8191.0f
                // normalization (8191 -> 1.0).
                light.Intensity = 8191;

            r.Lights.Add(light);
        }

        // --- Fog bulbs: a separate, cleanly sequential array (see note above) -- 36 bytes each,
        // per TombLib's WriteTr5: X,Y,Z (float,12) | Out/radius (float,4) | Out*Out/square_radius
        // (float,4, redundant -- discarded) | Length*65535.0f/density (float,4) | ColourR,G,B
        // (float,12, already /255.0f normalized). No inner radius or 4th colour float, unlike the
        // vanilla 40-byte struct.
        for (int i = 0; i < numFogBulbs; i++)
        {
            float fx = br.ReadSingle();
            float fy = br.ReadSingle();
            float fz = br.ReadSingle();
            float radius = br.ReadSingle();
            br.ReadSingle();                        // radius^2 (redundant -- discarded)
            float densityRaw = br.ReadSingle();
            float fr = br.ReadSingle();
            float fg = br.ReadSingle();
            float fb = br.ReadSingle();

            r.Lights.Add(new LevelLight
            {
                X = (int)MathF.Round(fx), Y = (int)MathF.Round(fy), Z = (int)MathF.Round(fz),
                ColourR = (byte)Math.Clamp(MathF.Round(fr * 255f), 0, 255),
                ColourG = (byte)Math.Clamp(MathF.Round(fg * 255f), 0, 255),
                ColourB = (byte)Math.Clamp(MathF.Round(fb * 255f), 0, 255),
                LightType = 4,
                // Prj2Exporter's FogBulb case reads In/Out as inner/outer range and Length as
                // "TR5-native storage" intensity. TombLib's bulb has one radius (no separate inner),
                // so In=0; density was scaled by *65535.0f at write time, so /65535.0f recovers it.
                In = 0,
                Out = radius,
                Length = densityRaw / 65535.0f,
            });
        }

        stream.Position = dataStart + startSdOffset;
        r.Sectors = new LevelSector[r.NumX * r.NumZ];
        for (int j = 0; j < r.NumX * r.NumZ; j++)
        {
            r.Sectors[j] = ReadSector(br);
            r.Sectors[j].HasFd = false;
        }

        r.NumPortals = br.ReadUInt16();
        r.Portals = new Portal[r.NumPortals];
        for (int j = 0; j < r.NumPortals; j++)
            r.Portals[j] = ReadPortal(br);
        stream.Seek(2, SeekOrigin.Current);        // separator

        stream.Position = dataStart + endPortalOffset;
        // tr3_room_staticmesh (20 bytes); count comes from the header (no inline uint16 count
        // prefix here, unlike TR4's read_room_static_meshes).
        stream.Seek(numStaticMeshes * 20, SeekOrigin.Current);

        stream.Position = dataStart + layerOffset;
        var layerNumVertices = new ushort[numLayers];
        var layerNumRectangles = new ushort[numLayers];
        var layerNumTriangles = new ushort[numLayers];
        for (int i = 0; i < numLayers; i++)
        {
            layerNumVertices[i] = br.ReadUInt16();
            stream.Seek(4, SeekOrigin.Current);    // _1[2]
            layerNumRectangles[i] = br.ReadUInt16();
            layerNumTriangles[i] = br.ReadUInt16();
            stream.Seek(6, SeekOrigin.Current);    // _2[3]
            stream.Seek(12 + 12, SeekOrigin.Current); // bounding_box_min/max (tr5_vertex x2)
            stream.Seek(16, SeekOrigin.Current);   // _3[4]
        }

        stream.Position = dataStart + polyOffset;
        var allRects = new List<RoomFace>();
        var allTris = new List<RoomFace>();
        ushort vertexOffset = 0;
        for (int i = 0; i < numLayers; i++)
        {
            for (int j = 0; j < layerNumRectangles[i]; j++)
                allRects.Add(ReadRoomFaceTr5(br, 4, vertexOffset));
            for (int j = 0; j < layerNumTriangles[i]; j++)
            {
                var face = ReadRoomFaceTr5(br, 3, vertexOffset);
                face.IsTriangle = true;
                allTris.Add(face);
            }
            vertexOffset += layerNumVertices[i];
        }
        r.Rectangles = allRects.ToArray();
        r.Triangles = allTris.ToArray();

        stream.Position = dataStart + verticesOffset;
        var allVerts = new List<RoomVertex>();
        for (int i = 0; i < numLayers; i++)
        {
            for (int j = 0; j < layerNumVertices[i]; j++)
            {
                float vx = br.ReadSingle();
                float vy = br.ReadSingle();
                float vz = br.ReadSingle();
                br.ReadSingle(); br.ReadSingle(); br.ReadSingle(); // normal (unused)
                uint colour = br.ReadUInt32();
                allVerts.Add(new RoomVertex
                {
                    // Same raw world-unit scale as TR1-4's int16 room-relative coords (verified:
                    // trlevel's own convert_vertices truncates these floats straight to int16,
                    // no rescale).
                    X = (short)vx, Y = (short)vy, Z = (short)vz,
                    Lighting = 0, Attributes = 0,
                    Colour = PackColour15(colour),
                });
            }
        }
        r.Vertices = allVerts.ToArray();

        stream.Position = roomEnd;
        return r;
    }

    private static RoomFace ReadRoomFaceTr5(BinaryReader br, int vertexCount, ushort vertexOffset)
    {
        // tr4_mesh_face3/tr4_mesh_face4 (used for TR5 room polys, unlike TR1-4's plain tr_face3/4):
        // vertices + texture, PLUS a trailing "effects" word that TR1-4 room faces don't have.
        var face = new RoomFace { Vertices = new ushort[vertexCount] };
        for (int i = 0; i < vertexCount; i++)
            face.Vertices[i] = (ushort)(br.ReadUInt16() + vertexOffset);
        face.Texture = br.ReadUInt16();
        br.ReadUInt16();                            // effects (unused)
        return face;
    }

    /// <summary>
    /// Down-converts an 8-bit-per-channel 0x00RRGGBB colour (TR5 room vertex colour, reinterpreted
    /// from its raw uint32) to the 5-bit-per-channel packed format RoomVertex.Colour already uses
    /// for TR3/4 vertex colours. Currently unused by the PRJ2 export pipeline either way (kept for
    /// parity/future use).
    /// </summary>
    private static ushort PackColour15(uint argb)
    {
        byte r8 = (byte)((argb >> 16) & 0xFF);
        byte g8 = (byte)((argb >> 8) & 0xFF);
        byte b8 = (byte)(argb & 0xFF);
        return (ushort)(((r8 >> 3) << 10) | ((g8 >> 3) << 5) | (b8 >> 3));
    }

    private static void ReadRoomData(BinaryReader br, LevelRoom room)
    {
        short numVertices = br.ReadInt16();
        room.Vertices = new RoomVertex[Math.Max(0, (int)numVertices)];
        for (int i = 0; i < room.Vertices.Length; i++)
        {
            room.Vertices[i] = new RoomVertex
            {
                X = br.ReadInt16(),
                Y = br.ReadInt16(),
                Z = br.ReadInt16(),
                Lighting = br.ReadInt16(),
                Attributes = br.ReadUInt16(),
                Colour = br.ReadUInt16()
            };
        }

        short numRectangles = br.ReadInt16();
        room.Rectangles = new RoomFace[Math.Max(0, (int)numRectangles)];
        for (int i = 0; i < room.Rectangles.Length; i++)
            room.Rectangles[i] = ReadRoomFace(br, 4);

        short numTriangles = br.ReadInt16();
        room.Triangles = new RoomFace[Math.Max(0, (int)numTriangles)];
        for (int i = 0; i < room.Triangles.Length; i++)
        {
            room.Triangles[i] = ReadRoomFace(br, 3);
            room.Triangles[i].IsTriangle = true;
        }

        short numSprites = br.ReadInt16();
        if (numSprites > 0)
            br.BaseStream.Seek(numSprites * 4L, SeekOrigin.Current);
    }

    private static RoomFace ReadRoomFace(BinaryReader br, int vertexCount)
    {
        var face = new RoomFace { Vertices = new ushort[vertexCount], IsTriangle = vertexCount == 3 };
        for (int i = 0; i < face.Vertices.Length; i++)
            face.Vertices[i] = br.ReadUInt16();
        face.Texture = br.ReadUInt16();
        return face;
    }

    private static ObjectTexture ReadObjectTexture(BinaryReader br, bool isTr5 = false)
    {
        var texture = new ObjectTexture
        {
            Attribute = br.ReadUInt16(),
            TileAndFlag = br.ReadUInt16(),
            NewFlags = br.ReadUInt16()
        };
        for (int i = 0; i < texture.Vertices.Length; i++)
        {
            texture.Vertices[i].X = br.ReadUInt16();
            texture.Vertices[i].Y = br.ReadUInt16();
        }
        texture.OriginalU = br.ReadUInt32();
        texture.OriginalV = br.ReadUInt32();
        texture.Width = br.ReadUInt32();
        texture.Height = br.ReadUInt32();
        // tr5_object_texture = tr4_object_texture (38 bytes, above) + a 2-byte filler
        // (verified against trlevel's trtypes.h: struct tr5_object_texture { tr4_object_texture tr4_texture; uint16_t filler; }).
        if (isTr5) br.ReadUInt16();
        return texture;
    }

    private static LevelLight ReadLight(BinaryReader br)
    {
        // tr4_room_light, 46 bytes, per TRosettaStone.
        var l = new LevelLight
        {
            X = br.ReadInt32(),
            Y = br.ReadInt32(),
            Z = br.ReadInt32(),
            ColourR = br.ReadByte(),
            ColourG = br.ReadByte(),
            ColourB = br.ReadByte(),
            LightType = br.ReadByte(),
        };
        l.Intensity = br.ReadUInt16();
        l.In = br.ReadSingle();
        l.Out = br.ReadSingle();
        l.Length = br.ReadSingle();
        l.CutOff = br.ReadSingle();
        l.DirX = br.ReadSingle();
        l.DirY = br.ReadSingle();
        l.DirZ = br.ReadSingle();
        return l;
    }

    private static Portal ReadPortal(BinaryReader br)
    {
        var p = new Portal { ToRoom = br.ReadUInt16() };
        p.Normal.X = br.ReadInt16();
        p.Normal.Y = br.ReadInt16();
        p.Normal.Z = br.ReadInt16();
        for (int i = 0; i < 4; i++)
        {
            p.Vertices[i].X = br.ReadInt16();
            p.Vertices[i].Y = br.ReadInt16();
            p.Vertices[i].Z = br.ReadInt16();
        }
        return p;
    }

    private static LevelSector ReadSector(BinaryReader br) => new()
    {
        FdIndex = br.ReadUInt16(),
        BoxIndex = br.ReadUInt16(),
        RoomBelow = br.ReadByte(),
        Floor = br.ReadSByte(),
        RoomAbove = br.ReadByte(),
        Ceiling = br.ReadSByte()
    };

    private static LevelBox ReadBox(BinaryReader br) => new()
    {
        XMin = br.ReadByte(), XMax = br.ReadByte(), ZMin = br.ReadByte(), ZMax = br.ReadByte(),
        TrueFloor = br.ReadInt16(), OverlapIndex = br.ReadInt16()
    };

    public TrProject ConvertToPrj(string filename, bool saveTga = true, bool fixFdivs = true)
    {
        uint slots = NumRooms <= 100 ? 100 : NumRooms <= 200 ? 200u : 300u;
        var p = new TrProject(NumRooms, slots);

        // Room-face texture usage determines the exported atlas: TextureBitmap (as read in Load())
        // mirrors ObjectTexture.TileAndFlag's unified room+object+bump tile numbering, but most of
        // that space is texture data used exclusively by WAD moveables/statics, which has no place
        // in the level's own room-texture file. Room faces do occasionally reference tiles that
        // would otherwise look "object-only" though (verified on real data), so we can't just keep
        // the room-tile range -- BuildRoomTextureAtlas scans actual room-face usage and keeps
        // exactly those tiles, compacted and remapped to a dense 0..N-1 sequence.
        var (roomAtlas, tileRemap) = BuildRoomTextureAtlas();

        if (saveTga && roomAtlas.PixelWidth > 0)
        {
            var tgaPath = Path.ChangeExtension(filename, ".tga");
            TgaWriter.Save(roomAtlas, tgaPath);
            var shortPath = Path.GetFileName(tgaPath);
            p.TgaFilePath = shortPath + " ";
        }

        for (int i = 0; i < Rooms.Length; i++)
        {
            var r1 = Rooms[i];
            if (r1.IsFlipRoom)
            {
                var name = $"Flipped Room{r1.OriginalRoom}";
                name.CopyTo(0, p.Rooms[i].Name, 0, Math.Min(name.Length, 79));
            }
            p.Rooms[i].X = (uint)r1.X;
            p.Rooms[i].Z = (uint)r1.Z;
            p.Rooms[i].XSize = (short)r1.NumX;
            p.Rooms[i].ZSize = (short)r1.NumZ;
            p.Rooms[i].XOffset = (ushort)((20 - r1.NumX) / 2);
            p.Rooms[i].ZOffset = (ushort)((20 - r1.NumZ) / 2);
            p.Rooms[i].XPos = (short)(r1.X / 1024);
            p.Rooms[i].ZPos = (short)(r1.Z / 1024);
            p.Rooms[i].Ambient.R = r1.Colour.R;
            p.Rooms[i].Ambient.G = r1.Colour.G;
            p.Rooms[i].Ambient.B = r1.Colour.B;
            p.Rooms[i].Ambient.A = r1.Colour.A;
            p.Rooms[i].FlipRoom = r1.AltRoom;
            p.Rooms[i].Flags1 = r1.Flags;
            if (r1.IsFlipRoom) p.Rooms[i].Flags1 |= 0x2;
            p.Rooms[i].IsFlipRoom = r1.IsFlipRoom;
            p.Rooms[i].Flags2 |= r1.AltGroup;
            p.Rooms[i].YBottom = -r1.YBottom / 256;
            p.Rooms[i].YTop = -r1.YTop / 256;
            p.Rooms[i].Blocks = new Block[r1.NumZ * r1.NumX];

            // Sectors[] are stored in the file in X-major order (idx = X_idx*NumZ + Z_idx,
            // per TRosettaStone). j = X_idx, k = Z_idx here to read them back correctly.
            for (int j = 0; j < r1.NumX; j++)
            for (int k = 0; k < r1.NumZ; k++)
            {
                int b = j * r1.NumZ + k;
                var sector = r1.Sectors[b];
                p.Rooms[i].Blocks[b] = new Block();
                var block = p.Rooms[i].Blocks[b];
                block.Id = 1;
                block.Floor = (short)-sector.Floor;
                block.Ceiling = (short)-sector.Ceiling;

                if (block.Floor != 127 && fixFdivs)
                {
                    int temp = -Math.Abs(block.Floor - (-r1.YBottom / 256));
                    for (int ii = 0; ii < 4; ii++) block.FDiv[ii] = (sbyte)temp;
                }
                if (block.Ceiling != 127 && fixFdivs)
                {
                    int temp = (-r1.YTop / 256) - block.Ceiling;
                    for (int ii = 0; ii < 4; ii++) block.CDiv[ii] = (sbyte)Math.Abs(temp);
                }

                if ((k == 0 && j == 0) || (k == r1.NumZ - 1 && j == 0) ||
                    (k == 0 && j == r1.NumX - 1) || (k == r1.NumZ - 1 && j == r1.NumX - 1))
                {
                    block.Id = 0x1E; block.Floor = 0; block.Ceiling = 20;
                }
                else if (j == 0 || j == r1.NumX - 1 || k == 0 || k == r1.NumZ - 1)
                {
                    block.Id = 0x1E;
                    block.Floor = (short)(-r1.YBottom / 256);
                    block.Ceiling = (short)(-r1.YTop / 256);
                }
                else if (sector.Floor == -127)
                {
                    block.Id = 0xE;
                    block.Floor = (short)(-r1.YBottom / 256);
                    block.Ceiling = (short)(-r1.YTop / 256);
                }
                else if (sector.RoomBelow != 255 && sector.RoomAbove != 255)
                    block.Id = 0x7;
                else if (sector.RoomBelow != 255)
                    block.Id = 0x3;
                else if (sector.RoomAbove != 255)
                    block.Id = 0x5;

                uint bx = (uint)((sector.BoxIndex & 0x7FF0) >> 4);
                if (bx != 0x7FF && bx < NumBoxes)
                {
                    if ((Boxes[bx].OverlapIndex & 0x8000) == 0x8000)
                        block.Flags1 |= 0x0020;
                }

                if (sector.HasFd && sector.FloorInfo != null)
                {
                    foreach (var fd in sector.FloorInfo)
                    {
                        if (fd.Tipo == FloorType.Trigger) continue;
                        if (fd.Tipo == FloorType.Door)
                        {
                            bool isHorizontalDoor = true;
                            foreach (var po in r1.Portals)
                            {
                                if (po.Normal.Y == 0 && po.ToRoom == fd.ToRoom)
                                {
                                    isHorizontalDoor = false;
                                    break;
                                }
                            }
                            if (isHorizontalDoor)
                            {
                                if (block.Id == 1) block.Id = 0xE;
                                if (block.Id == 0x1E)
                                {
                                    block.Floor = (short)-sector.Floor;
                                    block.Ceiling = (short)-sector.Ceiling;
                                }
                            }
                            else if (block.Id == 0x1E)
                            {
                                block.Id = 0x6;
                                block.Floor = (short)-sector.Floor;
                                block.Ceiling = (short)-sector.Ceiling;
                            }
                            continue;
                        }
                        ApplyFloorData(block, fd, r1, fixFdivs);
                    }
                }
            }

            ApplyRoomMeshTextures(p.Rooms[i], r1, ObjectTextures);
        }

        BuildPrjTextureTable(p, tileRemap);
        return p;
    }

    /// <summary>
    /// Builds a compacted room-only texture atlas: scans every room face's texture usage, collects
    /// the set of original (room+object+bump-numbered) tiles actually referenced, and copies just
    /// those 256x256 blocks -- in original pixel content, but sequential dense order -- out of the
    /// full TextureBitmap (which still holds every tile, room and object and bump alike, as read by
    /// Load()). Returns the new compact bitmap plus the original-tile -> new-index remap that
    /// ToPrjTexInfo needs to compute correct Y offsets against it.
    /// </summary>
    private (WriteableBitmap atlas, Dictionary<int, int> tileRemap) BuildRoomTextureAtlas()
    {
        // NOTE: an earlier version of this code excluded tiles >= NumRoomTextiles+NumObjTextiles,
        // on the theory that they were bump-map data (not plain diffuse colour). Direct visual
        // inspection of that raw pixel region disproved this: it contains ordinary, well-formed
        // room textures (brick, wood, roof tiles, decorative reliefs), not bump/normal data. All
        // referenced tiles are kept regardless of index range.
        var usedTiles = new SortedSet<int>();
        foreach (var room in Rooms)
        {
            foreach (var f in room.Rectangles)
            {
                int ti = f.Texture & 0x7FFF;
                if (ti >= 0 && ti < ObjectTextures.Length)
                    usedTiles.Add(ObjectTextures[ti].TileAndFlag & 0x7FFF);
            }
            foreach (var f in room.Triangles)
            {
                int ti = f.Texture & 0x7FFF;
                if (ti >= 0 && ti < ObjectTextures.Length)
                    usedTiles.Add(ObjectTextures[ti].TileAndFlag & 0x7FFF);
            }
        }

        var tileList = usedTiles.ToList();
        var remap = new Dictionary<int, int>(tileList.Count);
        for (int i = 0; i < tileList.Count; i++) remap[tileList[i]] = i;

        if (TextureBitmap == null || tileList.Count == 0)
            return (TextureBitmap ?? new WriteableBitmap(256, 256, 96, 96, PixelFormats.Bgr24, null), remap);

        int newHeight = tileList.Count * 256;
        var newBmp = new WriteableBitmap(256, newHeight, 96, 96, PixelFormats.Bgr24, null);
        int srcStride = TextureBitmap.PixelWidth * 3;
        var rowBuf = new byte[srcStride * 256];

        for (int i = 0; i < tileList.Count; i++)
        {
            int srcY = tileList[i] * 256;
            if (srcY < 0 || srcY + 256 > TextureBitmap.PixelHeight) continue; // guard malformed data
            TextureBitmap.CopyPixels(new Int32Rect(0, srcY, 256, 256), rowBuf, srcStride, 0);
            newBmp.WritePixels(new Int32Rect(0, i * 256, 256, 256), rowBuf, srcStride, 0);
        }

        return (newBmp, remap);
    }

    private void BuildPrjTextureTable(TrProject p, Dictionary<int, int> tileRemap)
    {
        // No 1024 cap: see the BlockTex.Index field comment -- this project does not round-trip
        // through the classic-PRJ on-disk 10-bit texture-index format, so its 1024-entry ceiling
        // does not apply here.
        int count = ObjectTextures.Length;
        p.NumTextures = (uint)count;
        p.Textures = new TexInfo[count];
        for (int i = 0; i < count; i++)
            p.Textures[i] = ToPrjTexInfo(ObjectTextures[i], tileRemap);
    }

    private static TexInfo ToPrjTexInfo(ObjectTexture texture, Dictionary<int, int> tileRemap)
    {
        int tile = texture.TileAndFlag & 0x7FFF;
        // Tiles never used by any room face (WAD-mesh-only textures) aren't in the remap at all --
        // they also never get written into any Block.Textures[] slot by ApplyRoomFaceTexture (slot
        // selection is driven by real room-face geometry, not by iterating this table), so this
        // TexInfo entry is simply never consulted; the 0 fallback is inert dead data, not a bug.
        int newTile = tileRemap.TryGetValue(tile, out int nt) ? nt : 0;
        int minX = texture.Vertices.Min(v => v.X >> 8);
        int maxX = texture.Vertices.Max(v => v.X >> 8);
        int minY = texture.Vertices.Min(v => v.Y >> 8);
        int maxY = texture.Vertices.Max(v => v.Y >> 8);

        minX = Math.Clamp(minX, 0, 255);
        maxX = Math.Clamp(maxX, minX, 255);
        minY = Math.Clamp(minY, 0, 255);
        maxY = Math.Clamp(maxY, minY, 255);

        return new TexInfo
        {
            X = (byte)minX,
            Y = (ushort)((newTile * 256) + minY),
            Unused = 0,
            FlipX = 0,
            Right = (byte)Math.Max(1, maxX - minX),
            FlipY = 0,
            Bottom = (byte)Math.Max(1, maxY - minY)
        };
    }

    /// <summary>
    /// Returns the inclusive [start,end] sector-index range a face's bounding box (in raw world
    /// units) covers along one axis, clamped to the room's size. Used because the TR4 compiler
    /// merges coplanar adjacent sector faces into a single larger quad/triangle -- a raw face can
    /// span several sectors, so its texture must be assigned to every sector it covers, not just
    /// the one under its centroid.
    /// </summary>
    private static (int start, int end) BlockRange(int minCoord, int maxCoord, int size)
    {
        int start = Math.Clamp(minCoord / 1024, 0, size - 1);
        int end = Math.Clamp(Math.Max(minCoord, maxCoord - 1) / 1024, start, size - 1);
        return (start, end);
    }

    private static void ApplyRoomMeshTextures(PrjRoom prjRoom, LevelRoom levelRoom, ObjectTexture[] objectTextures)
    {
        foreach (var face in levelRoom.Rectangles)
            ApplyRoomFaceTexture(prjRoom, levelRoom, face, objectTextures);
        foreach (var face in levelRoom.Triangles)
            ApplyRoomFaceTexture(prjRoom, levelRoom, face, objectTextures);
    }

    /// <summary>
    /// Classifies a raw face (already known to be flat along one horizontal axis, i.e. a wall) or
    /// a floor/ceiling face, and writes its texture into the correct one of Block.Textures[]'s 14
    /// classic-PRJ slots. Block.Textures[] indexing is X-major (b = x*NumZ + z), matching the rest
    /// of the codebase (LevelRoom.Sectors[], PrjRoom.Blocks[] population in ConvertToPrj) -- the
    /// previous Z-major indexing here (b = z*XSize + x) silently wrote to the wrong block whenever
    /// a room wasn't square (NumX != NumZ).
    /// </summary>
    private static void ApplyRoomFaceTexture(PrjRoom prjRoom, LevelRoom levelRoom, RoomFace face, ObjectTexture[] objectTextures)
    {
        int textureIndex = face.Texture & 0x7FFF;
        // BlockTex.Index is now a full int (see its field comment), so no artificial 1024-entry
        // ceiling here beyond the real bounds check against objectTextures itself.
        if (textureIndex < 0 || textureIndex >= objectTextures.Length) return;
        var vertices = face.Vertices
            .Where(v => v < levelRoom.Vertices.Length)
            .Select(v => levelRoom.Vertices[v])
            .ToArray();
        if (vertices.Length != face.Vertices.Length) return;

        int minX = vertices.Min(v => v.X), maxX = vertices.Max(v => v.X);
        int minY = vertices.Min(v => v.Y), maxY = vertices.Max(v => v.Y);
        int minZ = vertices.Min(v => v.Z), maxZ = vertices.Max(v => v.Z);
        int avgX = (int)Math.Round(vertices.Average(v => v.X));
        int avgY = (int)Math.Round(vertices.Average(v => v.Y));
        int avgZ = (int)Math.Round(vertices.Average(v => v.Z));

        const int epsilon = 8;

        if (Math.Abs(maxY - minY) <= epsilon)
        {
            // Floor/ceiling: assign to every sector the merged face's bounding box covers.
            var (bx0, bx1) = BlockRange(minX, maxX, prjRoom.XSize);
            var (bz0, bz1) = BlockRange(minZ, maxZ, prjRoom.ZSize);
            for (int bx = bx0; bx <= bx1; bx++)
            for (int bz = bz0; bz <= bz1; bz++)
            {
                int target = bx * prjRoom.ZSize + bz;
                if (target < 0 || target >= prjRoom.Blocks.Length) continue;

                int floorY = -prjRoom.Blocks[target].Floor * 256;
                int ceilingY = -prjRoom.Blocks[target].Ceiling * 256;
                int slot = Math.Abs(avgY - floorY) <= Math.Abs(avgY - ceilingY)
                    ? (face.IsTriangle ? 8 : 0)
                    : (face.IsTriangle ? 9 : 1);
                SetBlockTexture(prjRoom.Blocks[target].Textures[slot], textureIndex, face, objectTextures[textureIndex]);
            }
            return;
        }

        // Walls: the classic-PRJ format (verified against TombLib's PrjLoader.cs) only stores a
        // sector's own -X ("North") and -Z ("West") walls in its own 14-slot array; a +X/+Z-facing
        // wall is always stored as the NEIGHBOURING sector's -X/-Z wall instead (PrjLoader reads
        // slot 4/7 from sector[x-1,z]/[x,z-1] when the current sector has no -X/-Z middle face of
        // its own). Each side also has up to 3 vertical tiers: QA (a floor-height step, slot 2/5),
        // Middle (the visible body between two rooms/heights, slot 4/7), WS (a ceiling-height step,
        // slot 3/6). We never use the Floor2/Ceiling2 tier slots (10-13): raw TR4 FloorData only
        // ever encodes one floor split and one ceiling split per sector, so a second stacked tier on
        // the same side can't occur in compiled TR4 data. As with floor/ceiling, a merged wall face
        // can span several sectors along its free axis, so we assign to every sector it covers.
        if (Math.Abs(maxX - minX) <= epsilon)
        {
            int seamX = (int)Math.Round(avgX / 1024.0);
            bool isInteriorSeam = seamX > 0 && seamX < prjRoom.XSize;
            int ownX = isInteriorSeam ? seamX : Math.Clamp(seamX, 0, prjRoom.XSize - 1);
            int neighborX = isInteriorSeam ? seamX - 1 : -1;
            var (bz0, bz1) = BlockRange(minZ, maxZ, prjRoom.ZSize);
            for (int bz = bz0; bz <= bz1; bz++)
            {
                bool neighborUnreliable = !isInteriorSeam || IsBorderOrSolid(levelRoom, neighborX, bz);
                ApplyWallFace(prjRoom, ownX, bz, neighborX, bz, avgY, textureIndex, face, objectTextures,
                    qaSlot: 2, middleSlot: 4, wsSlot: 3, neighborUnreliable);
            }
            return;
        }

        if (Math.Abs(maxZ - minZ) <= epsilon)
        {
            int seamZ = (int)Math.Round(avgZ / 1024.0);
            bool isInteriorSeam = seamZ > 0 && seamZ < prjRoom.ZSize;
            int ownZ = isInteriorSeam ? seamZ : Math.Clamp(seamZ, 0, prjRoom.ZSize - 1);
            int neighborZ = isInteriorSeam ? seamZ - 1 : -1;
            var (bx0, bx1) = BlockRange(minX, maxX, prjRoom.XSize);
            for (int bx = bx0; bx <= bx1; bx++)
            {
                bool neighborUnreliable = !isInteriorSeam || IsBorderOrSolid(levelRoom, bx, neighborZ);
                ApplyWallFace(prjRoom, bx, ownZ, bx, neighborZ, avgY, textureIndex, face, objectTextures,
                    qaSlot: 5, middleSlot: 7, wsSlot: 6, neighborUnreliable);
            }
        }
    }

    /// <summary>
    /// A sector's converted Block.Floor/Ceiling is NOT trustworthy for wall-tier classification when
    /// the sector is on the room's outer ring (ConvertToPrj forces Floor/Ceiling to the room's own
    /// YBottom/YTop there, discarding the sector's real height data -- see the (j==0 || ... ) branch)
    /// or when the raw sector is the classic TR wall sentinel (Floor == -127). In both cases the
    /// neighbor's "height" carries no real geometric information, so comparing against it produces
    /// false negatives whenever it happens to numerically coincide with the real neighbor's own floor/
    /// ceiling (verified case: alexhub2 room 1, seam x=1/z=3 -- neighbor(0,3) is a border sector whose
    /// forced Floor/Ceiling happened to equal own(1,3)'s real floor/ceiling, hiding a real QA+WS pair
    /// of compiled wall faces behind an apparent "no difference" reading).
    /// </summary>
    private static bool IsBorderOrSolid(LevelRoom room, int x, int z)
    {
        if (x < 0 || z < 0 || x >= room.NumX || z >= room.NumZ) return true;
        if (x == 0 || x == room.NumX - 1 || z == 0 || z == room.NumZ - 1) return true;
        int idx = x * room.NumZ + z;
        if (idx < 0 || idx >= room.Sectors.Length) return true;
        return room.Sectors[idx].Floor == -127;
    }

    /// <summary>
    /// Returns the raw-TR-Y (positive-down, same convention as vertex.Y/avgY) height of one corner
    /// of a block's floor, honouring any Tilt/Roof/diagonal-split corner data already applied by
    /// ApplyFloorData. FloorCorner index order verified against ApplyFloorData's own Tilt/Split
    /// branches: [0]=XpZn [1]=XnZn [2]=XnZp [3]=XpZp.
    /// </summary>
    private static int GetCornerFloorY(Block block, bool xp, bool zp)
    {
        int idx = (xp, zp) switch { (true, false) => 0, (false, false) => 1, (false, true) => 2, (true, true) => 3 };
        return -(block.Floor + block.FloorCorner[idx]) * 256;
    }

    /// <summary>
    /// Same as <see cref="GetCornerFloorY"/> but for the ceiling. CeilCorner uses a DIFFERENT index
    /// order than FloorCorner (verified against ApplyFloorData's Roof/Split branches):
    /// [0]=XpZp [1]=XnZp [2]=XnZn [3]=XpZn.
    /// </summary>
    private static int GetCornerCeilY(Block block, bool xp, bool zp)
    {
        int idx = (xp, zp) switch { (true, true) => 0, (false, true) => 1, (false, false) => 2, (true, false) => 3 };
        return -(block.Ceiling + block.CeilCorner[idx]) * 256;
    }

    /// <summary>
    /// Writes a wall face's texture into the "owning" block's QA/Middle/WS slot. (ownX,ownZ) is the
    /// block whose own -X/-Z side this wall represents (always the higher-index side of the seam,
    /// per PrjLoader's storage convention); (neighborX,neighborZ) is the block across the seam, or
    /// an out-of-range coordinate if this is a room-boundary wall with no neighbour.
    /// Tier classification (QA/Middle/WS) replicates TombLib's own per-corner wall rule (verified
    /// against TombLib/LevelData/SectorGeometry/RoomExtensionMethods.cs -- GetPositiveXWallData/
    /// GetNegativeXWallData/etc. -- and SectorWallData.cs's zero-height-face skip): a QA (floor-step)
    /// face exists on "own" only where own's floor is higher than the neighbor's AT A SHARED CORNER,
    /// evaluated per corner rather than as a single flat scalar -- the previous flat-scalar
    /// comparison (~74% accuracy per validation notes) missed exactly the sloped/tilted sectors
    /// where the two shared corners disagree on which side is higher. Mirrored for WS (ceiling-step)
    /// using "own ceiling lower than neighbor's".
    /// </summary>
    private static void ApplyWallFace(PrjRoom prjRoom, int ownX, int ownZ, int neighborX, int neighborZ, int avgY,
        int textureIndex, RoomFace face, ObjectTexture[] objectTextures, int qaSlot, int middleSlot, int wsSlot,
        bool neighborUnreliable = false)
    {
        int ownTarget = ownX * prjRoom.ZSize + ownZ;
        if (ownTarget < 0 || ownTarget >= prjRoom.Blocks.Length) return;

        int slot = middleSlot; // default: full-height wall (room edge, or no floor/ceiling mismatch found)
        bool hasNeighbor = neighborX >= 0 && neighborZ >= 0 && neighborX < prjRoom.XSize && neighborZ < prjRoom.ZSize;

        if (hasNeighbor && neighborUnreliable)
        {
            var ownBlock0 = prjRoom.Blocks[ownTarget];
            bool isXDirection0 = qaSlot == 2;
            int ofA, ofB, ocA, ocB;
            if (isXDirection0)
            {
                ofA = GetCornerFloorY(ownBlock0, xp: false, zp: false);
                ofB = GetCornerFloorY(ownBlock0, xp: false, zp: true);
                ocA = GetCornerCeilY(ownBlock0, xp: false, zp: false);
                ocB = GetCornerCeilY(ownBlock0, xp: false, zp: true);
            }
            else
            {
                ofA = GetCornerFloorY(ownBlock0, xp: false, zp: false);
                ofB = GetCornerFloorY(ownBlock0, xp: true, zp: false);
                ocA = GetCornerCeilY(ownBlock0, xp: false, zp: false);
                ocB = GetCornerCeilY(ownBlock0, xp: true, zp: false);
            }
            int floorBottom = Math.Max(ofA, ofB);
            int ceilTop = Math.Min(ocA, ocB);
            int span = floorBottom - ceilTop;
            if (span > 0)
            {
                int sixth = span / 6;
                if (avgY >= floorBottom - sixth) slot = qaSlot;
                else if (avgY <= ceilTop + sixth) slot = wsSlot;
            }
        }
        else if (hasNeighbor)
        {
            int neighborTarget = neighborX * prjRoom.ZSize + neighborZ;
            if (neighborTarget >= 0 && neighborTarget < prjRoom.Blocks.Length)
            {
                var ownBlock = prjRoom.Blocks[ownTarget];
                var neighborBlock = prjRoom.Blocks[neighborTarget];

                // Seam corners shared between own and neighbor, in own's local corner naming.
                // X-direction seam (own is the higher-X sector, its -X side faces neighbor's +X
                // side): shared corners are own's XnZn/XnZp == neighbor's XpZn/XpZp.
                // Z-direction seam (own is the higher-Z sector, its -Z side faces neighbor's +Z
                // side): shared corners are own's XnZn/XpZn == neighbor's XnZp/XpZp.
                bool isXDirection = qaSlot == 2;

                int ownFloorA, ownFloorB, neighFloorA, neighFloorB;
                int ownCeilA, ownCeilB, neighCeilA, neighCeilB;
                if (isXDirection)
                {
                    ownFloorA = GetCornerFloorY(ownBlock, xp: false, zp: false);   // own XnZn
                    ownFloorB = GetCornerFloorY(ownBlock, xp: false, zp: true);    // own XnZp
                    neighFloorA = GetCornerFloorY(neighborBlock, xp: true, zp: false); // neighbor XpZn
                    neighFloorB = GetCornerFloorY(neighborBlock, xp: true, zp: true);  // neighbor XpZp

                    ownCeilA = GetCornerCeilY(ownBlock, xp: false, zp: false);
                    ownCeilB = GetCornerCeilY(ownBlock, xp: false, zp: true);
                    neighCeilA = GetCornerCeilY(neighborBlock, xp: true, zp: false);
                    neighCeilB = GetCornerCeilY(neighborBlock, xp: true, zp: true);
                }
                else
                {
                    ownFloorA = GetCornerFloorY(ownBlock, xp: false, zp: false);   // own XnZn
                    ownFloorB = GetCornerFloorY(ownBlock, xp: true, zp: false);    // own XpZn
                    neighFloorA = GetCornerFloorY(neighborBlock, xp: false, zp: true); // neighbor XnZp
                    neighFloorB = GetCornerFloorY(neighborBlock, xp: true, zp: true);  // neighbor XpZp

                    ownCeilA = GetCornerCeilY(ownBlock, xp: false, zp: false);
                    ownCeilB = GetCornerCeilY(ownBlock, xp: true, zp: false);
                    neighCeilA = GetCornerCeilY(neighborBlock, xp: false, zp: true);
                    neighCeilB = GetCornerCeilY(neighborBlock, xp: true, zp: true);
                }

                // QA exists on "own" only where own's floor is higher (smaller raw-Y) than the
                // neighbor's -- mirrors SectorWallData's "yStartA <= yEndA && yStartB <= yEndB =>
                // skip" zero/negative-height check. Evaluated PER CORNER: a corner where own is NOT
                // higher contributes no real geometry there, so it must not widen the Y-band (doing
                // so was the source of the Middle-tier false positives in the previous attempt).
                bool qaValidA = ownFloorA < neighFloorA;
                bool qaValidB = ownFloorB < neighFloorB;
                bool hasQaOnOwn = qaValidA || qaValidB;

                bool wsValidA = ownCeilA > neighCeilA;
                bool wsValidB = ownCeilB > neighCeilB;
                bool hasWsOnOwn = wsValidA || wsValidB;

                if (hasQaOnOwn)
                {
                    int loF = int.MaxValue, hiF = int.MinValue;
                    if (qaValidA) { loF = Math.Min(loF, ownFloorA); hiF = Math.Max(hiF, neighFloorA); }
                    if (qaValidB) { loF = Math.Min(loF, ownFloorB); hiF = Math.Max(hiF, neighFloorB); }
                    if (avgY >= loF && avgY <= hiF) slot = qaSlot;
                }
                if (slot == middleSlot && hasWsOnOwn)
                {
                    int loC = int.MaxValue, hiC = int.MinValue;
                    if (wsValidA) { loC = Math.Min(loC, neighCeilA); hiC = Math.Max(hiC, ownCeilA); }
                    if (wsValidB) { loC = Math.Min(loC, neighCeilB); hiC = Math.Max(hiC, ownCeilB); }
                    if (avgY >= loC && avgY <= hiC) slot = wsSlot;
                }
            }
        }

        SetBlockTexture(prjRoom.Blocks[ownTarget].Textures[slot], textureIndex, face, objectTextures[textureIndex]);
    }

    private static void SetBlockTexture(BlockTex blockTex, int textureIndex, RoomFace face, ObjectTexture texture)
    {
        blockTex.Tipo = 0x0007;
        // No more 8/10-bit packing here -- see the BlockTex.Index field comment. Flags1 no longer
        // carries any part of the index; its low bits are simply left at 0 (unused) below, with
        // 0x04/0x08/0x80 still used for DoubleSided/BlendMode/flip exactly as before.
        blockTex.Index = textureIndex;
        if ((face.Texture & 0x8000) != 0)
            blockTex.Flags1 |= 0x04;
        // Attribute: 0 = opaque, 1 = alpha-tested, 2 = additive (TRosettaStone tr4_object_texture).
        // Alpha-tested (1) needs no explicit flag here: TombLib's own compiler auto-detects the need
        // for alpha testing by scanning the actual texture pixels (TexInfoManager.SortOutAlpha)
        // regardless of any stored attribute -- only additive needs to be signalled explicitly here,
        // matching PrjLoader's own 0x08 bit convention (BlendMode = Flags1 & 0x08 ? Additive : Normal).
        if (texture.Attribute == 2)
            blockTex.Flags1 |= 0x08;
        blockTex.Rotation = 0;
        blockTex.Triangle = 0;
        blockTex.Filler = 0;
    }

    private static void ApplyFloorData(Block block, ParsedFloorData fd, LevelRoom r1, bool fixFdivs)
    {
        if (fd.Tipo == FloorType.Beetle) { block.Flags2 |= 0x0040; return; }
        if (fd.Tipo == FloorType.Trigtrig) { block.Flags2 |= 0x0020; return; }
        if (fd.Tipo == FloorType.Climb)
        {
            if (fd.N) block.Flags1 |= 0x0200;
            if (fd.S) block.Flags1 |= 0x0080;
            if (fd.W) block.Flags1 |= 0x0100;
            if (fd.E) block.Flags1 |= 0x0040;
            return;
        }
        if (fd.Tipo == FloorType.Monkey) { block.Flags1 |= 0x4000; return; }
        if (fd.Tipo == FloorType.Lava) { block.Flags1 |= 0x0010; return; }

        if (fd.Tipo == FloorType.Tilt)
        {
            // Function 0x02 (Floor Slant), per TRosettaStone, verified against TombLib's compiler
            // (Compilers/FloorData.cs quad-slope branch): relative to XnZn=0, XpZn=AddZ, XnZp=AddX,
            // XpZp=AddX+AddZ. FloorCorner convention (verified against the triangulation path) is
            // max-relative: FloorCorner[i] = max(rel) - rel[i], always >= 0.
            // Block.FloorCorner index mapping: [0]=XpZn [1]=XnZn [2]=XnZp [3]=XpZp.
            int relXnZn = 0, relXpZn = fd.AddZ, relXnZp = fd.AddX, relXpZp = fd.AddX + fd.AddZ;
            int maxRel = Math.Max(Math.Max(relXnZn, relXpZn), Math.Max(relXnZp, relXpZp));
            block.FloorCorner[0] = (sbyte)(maxRel - relXpZn);
            block.FloorCorner[1] = (sbyte)(maxRel - relXnZn);
            block.FloorCorner[2] = (sbyte)(maxRel - relXnZp);
            block.FloorCorner[3] = (sbyte)(maxRel - relXpZp);
            if (fixFdivs)
            {
                int v = -Math.Abs(block.Floor - (-r1.YBottom / 256));
                for (int ii = 0; ii < 4; ii++) block.FDiv[ii] = (sbyte)v;
            }
            return;
        }

        if (fd.Tipo == FloorType.Roof)
        {
            // Function 0x03 (Ceiling Slant), per TRosettaStone, verified against TombLib's compiler:
            // relative to XnZn=0, XpZn=AddZ, XnZp=-AddX, XpZp=AddZ-AddX (X-difference sign flips for
            // ceiling vs floor). CeilCorner convention (verified against the triangulation path) is
            // min-relative: CeilCorner[i] = rel[i] - min(rel), always >= 0.
            // Block.CeilCorner index mapping: [0]=XpZp [1]=XnZp [2]=XnZn [3]=XpZn.
            int relXnZn = 0, relXpZn = fd.AddZ, relXnZp = -fd.AddX, relXpZp = fd.AddZ - fd.AddX;
            int minRel = Math.Min(Math.Min(relXnZn, relXpZn), Math.Min(relXnZp, relXpZp));
            block.CeilCorner[0] = (sbyte)(relXpZp - minRel);
            block.CeilCorner[1] = (sbyte)(relXnZp - minRel);
            block.CeilCorner[2] = (sbyte)(relXnZn - minRel);
            block.CeilCorner[3] = (sbyte)(relXpZn - minRel);
            if (fixFdivs)
            {
                int v = Math.Abs((-r1.YTop / 256) - block.Ceiling);
                for (int ii = 0; ii < 4; ii++) block.CDiv[ii] = (sbyte)v;
            }
            return;
        }

        if (fd.Tipo is FloorType.Split1 or FloorType.Split2 or >= FloorType.Nocol1 and <= FloorType.Nocol4)
        {
            ApplyFloorSplit(block, fd);
            return;
        }

        if (fd.Tipo is FloorType.Split3 or FloorType.Split4 or >= FloorType.Nocol5 and <= FloorType.Nocol8)
        {
            ApplyCeilingSplit(block, fd);
        }
    }

    private static void ApplyFloorSplit(Block block, ParsedFloorData fd)
    {
        // Triangulation formula per TRosettaStone: H = Hfloor + (max(dC) - dCn). fd.Corners parsing
        // order already matches FloorCorner's index convention 1:1 ([0]=XpZn,[1]=XnZn,[2]=XnZp,[3]=XpZp).
        // block.Floor is NOT lowered: it already represents Hfloor directly, like the fixed ceiling case.
        int[] a = { fd.Corners[0], fd.Corners[1], fd.Corners[2], fd.Corners[3] };
        int maxCorner = a.Max();
        block.FloorCorner[0] = (sbyte)(maxCorner - a[0]);
        block.FloorCorner[1] = (sbyte)(maxCorner - a[1]);
        block.FloorCorner[2] = (sbyte)(maxCorner - a[2]);
        block.FloorCorner[3] = (sbyte)(maxCorner - a[3]);
        // Split1(0x07)/Nocol1(0x0B)/Nocol2(0x0C): NW-SE diagonal (XnZp-XpZn) -> SplitDirectionIsXEqualsZ=false.
        // Split2(0x08)/Nocol3(0x0D)/Nocol4(0x0E): NE-SW diagonal (XnZn-XpZp) -> SplitDirectionIsXEqualsZ=true.
        block.FloorSplitXEqualsZ = fd.Tipo is FloorType.Split2 or FloorType.Nocol3 or FloorType.Nocol4;

        if (fd.Tipo is FloorType.Split2 or FloorType.Nocol3 or FloorType.Nocol4)
        {
            if (a[1] > Math.Max(a[0], a[2]) || a[3] > Math.Max(a[0], a[2]) ||
                a[1] < Math.Min(a[0], a[2]) || a[3] < Math.Min(a[0], a[2]))
                block.Flags3 |= 0x1;
            if (fd.Tipo == FloorType.Nocol3) block.Flags2 |= 0x4;
            if (fd.Tipo == FloorType.Nocol4) block.Flags2 |= 0x2;
        }
        if (fd.Tipo is FloorType.Split1 or FloorType.Nocol1 or FloorType.Nocol2)
        {
            if (a[0] > Math.Max(a[1], a[3]) || a[2] > Math.Max(a[1], a[3]) ||
                a[0] < Math.Min(a[1], a[3]) || a[2] < Math.Min(a[1], a[3]))
                block.Flags3 |= 0x1;
            if (fd.Tipo == FloorType.Nocol1) block.Flags2 |= 0x4;
            if (fd.Tipo == FloorType.Nocol2) block.Flags2 |= 0x2;
        }
        if (fd.Tipo is >= FloorType.Nocol1 and <= FloorType.Nocol4)
        {
            if (block.Id == 0x3) block.Id = 1;
            if (block.Id == 0x7) block.Id = 5;
        }
    }

    private static void ApplyCeilingSplit(Block block, ParsedFloorData fd)
    {
        // Triangulation formula per TRosettaStone: H = Hbase + (max(dC) - dCn). Verified against
        // trview's parse_triangulation/Sector.cpp: for the ceiling, the SAME raw c00/c01/c10/c11
        // bit fields (same fd.Corners[] parse as floor) map to corners with Z MIRRORED but X
        // unchanged relative to the floor interpretation -- i.e. fd.Corners[i] keeps the same
        // index but means a different corner: [0]=XpZp(NE) [1]=XnZp(NW) [2]=XnZn(SW) [3]=XpZn(SE).
        // CeilCorner's own target order is [0]=XpZp [1]=XnZp [2]=XnZn [3]=XpZn -- so, unlike a
        // naive full reversal, this is actually a direct 1:1 index mapping, not reversed.
        // block.Ceiling is NOT adjusted: it already represents the reference height directly.
        int maxCorner = fd.Corners.Max();
        block.CeilCorner[0] = (sbyte)(maxCorner - fd.Corners[0]); // XpZp
        block.CeilCorner[1] = (sbyte)(maxCorner - fd.Corners[1]); // XnZp
        block.CeilCorner[2] = (sbyte)(maxCorner - fd.Corners[2]); // XnZn
        block.CeilCorner[3] = (sbyte)(maxCorner - fd.Corners[3]); // XpZn
        // Split3(0x09)/Nocol5(0x0F)/Nocol6(0x10): "NW" ceiling diagonal -> SplitDirectionIsXEqualsZ=false.
        // Split4(0x0A)/Nocol7(0x11)/Nocol8(0x12): "NE" ceiling diagonal -> SplitDirectionIsXEqualsZ=true.
        block.CeilingSplitXEqualsZ = fd.Tipo is FloorType.Split4 or FloorType.Nocol7 or FloorType.Nocol8;
        if (fd.Tipo is FloorType.Nocol5 or FloorType.Nocol7) block.Flags2 |= 0x10;
        if (fd.Tipo is FloorType.Nocol6 or FloorType.Nocol8) block.Flags2 |= 0x8;
        if (fd.Tipo is >= FloorType.Nocol5 and <= FloorType.Nocol8)
        {
            if (block.Id == 0x5) block.Id = 1;
            if (block.Id == 0x7) block.Id = 3;
        }
    }

    public void MakeDoors(TrProject p, bool tr2PrjLinks)
    {
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = Rooms[i];
            if (r.NumPortals == 0) continue;
            var portalArray = new List<Portal>();
            for (int j = 0; j < r.NumPortals - 1; j++)
            {
                bool found = false;
                for (int k = j + 1; k < r.NumPortals && !found; k++)
                {
                    if (r.Portals[j].PortalEquals(r.Portals[k]))
                        found = true;
                }
                if (!found) portalArray.Add(r.Portals[j]);
            }
            portalArray.Add(r.Portals[r.NumPortals - 1]);
            if (portalArray.Count < r.Portals.Length)
            {
                r.NumPortals = (ushort)portalArray.Count;
                r.Portals = portalArray.ToArray();
                Rooms[i] = r;
            }
        }

        int doorCount = 0;
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = Rooms[i];
            p.Rooms[i].NumDoors = r.NumPortals;
            p.Rooms[i].Doors = new Door[r.NumPortals];
            p.Rooms[i].DoorThingIndex = new ushort[r.NumPortals];

            for (int j = 0; j < r.Portals.Length; j++)
            {
                var portal = r.Portals[j];
                int minx = portal.Vertices[0].X, maxx = minx;
                int minz = portal.Vertices[0].Z, maxz = minz;
                for (int k = 1; k < 4; k++)
                {
                    if (portal.Vertices[k].X < minx) minx = portal.Vertices[k].X;
                    if (portal.Vertices[k].X > maxx) maxx = portal.Vertices[k].X;
                    if (portal.Vertices[k].Z < minz) minz = portal.Vertices[k].Z;
                    if (portal.Vertices[k].Z > maxz) maxz = portal.Vertices[k].Z;
                }

                var d = new Door { Room = (ushort)i, YClickAboveFloor = 0, Filler = new ushort[13] };
                d.Filler[0] = portal.ToRoom;
                p.Rooms[i].DoorThingIndex[j] = (ushort)doorCount;

                // NOTE: door.XPos/XSize always come from the portal's X-vertex extent (minx/maxx),
                // and door.ZPos/ZSize always come from its Z-vertex extent (minz/maxz), for every
                // direction including walls. Prj2Exporter builds a RectangleInt2(x0,z0,x1,z1) directly
                // from these fields with no per-direction rotation, matching TombLib's own PrjLoader
                // (GetArea is called identically regardless of portal direction). The previous code
                // crossed the axes for all 6 directions (X-derived data stored in the Z field and vice
                // versa), which would rotate every portal's rectangle 90 degrees from where it belongs.
                if (portal.Normal.X == 1)
                { d.Id = 2; d.XPos = (short)(minx / 1024); d.XSize = 1; d.ZPos = (short)(minz / 1024); d.ZSize = (short)((maxz - minz) / 1024); }
                if (portal.Normal.X == -1)
                { d.Id = 0xFFFD; d.XPos = (short)(minx / 1024); d.XSize = 1; d.ZPos = (short)(minz / 1024); d.ZSize = (short)((maxz - minz) / 1024); }
                if (portal.Normal.Z == 1)
                { d.Id = 1; d.ZPos = (short)(minz / 1024); d.ZSize = 1; d.XPos = (short)(minx / 1024); d.XSize = (short)((maxx - minx) / 1024); }
                if (portal.Normal.Z == -1)
                { d.Id = 0xFFFE; d.ZPos = (short)(minz / 1024); d.ZSize = 1; d.XPos = (short)(minx / 1024); d.XSize = (short)((maxx - minx) / 1024); }
                if (portal.Normal.Y == -1)
                { d.Id = 4; d.XPos = (short)(minx / 1024); d.XSize = (short)((maxx - minx) / 1024); d.ZPos = (short)(minz / 1024); d.ZSize = (short)((maxz - minz) / 1024); }
                if (portal.Normal.Y == 1)
                { d.Id = 0xFFFB; d.XPos = (short)(minx / 1024); d.XSize = (short)((maxx - minx) / 1024); d.ZPos = (short)(minz / 1024); d.ZSize = (short)((maxz - minz) / 1024); }

                if (!r.IsFlipRoom) doorCount++;
                p.Rooms[i].Doors[j] = d;
            }
        }

        p.NumThings = (uint)doorCount;

        for (int i = 0; i < Rooms.Length; i++)
        for (int j = 0; j < Rooms[i].Portals.Length; j++)
        {
            var d = p.Rooms[i].Doors[j];
            int room = d.Filler[0];
            for (int k = 0; k < Rooms[room].Portals.Length; k++)
            {
                if (Rooms[room].Portals[k].Adjoins(Rooms[i].Portals[j], Rooms[room].Z, Rooms[room].X, Rooms[i].Z, Rooms[i].X))
                {
                    p.Rooms[i].Doors[j].Slot = p.Rooms[room].DoorThingIndex[k];
                    break;
                }
            }
        }

        for (int i = 0; i < Rooms.Length; i++)
        {
            if (!Rooms[i].IsFlipRoom) continue;
            var r = Rooms[i];
            // Assumes a flip room mirrors its original room's door/portal count exactly, which TR4
            // levels observed so far always satisfied. Some TR5 levels violate it (rooms with a
            // handful more/fewer portals on one side of the flip pair) -- guard rather than crash;
            // any door beyond the shorter side's count is left at its ConvertToPrj default.
            int pairedDoorCount = Math.Min(p.Rooms[i].Doors.Length, p.Rooms[r.OriginalRoom].Doors.Length);
            for (int j = 0; j < pairedDoorCount; j++)
            {
                var d = p.Rooms[r.OriginalRoom].Doors[j];
                p.Rooms[i].DoorThingIndex[j] = p.Rooms[r.OriginalRoom].DoorThingIndex[j];
                p.Rooms[i].Doors[j].Slot = d.Slot;
                p.Rooms[i].Doors[j].Room = d.Room;
            }
        }

        for (int i = 0; i < p.Rooms.Length; i++)
        {
            if (p.Rooms[i].Id == 1) break;
            for (int j = 0; j < p.Rooms[i].Doors.Length; j++)
            {
                var d = p.Rooms[i].Doors[j];
                if (d.Id is 4 or 0xFFFB) continue;
                var bloks = d.GetBlockIndices(p.Rooms[i].ZSize);
                Door? dd = null;
                for (int k = 0; k < p.Rooms[d.Filler[0]].Doors.Length; k++)
                {
                    if (d.Slot == p.Rooms[d.Filler[0]].DoorThingIndex[k])
                    { dd = p.Rooms[d.Filler[0]].Doors[k]; break; }
                }
                if (dd == null) continue;
                var bloks2 = dd.GetAdjacentBlockIndices(p.Rooms[dd.Room].ZSize);
                if (bloks.Length != bloks2.Length) continue;
                int rm = p.Rooms[i].IsFlipRoom && p.Rooms[dd.Room].FlipRoom > -1
                    ? p.Rooms[dd.Room].FlipRoom : dd.Room;
                for (int k = 0; k < bloks.Length; k++)
                {
                    if (bloks[k] >= p.Rooms[i].Blocks.Length || bloks2[k] >= p.Rooms[rm].Blocks.Length)
                        continue; // out-of-range door geometry (bad/edge-case source data); skip rather than crash
                    p.Rooms[i].Blocks[bloks[k]].Floor = p.Rooms[rm].Blocks[bloks2[k]].Floor;
                    p.Rooms[i].Blocks[bloks[k]].Ceiling = p.Rooms[rm].Blocks[bloks2[k]].Ceiling;
                }
            }
        }

        for (int i = 0; i < p.Rooms.Length; i++)
        {
            if (p.Rooms[i].Id == 1) break;
            foreach (var d in p.Rooms[i].Doors)
                d.MarkDoorBlocks(p.Rooms[i]);
        }

        p.Rooms[0].Link = 0xFFFF;

        if (tr2PrjLinks)
        {
            int previousRoom = -1, firstRoom = 0;
            for (int i = 0; i < p.Rooms.Length; i++)
            {
                if (p.Rooms[i].Id == 1) break;
                p.Rooms[i].Link = 0;
                if (p.Rooms[i].NumDoors == 0) p.Rooms[i].Link = (ushort)i;
                else
                {
                    if (previousRoom >= 0) p.Rooms[previousRoom].Link = (ushort)i;
                    else firstRoom = i;
                    previousRoom = i;
                }
            }
            if (previousRoom >= 0) p.Rooms[previousRoom].Link = (ushort)firstRoom;
        }
    }
}
