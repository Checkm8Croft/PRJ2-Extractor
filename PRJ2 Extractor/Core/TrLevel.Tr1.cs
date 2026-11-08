using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PRJ2_Extractor.Models;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Tomb Raider 1 (.phd / .tub, version 0x00000020) reader. It fills the same model as the TR4 / TR5 loaders so that
/// <see cref="ConvertToPrj"/> and <see cref="Prj2Exporter"/> are shared.
/// Layout source of truth: TombLib Compilers/Structs.cs (PrjRoom.WriteTr1) and Compilers/Tr1.cs, cross-checked with
/// trview (trlevel/Level_tr1_pc.ixx) and TRosettaStone.
/// </summary>
public partial class TrLevel
{
    private const uint Tr1Version = 0x00000020;

    /// <summary>True for a Tomb Raider 1 (.phd / .tub) level.</summary>
    public bool IsTr1;

    private byte LoadTr1(string filename, IProgress<int>? progress)
    {
        var bytes = File.ReadAllBytes(filename);
        using var ms = new MemoryStream(bytes, writable: false);
        using var br = new BinaryReader(ms);
        long size = ms.Length;
        void Report() => progress?.Report((int)Math.Min(99, ms.Position * 100 / size));

        IsTr1 = true;
        FileVersion = br.ReadUInt32();

        // --- 8-bit textiles (256 x 256, palette indices); the palette only comes at the very end of the geometry. ---
        uint numTextiles = br.ReadUInt32();
        if (numTextiles == 0 || numTextiles > 512 || ms.Position + numTextiles * 65536L > size) return 4;
        var textiles = br.ReadBytes((int)numTextiles * 65536);
        NumRoomTextiles = (ushort)numTextiles; // TR1 has a single textile pool: no room / object / bump split
        NumObjTextiles = 0;
        NumBumpTextiles = 0;
        br.ReadUInt32(); // unused
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
            if (dataWords > 0) ReadRoomDataTr1To3(br, r, extendedVertex: false);
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
                // TR1 stores the plain box number (0xFFFF = none); the shared code uses the TR4 encoding (bits 4-14, 0x7FF = none).
                s.BoxIndex = s.BoxIndex == 0xFFFF ? (ushort)0x7FF0 : (ushort)((s.BoxIndex & 0x7FF) << 4);
                r.Sectors[j] = s;
            }

            ApplyAmbient13Bit(r, br.ReadInt16());

