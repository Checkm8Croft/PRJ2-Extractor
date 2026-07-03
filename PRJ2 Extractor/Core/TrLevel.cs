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
                do
                {
                    data = FloorData[fdIndex + k];
                    k++;
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
            int totalHeight = NumRoomTextiles * 256;
            if (NumBumpTextiles > 0)
                totalHeight += (NumBumpTextiles / 2) * 256;

            var allPixels = new byte[256 * totalHeight * 3];
            using (var br3 = new BinaryReader(tex32, Encoding.UTF8, leaveOpen: true))
            {
                int offset = 0;
                for (int i = 0; i < NumRoomTextiles * 256; i++)
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
                    tex32.Seek(NumObjTextiles * 256 * 256 * 4, SeekOrigin.Current);
                    for (int i = NumRoomTextiles * 256; i < totalHeight; i++)
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
                    Z = br2.ReadInt32(),
                    X = br2.ReadInt32(),
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
                r.NumX = br2.ReadUInt16();
                r.NumZ = br2.ReadUInt16();
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
                geometry.Seek(lightCount * 46, SeekOrigin.Current);
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
            geometry.Seek(size * 16, SeekOrigin.Current);
            size = br2.ReadUInt32();
            geometry.Seek(size * 40, SeekOrigin.Current);
            size = br2.ReadUInt32();
            geometry.Seek(size * 16, SeekOrigin.Current);
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

        if (result == 0)
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
        return result;
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

    private static ObjectTexture ReadObjectTexture(BinaryReader br)
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
        return texture;
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

        if (saveTga && TextureBitmap != null && TextureBitmap.PixelWidth > 0)
        {
            var tgaPath = Path.ChangeExtension(filename, ".tga");
            TgaWriter.Save(TextureBitmap, tgaPath);
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

            for (int j = 0; j < r1.NumZ; j++)
            for (int k = 0; k < r1.NumX; k++)
            {
                int b = j * r1.NumX + k;
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

                if ((k == 0 && j == 0) || (k == r1.NumX - 1 && j == 0) ||
                    (k == 0 && j == r1.NumZ - 1) || (k == r1.NumX - 1 && j == r1.NumZ - 1))
                {
                    block.Id = 0x1E; block.Floor = 0; block.Ceiling = 20;
                }
                else if (j == 0 || j == r1.NumZ - 1 || k == 0 || k == r1.NumX - 1)
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

            ApplyRoomMeshTextures(p.Rooms[i], r1, ObjectTextures.Length);
        }

        BuildPrjTextureTable(p);
        return p;
    }

    private void BuildPrjTextureTable(TrProject p)
    {
        int count = Math.Min(ObjectTextures.Length, 1024);
        p.NumTextures = (uint)count;
        p.Textures = new TexInfo[count];
        for (int i = 0; i < count; i++)
            p.Textures[i] = ToPrjTexInfo(ObjectTextures[i]);
    }

    private static TexInfo ToPrjTexInfo(ObjectTexture texture)
    {
        int tile = texture.TileAndFlag & 0x7FFF;
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
            Y = (ushort)((tile * 256) + minY),
            Unused = 0,
            FlipX = 0,
            Right = (byte)Math.Max(1, maxX - minX),
            FlipY = 0,
            Bottom = (byte)Math.Max(1, maxY - minY)
        };
    }

    private static void ApplyRoomMeshTextures(PrjRoom prjRoom, LevelRoom levelRoom, int objectTextureCount)
    {
        foreach (var face in levelRoom.Rectangles)
            ApplyRoomFaceTexture(prjRoom, levelRoom, face, objectTextureCount);
        foreach (var face in levelRoom.Triangles)
            ApplyRoomFaceTexture(prjRoom, levelRoom, face, objectTextureCount);
    }

    private static void ApplyRoomFaceTexture(PrjRoom prjRoom, LevelRoom levelRoom, RoomFace face, int objectTextureCount)
    {
        int textureIndex = face.Texture & 0x7FFF;
        if (textureIndex < 0 || textureIndex >= objectTextureCount || textureIndex > 1023) return;
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

        int slot;
        int blockX;
        int blockZ;
        const int epsilon = 8;

        if (Math.Abs(maxY - minY) <= epsilon)
        {
            blockX = Math.Clamp(avgX / 1024, 0, prjRoom.XSize - 1);
            blockZ = Math.Clamp(avgZ / 1024, 0, prjRoom.ZSize - 1);
            int blockIndex = blockZ * prjRoom.XSize + blockX;
            if (blockIndex < 0 || blockIndex >= prjRoom.Blocks.Length) return;

            int floorY = -prjRoom.Blocks[blockIndex].Floor * 256;
            int ceilingY = -prjRoom.Blocks[blockIndex].Ceiling * 256;
            if (Math.Abs(avgY - floorY) <= Math.Abs(avgY - ceilingY))
                slot = face.IsTriangle ? 8 : 0;
            else
                slot = face.IsTriangle ? 9 : 1;
        }
        else if (Math.Abs(maxX - minX) <= epsilon)
        {
            blockX = Math.Clamp((int)Math.Round(avgX / 1024.0), 0, prjRoom.XSize - 1);
            blockZ = Math.Clamp(avgZ / 1024, 0, prjRoom.ZSize - 1);
            slot = 4;
        }
        else if (Math.Abs(maxZ - minZ) <= epsilon)
        {
            blockX = Math.Clamp(avgX / 1024, 0, prjRoom.XSize - 1);
            blockZ = Math.Clamp((int)Math.Round(avgZ / 1024.0), 0, prjRoom.ZSize - 1);
            slot = 7;
        }
        else
        {
            return;
        }

        int target = blockZ * prjRoom.XSize + blockX;
        if (target < 0 || target >= prjRoom.Blocks.Length) return;
        SetBlockTexture(prjRoom.Blocks[target].Textures[slot], textureIndex, face);
    }

    private static void SetBlockTexture(BlockTex blockTex, int textureIndex, RoomFace face)
    {
        blockTex.Tipo = 0x0007;
        blockTex.Index = (byte)(textureIndex & 0xFF);
        blockTex.Flags1 = (byte)((textureIndex >> 8) & 0x03);
        if ((face.Texture & 0x8000) != 0)
            blockTex.Flags1 |= 0x04;
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
            if (fd.AddX >= 0) { block.FloorCorner[2] = fd.AddX; block.FloorCorner[3] = fd.AddX; }
            else { block.FloorCorner[0] = (sbyte)-fd.AddX; block.FloorCorner[1] = (sbyte)-fd.AddX; }
            if (fd.AddZ >= 0) { block.FloorCorner[0] += fd.AddZ; block.FloorCorner[3] += fd.AddZ; }
            else { block.FloorCorner[2] += (sbyte)Math.Abs(fd.AddZ); block.FloorCorner[1] += (sbyte)Math.Abs(fd.AddZ); }
            block.Floor -= (short)(Math.Abs(fd.AddX) + Math.Abs(fd.AddZ));
            if (fixFdivs)
            {
                int v = -Math.Abs(block.Floor - (-r1.YBottom / 256));
                for (int ii = 0; ii < 4; ii++) block.FDiv[ii] = (sbyte)v;
            }
            return;
        }

        if (fd.Tipo == FloorType.Roof)
        {
            if (fd.AddX >= 0) { block.CeilCorner[0] = (sbyte)-fd.AddX; block.CeilCorner[1] = (sbyte)-fd.AddX; }
            else { block.CeilCorner[2] = fd.AddX; block.CeilCorner[3] = fd.AddX; }
            if (fd.AddZ >= 0) { block.CeilCorner[1] -= fd.AddZ; block.CeilCorner[2] -= fd.AddZ; }
            else { block.CeilCorner[0] += fd.AddZ; block.CeilCorner[3] += fd.AddZ; }
            block.Ceiling += (short)(Math.Abs(fd.AddX) + Math.Abs(fd.AddZ));
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
        block.FloorCorner[0] = (sbyte)fd.Corners[0];
        block.FloorCorner[1] = (sbyte)fd.Corners[1];
        block.FloorCorner[2] = (sbyte)fd.Corners[2];
        block.FloorCorner[3] = (sbyte)fd.Corners[3];
        int[] a = { fd.Corners[0], fd.Corners[1], fd.Corners[2], fd.Corners[3] };
        int maxCorner = a.Max();
        block.Floor -= (short)maxCorner;

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
        block.CeilCorner[0] = (sbyte)-fd.Corners[0];
        block.CeilCorner[1] = (sbyte)-fd.Corners[1];
        block.CeilCorner[2] = (sbyte)-fd.Corners[2];
        block.CeilCorner[3] = (sbyte)-fd.Corners[3];
        int maxCorner = fd.Corners.Max();
        block.Ceiling += (short)maxCorner;
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

                if (portal.Normal.X == 1)
                { d.Id = 2; d.ZPos = 0; d.ZSize = 1; d.XPos = (short)(minz / 1024); d.XSize = (short)((maxz - minz) / 1024); }
                if (portal.Normal.X == -1)
                { d.Id = 0xFFFD; d.ZPos = (short)(minx / 1024); d.ZSize = 1; d.XPos = (short)(minz / 1024); d.XSize = (short)((maxz - minz) / 1024); }
                if (portal.Normal.Z == 1)
                { d.Id = 1; d.XPos = 0; d.XSize = 1; d.ZPos = (short)(minx / 1024); d.ZSize = (short)((maxx - minx) / 1024); }
                if (portal.Normal.Z == -1)
                { d.Id = 0xFFFE; d.XPos = (short)(minz / 1024); d.XSize = 1; d.ZPos = (short)(minx / 1024); d.ZSize = (short)((maxx - minx) / 1024); }
                if (portal.Normal.Y == -1)
                { d.Id = 4; d.XPos = (short)(minz / 1024); d.XSize = (short)((maxz - minz) / 1024); d.ZPos = (short)(minx / 1024); d.ZSize = (short)((maxx - minx) / 1024); }
                if (portal.Normal.Y == 1)
                { d.Id = 0xFFFB; d.XPos = (short)(minz / 1024); d.XSize = (short)((maxz - minz) / 1024); d.ZPos = (short)(minx / 1024); d.ZSize = (short)((maxx - minx) / 1024); }

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
            for (int j = 0; j < p.Rooms[i].Doors.Length; j++)
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
                var bloks = d.GetBlockIndices(p.Rooms[i].XSize);
                Door? dd = null;
                for (int k = 0; k < p.Rooms[d.Filler[0]].Doors.Length; k++)
                {
                    if (d.Slot == p.Rooms[d.Filler[0]].DoorThingIndex[k])
                    { dd = p.Rooms[d.Filler[0]].Doors[k]; break; }
                }
                if (dd == null) continue;
                var bloks2 = dd.GetAdjacentBlockIndices(p.Rooms[dd.Room].XSize);
                if (bloks.Length != bloks2.Length) continue;
                int rm = p.Rooms[i].IsFlipRoom && p.Rooms[dd.Room].FlipRoom > -1
                    ? p.Rooms[dd.Room].FlipRoom : dd.Room;
                for (int k = 0; k < bloks.Length; k++)
                {
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
