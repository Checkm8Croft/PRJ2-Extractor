using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PRJ2_Extractor.Models;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Tomb Raider 2 (.tr2, version 0x2D) and Tomb Raider 3 (.tr2, version 0xFF080038 / 0xFF180038) reader.
/// Layout source of truth: TombLib Compilers/Tr2.cs, Tr3.cs and Structs.cs (WriteTr2 / WriteTr3), cross-checked with
/// trview (trlevel/Level_tr2_pc.ixx, Level_tr3_pc.ixx). It fills the shared <see cref="TrLevel"/> model.
/// </summary>
public partial class TrLevel
{
    private const uint Tr2Version = 0x0000002D;
    private const uint Tr3VersionA = 0xFF080038;
    private const uint Tr3VersionB = 0xFF180038;

    /// <summary>True for a Tomb Raider 2 level.</summary>
    public bool IsTr2;

    /// <summary>True for a Tomb Raider 3 level.</summary>
    public bool IsTr3;

    private static bool IsTr23Version(uint v) => v == Tr2Version || v == Tr3VersionA || v == Tr3VersionB;

    private byte LoadTr23(string filename, IProgress<int>? progress)
    {
        var bytes = File.ReadAllBytes(filename);
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms);
        long size = ms.Length;
        void Report() => progress?.Report((int)Math.Min(99, ms.Position * 100 / size));
        void Skip(long bytesToSkip) => ms.Seek(bytesToSkip, SeekOrigin.Current);

        FileVersion = br.ReadUInt32();
        bool tr3 = FileVersion != Tr2Version;
        IsTr2 = !tr3;
        IsTr3 = tr3;

        // --- Palettes (8-bit 6-bit-RGB + a 16-bit one) are not needed: the 16-bit textiles are used directly. ---
        Skip(768 + 1024);
        uint numTextiles = br.ReadUInt32();
        if (numTextiles == 0 || numTextiles > 512 || ms.Position + numTextiles * (65536L + 131072L) > size) return 4;
        Skip(numTextiles * 65536L);                  // 8-bit tiles
        var tex16 = br.ReadBytes((int)numTextiles * 131072);
        NumRoomTextiles = (ushort)numTextiles;       // single textile pool, no room / object / bump split
        NumObjTextiles = 0;
        NumBumpTextiles = 0;
        br.ReadUInt32();                             // filler
        Report();

        // --- Rooms ---
        NumRooms = br.ReadUInt16();
        Rooms = new LevelRoom[NumRooms];
        for (int i = 0; i < NumRooms; i++)
        {
            var r = new LevelRoom
            {
                X = br.ReadInt32(),
                Z = br.ReadInt32(),
                YBottom = br.ReadInt32(),
                YTop = br.ReadInt32()
            };
            uint dataWords = br.ReadUInt32();
            long dataEnd = ms.Position + dataWords * 2L;
            if (dataEnd > size) return 4;
            if (dataWords > 0) ReadRoomDataTr1To3(br, r, extendedVertex: true);
            ms.Position = dataEnd;

            r.NumPortals = br.ReadUInt16();
            r.Portals = new Portal[r.NumPortals];
            for (int j = 0; j < r.NumPortals; j++)
                r.Portals[j] = ReadPortal(br);

            r.NumZ = br.ReadUInt16();
            r.NumX = br.ReadUInt16();
            if (r.NumX * r.NumZ > 4096) return 4;
            r.Sectors = new LevelSector[r.NumX * r.NumZ];
            for (int j = 0; j < r.Sectors.Length; j++)
            {
                var s = ReadSector(br);
                s.HasFd = false;
                // TR2: plain box number (0xFFFF = none). TR3 already uses the TR4 encoding (box << 4 | material).
                if (!tr3)
                    s.BoxIndex = s.BoxIndex == 0xFFFF ? (ushort)0x7FF0 : (ushort)((s.BoxIndex & 0x7FF) << 4);
                r.Sectors[j] = s;
            }

            // TR2: intensity1, intensity2, light mode. TR3: intensity, light mode. Same inverted 13-bit greyscale as TR1.
            ApplyAmbient13Bit(r, br.ReadInt16());
            if (!tr3) br.ReadInt16();                // intensity2
            br.ReadInt16();                          // light mode

            ushort lightCount = br.ReadUInt16();
            for (int li = 0; li < lightCount; li++)
                r.Lights.Add(tr3 ? ReadLightTr3(br) : ReadLightTr2(br));

            ushort staticCount = br.ReadUInt16();
            for (int si = 0; si < staticCount; si++)
            {
                // tr_room_staticmesh (TR2 / TR3, 20 bytes): X, Y, Z int32 | rotation | intensity1 | intensity2 | mesh id
                var st = new LevelStatic
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    Rotation = br.ReadUInt16(),
                };
                ushort intensity1 = br.ReadUInt16();
                br.ReadUInt16();                     // intensity2 (TR2 only)
                // TR3 intensity1 is a 15-bit RGB colour (same packing as TR4); TR2 is a 13-bit greyscale with no TombLib equivalent.
                st.Colour = tr3 ? intensity1 : (ushort)0x4210;
                st.MeshId = br.ReadUInt16();
                r.Statics.Add(st);
            }