            ushort lightCount = br.ReadUInt16();
            for (int li = 0; li < lightCount; li++)
            {
                // tr_room_light (TR1): X, Y, Z int32 | intensity uint16 | fade uint32 (TombLib writes fade = Out / 2)
                var l = new LevelLight
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    LightType = 1,
                    ColourR = 128, ColourG = 128, ColourB = 128, // greyscale lighting: white at full strength
                    Intensity = br.ReadUInt16(),
                };
                uint fade = br.ReadUInt32();
                l.Out = fade * 2f;
                l.In = l.Out / 2f; // TR1 has no inner radius: this is a guess (not recoverable)
                r.Lights.Add(l);
            }

            ushort staticCount = br.ReadUInt16();
            for (int si = 0; si < staticCount; si++)
            {
                // tr_room_staticmesh (TR1, 18 bytes): X, Y, Z int32 | rotation uint16 | intensity uint16 | mesh id uint16
                var st = new LevelStatic
                {
                    X = br.ReadInt32(),
                    Y = br.ReadInt32(),
                    Z = br.ReadInt32(),
                    Rotation = br.ReadUInt16(),
                };
                br.ReadUInt16(); // 13-bit intensity (higher = darker): no use in TombLib, instances stay untinted
                st.Colour = 0x4210;
                st.MeshId = br.ReadUInt16();
                r.Statics.Add(st);
            }

            r.AltRoom = br.ReadInt16();
            r.Flags = (ushort)(br.ReadUInt16() & 0x0001); // bit 0 = water; the other bits are not used by TR1
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
        void Skip(long bytesToSkip) => ms.Seek(bytesToSkip, SeekOrigin.Current);
        Skip(br.ReadUInt32() * 2L);                  // mesh data (words)
        Skip(br.ReadUInt32() * 4L);                  // mesh pointers
        NumAnimations = br.ReadUInt32();
        Skip(NumAnimations * 32L);                   // animations
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

        // --- Object textures (20 bytes) ---
        uint numObjectTextures = br.ReadUInt32();
        if (numObjectTextures > 0x100000 || ms.Position + numObjectTextures * 20L > size) return 4;
        ObjectTextures = new ObjectTexture[numObjectTextures];
        for (int i = 0; i < ObjectTextures.Length; i++)
            ObjectTextures[i] = ReadObjectTextureTr1(br);

        // TR1 textures have no triangle flag: a texture used by a room triangle only defines 3 corners (the 4th is padding).
        foreach (var room in Rooms)
            foreach (var tri in room.Triangles)
            {
                int t = tri.Texture & 0x7FFF;
                if (t < ObjectTextures.Length) ObjectTextures[t].NewFlags |= 0x8000;
            }

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
        if (NumBoxes > 0x10000 || ms.Position + NumBoxes * 20L > size) return 4;
        Boxes = new LevelBox[NumBoxes];
        for (int i = 0; i < NumBoxes; i++)
        {
            // TR1 box: Zmin, Zmax, Xmin, Xmax int32 (world units) | true floor int16 | overlap index int16
            int zMin = br.ReadInt32(), zMax = br.ReadInt32(), xMin = br.ReadInt32(), xMax = br.ReadInt32();
            Boxes[i] = new LevelBox
            {
                ZMin = (byte)(zMin >> 10), ZMax = (byte)(zMax >> 10), XMin = (byte)(xMin >> 10), XMax = (byte)(xMax >> 10),
                TrueFloor = br.ReadInt16(), OverlapIndex = br.ReadInt16()
            };
        }

        Skip(br.ReadUInt32() * 2L);                  // overlaps
        Skip(NumBoxes * 12L);                        // zones (6 x int16 per box)
        Skip(br.ReadUInt32() * 2L);                  // animated textures (words)
        Report();

        // --- Entities (22 bytes) ---
        Items = [];
        AiObjects = [];
        count = br.ReadUInt32();
        if (count > 100000) return 4;
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
            br.ReadInt16();                            // intensity (-1 = room lighting): not used
            item.Flags = (ushort)(br.ReadUInt16() & 0x3F00); // invisible + activation mask (TR1 has no clear-body bit)
            Items.Add(item);
        }

        Skip(8192);                                    // light map (32 x 256)

        // --- Palette (6-bit RGB) and atlas ---
        var palette = br.ReadBytes(768);
        if (palette.Length < 768) return 4;
        TextureBitmap = BuildTr1Atlas(textiles, palette, (int)numTextiles);
        progress?.Report(100);
        return 0;
    }

    /// <summary>Room geometry shared by TR1-TR3; the vertex is 8 bytes in TR1 and 12 bytes in TR2 / TR3 (attributes + second lighting / colour).</summary>
    private static void ReadRoomDataTr1To3(BinaryReader br, LevelRoom room, bool extendedVertex)
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
            };
            if (extendedVertex)
            {
                room.Vertices[i].Attributes = br.ReadUInt16();
                room.Vertices[i].Colour = br.ReadUInt16();
            }
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

    /// <summary>tr_object_texture (TR1, 20 bytes): attribute, tile, then 4 vertices of (Xfrac, Xpixel, Yfrac, Ypixel); same vertex encoding as TR4.</summary>
    private static ObjectTexture ReadObjectTextureTr1(BinaryReader br)
    {
        var texture = new ObjectTexture
        {
            Attribute = br.ReadUInt16(),
            TileAndFlag = br.ReadUInt16(),
        };
        for (int i = 0; i < texture.Vertices.Length; i++)
        {
            texture.Vertices[i].X = br.ReadUInt16();
            texture.Vertices[i].Y = br.ReadUInt16();
        }
        return texture;
    }

    private static WriteableBitmap BuildTr1Atlas(byte[] textiles, byte[] palette, int numTextiles)
    {
        int height = numTextiles * 256;
        var pixels = new byte[256 * height * 3];
        static byte Scale6(byte v) => (byte)(((v & 63) << 2) | ((v & 63) >> 4));
        int o = 0;
        for (int i = 0; i < 256 * height; i++)
        {
            byte index = textiles[i];
            if (index == 0) // transparent: same magenta placeholder as the TR4 loader
            {
                pixels[o++] = 255; pixels[o++] = 0; pixels[o++] = 255;
            }
            else
            {
                pixels[o++] = Scale6(palette[index * 3 + 2]); // B
                pixels[o++] = Scale6(palette[index * 3 + 1]); // G
                pixels[o++] = Scale6(palette[index * 3]);     // R
            }
        }
        var bmp = new WriteableBitmap(256, height, 96, 96, PixelFormats.Bgr24, null);
        bmp.WritePixels(new Int32Rect(0, 0, 256, height), pixels, 256 * 3, 0);
        return bmp;
    }
}