            r.AltRoom = br.ReadInt16();
            r.Flags = (ushort)(br.ReadUInt16() & 0x0001); // bit 0 = water
            if (tr3)
            {
                r.WaterScheme = br.ReadByte();
                r.Reverb = br.ReadByte();
                br.ReadByte();                       // filler
            }
            Rooms[i] = r;
            Report();
        }

        // --- FloorData ---
        NumFloorData = br.ReadUInt32();
        if (NumFloorData > 0x1000000) return 4;
        FloorData = new ushort[NumFloorData];
        for (int i = 0; i < NumFloorData; i++)
            FloorData[i] = br.ReadUInt16();

        // --- Meshes, animations and models: skipped (Lara Extractor handles those). ---
        Skip(br.ReadUInt32() * 2L);                  // mesh data (words)
        Skip(br.ReadUInt32() * 4L);                  // mesh pointers
        NumAnimations = br.ReadUInt32();
        Skip(NumAnimations * 32L);
        NumStateChanges = br.ReadUInt32();
        Skip(NumStateChanges * 6L);
        NumAnimDispatches = br.ReadUInt32();
        Skip(NumAnimDispatches * 8L);
        NumAnimCommands = br.ReadUInt32();
        Skip(NumAnimCommands * 2L);
        NumMeshtrees = br.ReadUInt32();
        Skip(NumMeshtrees * 4L);                     // mesh trees (dwords)
        SizeKeyframes = br.ReadUInt32();
        Skip(SizeKeyframes * 2L);                    // frames (words)
        NumMoveables = br.ReadUInt32();
        Skip(NumMoveables * 18L);
        NumStatics = br.ReadUInt32();
        Skip(NumStatics * 32L);
        Report();

        // TR2 stores the object textures here, TR3 only after the animated textures.
        bool ReadObjectTextures()
        {
            uint n = br.ReadUInt32();
            if (n > 0x100000 || ms.Position + n * 20L > size) return false;
            ObjectTextures = new ObjectTexture[n];
            for (int i = 0; i < ObjectTextures.Length; i++)
                ObjectTextures[i] = ReadObjectTextureTr1(br);
            return true;
        }
        if (!tr3 && !ReadObjectTextures()) return 4;

        Skip(br.ReadUInt32() * 16L);                 // sprite textures
        Skip(br.ReadUInt32() * 8L);                  // sprite sequences

        // --- Cameras, sound sources, boxes ---
        uint count = br.ReadUInt32();
        Cameras = new List<LevelCamera>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
            Cameras.Add(new LevelCamera
            {
                X = br.ReadInt32(), Y = br.ReadInt32(), Z = br.ReadInt32(),
                Room = br.ReadInt16(), Flags = br.ReadUInt16(),
            });
        FlybyCameras = [];

        count = br.ReadUInt32();
        SoundSources = new List<LevelSoundSource>((int)Math.Min(count, 4096));
        for (uint i = 0; i < count; i++)
            SoundSources.Add(new LevelSoundSource
            {
                X = br.ReadInt32(), Y = br.ReadInt32(), Z = br.ReadInt32(),
                SoundId = br.ReadUInt16(), Flags = br.ReadUInt16(),
            });

        NumBoxes = br.ReadUInt32();
        if (NumBoxes > 0x10000 || ms.Position + NumBoxes * 8L > size) return 4;
        Boxes = new LevelBox[NumBoxes];
        for (int i = 0; i < NumBoxes; i++)
        {
            // TR2 / TR3 box: Zmin, Zmax, Xmin, Xmax (bytes, sector units) | true floor int16 | overlap index int16
            byte zMin = br.ReadByte(), zMax = br.ReadByte(), xMin = br.ReadByte(), xMax = br.ReadByte();
            Boxes[i] = new LevelBox
            {
                ZMin = zMin, ZMax = zMax, XMin = xMin, XMax = xMax,
                TrueFloor = br.ReadInt16(), OverlapIndex = br.ReadInt16()
            };
        }

        Skip(br.ReadUInt32() * 2L);                  // overlaps
        Skip(NumBoxes * 20L);                        // zones (10 x int16 per box)
        Skip(br.ReadUInt32() * 2L);                  // animated textures (words)
        if (tr3 && !ReadObjectTextures()) return 4;
        Report();

        // TR2 / TR3 have no triangle flag on textures: mark the ones used by room triangles.
        foreach (var room in Rooms)
            foreach (var tri in room.Triangles)
            {
                int t = tri.Texture & 0x7FFF;
                if (t < ObjectTextures.Length) ObjectTextures[t].NewFlags |= 0x8000;
            }

        // --- Entities (24 bytes) ---
        Items = [];
        AiObjects = [];
        count = br.ReadUInt32();
        if (count > 100000 || ms.Position + count * 24L > size) return 4;
        for (uint i = 0; i < count; i++)
        {
            var item = new LevelItem
            {
                TypeId = br.ReadUInt16(),
                Room = br.ReadInt16(),
                X = br.ReadInt32(),
                Y = br.ReadInt32(),
                Z = br.ReadInt32(),
                Angle = br.ReadUInt16(),
                Colour = 0xFFFF,
                Ocb = 0,
            };
            br.ReadInt16();                          // intensity1 (-1 = room lighting)
            br.ReadInt16();                          // intensity2
            item.Flags = (ushort)(br.ReadUInt16() & 0x3F80); // clear body + invisible + activation mask
            Items.Add(item);
        }

        TextureBitmap = BuildTr23Atlas(tex16, (int)numTextiles);
        progress?.Report(100);
        return 0;
    }

    /// <summary>
    /// Ambient intensity of TR1-TR3 rooms: 13-bit greyscale, higher = darker. TombLib packs it as
    /// (1 - luma(ambient / 2)) * 255 &lt;&lt; 5 (Compilers/Rooms.cs PackColorTo13BitGreyscale), so it is inverted here.
    /// </summary>
    private static void ApplyAmbient13Bit(LevelRoom r, short ambient)
    {
        float grey = Math.Clamp((1f - Math.Clamp(ambient >> 5, 0, 255) / 255f) * 2f, 0f, 2f);
        r.AmbientLight = new Vector3(grey);
        byte c = (byte)Math.Clamp((int)MathF.Round(grey * 128f) + 4, 0, 255); // keeps the TR4-style room colour consistent
        r.Colour.R = r.Colour.G = r.Colour.B = c;
    }

    /// <summary>tr2_room_light (24 bytes): X, Y, Z int32 | intensity1, intensity2 uint16 | fade1, fade2 uint32. No colour: white.</summary>
    private static LevelLight ReadLightTr2(BinaryReader br)
    {
        var l = new LevelLight
        {
            X = br.ReadInt32(), Y = br.ReadInt32(), Z = br.ReadInt32(),
            LightType = 1,
            ColourR = 128, ColourG = 128, ColourB = 128,
            Intensity = br.ReadUInt16(),
        };
        br.ReadUInt16();                             // intensity2
        l.Out = br.ReadUInt32() * 2f;                // TombLib writes fade = Out / 2
        br.ReadUInt32();                             // fade2
        l.In = l.Out / 2f;                           // no inner radius in the file: a guess
        return l;
    }

    /// <summary>tr3_room_light (24 bytes): X, Y, Z int32 | R, G, B, type bytes | point: intensity, fade uint32 / sun: nx, ny, nz int16 + padding.</summary>
    private static LevelLight ReadLightTr3(BinaryReader br)
    {
        var l = new LevelLight
        {
            X = br.ReadInt32(), Y = br.ReadInt32(), Z = br.ReadInt32(),
            ColourR = br.ReadByte(), ColourG = br.ReadByte(), ColourB = br.ReadByte(),
        };
        byte type = br.ReadByte();                   // 1 = sun, 0 = point (TombLib Compilers/Structs.cs)
        if (type == 1)
        {
            l.LightType = 0;
            // TombLib writes (ushort)(direction * 1024) with direction = -sunDirection.X / .Y / -.Z, so the raw
            // value is the signed int16 divided by 1024, the same convention as the TR4 sun fields.
            l.DirX = br.ReadInt16() / 1024f;
            l.DirY = br.ReadInt16() / 1024f;
            l.DirZ = br.ReadInt16() / 1024f;
            br.ReadUInt16();                         // padding
            l.Intensity = 8191;                      // a sun has no separate intensity: the colour carries it
        }
        else
        {
            l.LightType = 1;
            l.Intensity = (ushort)Math.Min(br.ReadUInt32(), ushort.MaxValue);
            l.Out = br.ReadUInt32() * 2f;            // TombLib writes fade = Out / 2
            l.In = l.Out / 2f;                       // no inner radius in the file: a guess
        }
        return l;
    }

    /// <summary>16-bit textiles (A1R5G5B5, bit 15 set = opaque) into the shared Bgr24 atlas; transparent pixels use the TR4 magenta placeholder.</summary>
    private static WriteableBitmap BuildTr23Atlas(byte[] tex16, int numTextiles)
    {
        int height = numTextiles * 256;
        var pixels = new byte[256 * height * 3];
        int o = 0;
        for (int i = 0; i < 256 * height; i++)
        {
            ushort v = (ushort)(tex16[i * 2] | (tex16[i * 2 + 1] << 8));
            if ((v & 0x8000) == 0)
            {
                pixels[o++] = 255; pixels[o++] = 0; pixels[o++] = 255;
            }
            else
            {
                int r = (v >> 10) & 31, g = (v >> 5) & 31, b = v & 31;
                pixels[o++] = (byte)((b << 3) | (b >> 2));
                pixels[o++] = (byte)((g << 3) | (g >> 2));
                pixels[o++] = (byte)((r << 3) | (r >> 2));
            }
        }
        var bmp = new WriteableBitmap(256, height, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, 256, height), pixels, 256 * 3, 0);
        return bmp;
    }
}
