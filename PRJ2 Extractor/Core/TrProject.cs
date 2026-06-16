using System.IO;
using System.Text;
using PRJ2_Extractor.Models;

namespace PRJ2_Extractor.Core;

public class TrProject : IDisposable
{
    public char[] Signature = "PROJFILE1\0\0\0".ToCharArray();
    public uint NumRoomSlots;
    public ushort NumUsedRooms;
    public PrjRoom[] Rooms = [];
    public uint NumThings;
    public uint MaxThings;
    public uint[] UnusedThings = [];
    public uint NumLights;
    public uint[] UnusedLights = [];
    public uint NumTriggers;
    public uint[] UnusedTriggers = [];
    public string TgaFilePath = "NA ";
    public uint NumTextures;
    public TexInfo[] Textures = [];
    public string WasFilePath = "NA ";
    public uint NumObjects;
    public WasObject[] WasObjects = [];
    public uint NumAnimRanges;
    public uint[] UnusedAnimRanges = [];
    public uint[] AnimTextures = [];
    public AnimTex[] AnimRanges = [];
    public byte[] TextureSounds = [];
    public byte[] BumpSettings = [];
    public List<string> InvalidHeights = [];

    public TrProject() { }

    public TrProject(ushort numRooms, uint numSlots)
    {
        Signature = "PROJFILE1\0\0\0".ToCharArray();
        NumRoomSlots = numSlots;
        NumUsedRooms = numRooms;
        Rooms = new PrjRoom[numSlots];
        for (int i = numRooms; i < numSlots; i++)
            Rooms[i] = new PrjRoom { Id = 1 };
        for (int i = 0; i < numRooms; i++)
        {
            Rooms[i] = new PrjRoom();
            var name = $"Room{i}";
            name.CopyTo(0, Rooms[i].Name, 0, Math.Min(name.Length, 79));
            Rooms[i].Link = (ushort)i;
            Rooms[i].Ambient.R = 128;
            Rooms[i].Ambient.G = 128;
            Rooms[i].Ambient.B = 128;
            Rooms[i].FlipRoom = -1;
        }
        MaxThings = 2000;
        UnusedThings = Enumerable.Range(0, (int)MaxThings).Select(i => (uint)i).ToArray();
        UnusedLights = Enumerable.Range(0, 768).Select(i => (uint)i).ToArray();
        UnusedTriggers = Enumerable.Range(0, 512).Select(i => (uint)i).ToArray();
        TgaFilePath = "NA ";
        WasFilePath = "NA ";
        UnusedAnimRanges = Enumerable.Range(0, 40).Select(i => (uint)i).ToArray();
        AnimTextures = Enumerable.Repeat(0xFFFFFFFFu, 256).ToArray();
        AnimRanges = new AnimTex[40];
        TextureSounds = Enumerable.Repeat((byte)6, 256).ToArray();
        BumpSettings = new byte[256];
        InvalidHeights = [];
    }

    public void Dispose() { }

    private static void WriteSz(char[] sz, int len, BinaryWriter bw)
    {
        for (int i = 0; i < len; i++)
            bw.Write(sz[i]);
    }

    private static void WriteStr(string s, BinaryWriter bw)
    {
        foreach (char c in s)
            bw.Write(c);
    }

    private static void WriteArray(uint[] a, BinaryWriter bw)
    {
        foreach (var v in a) bw.Write(v);
    }

    private static void WriteArray(ushort[] a, BinaryWriter bw)
    {
        foreach (var v in a) bw.Write(v);
    }

    private static void WriteArray(byte[] a, BinaryWriter bw)
    {
        foreach (var v in a) bw.Write(v);
    }

    private static void WriteWasObj(WasObject ob, BinaryWriter bw)
    {
        bw.Write(ob.SlotType);
        if (ob.SlotType == 0) return;
        WriteStr(ob.Name, bw);
        bw.Write(ob.Slot);
        bw.Write(ob.W);
        bw.Write(ob.N);
        bw.Write(ob.E);
        bw.Write(ob.S);
        for (int i = 1; i <= 5; i++)
        for (int j = 1; j <= 5; j++)
            bw.Write(ob.Collision[i, j]);
        for (int i = 1; i <= 5; i++)
        for (int j = 1; j <= 5; j++)
            bw.Write(ob.Mode[i, j]);
    }

    private static void WriteTex(TexInfo t, BinaryWriter bw)
    {
        bw.Write(t.X);
        bw.Write(t.Y);
        bw.Write(t.Unused);
        bw.Write(t.FlipX);
        bw.Write(t.Right);
        bw.Write(t.FlipY);
        bw.Write(t.Bottom);
    }

    private static void WriteDoor(Door d, BinaryWriter bw)
    {
        bw.Write(d.Id);
        bw.Write(d.XPos);
        bw.Write(d.ZPos);
        bw.Write(d.XSize);
        bw.Write(d.ZSize);
        bw.Write(d.YClickAboveFloor);
        bw.Write(d.Room);
        bw.Write(d.Slot);
        foreach (var f in d.Filler) bw.Write(f);
    }

    private static void WriteBlockTex(BlockTex bt, BinaryWriter bw)
    {
        bw.Write(bt.Tipo);
        bw.Write(bt.Index);
        bw.Write(bt.Flags1);
        bw.Write(bt.Rotation);
        bw.Write(bt.Triangle);
        bw.Write(bt.Filler);
    }

    private static void WriteBlock(Block b, BinaryWriter bw)
    {
        bw.Write(b.Id);
        bw.Write(b.Flags1);
        bw.Write(b.Floor);
        bw.Write(b.Ceiling);
        foreach (var c in b.FloorCorner) bw.Write(c);
        foreach (var c in b.CeilCorner) bw.Write(c);
        foreach (var c in b.FDiv) bw.Write(c);
        foreach (var c in b.CDiv) bw.Write(c);
        foreach (var t in b.Textures) WriteBlockTex(t, bw);
        bw.Write(b.Flags2);
        bw.Write(b.Flags3);
    }

    private static void WriteObj(RoomObj ob, BinaryWriter bw)
    {
        bw.Write(ob.Id);
        bw.Write(ob.XPos);
        bw.Write(ob.ZPos);
        bw.Write(ob.XSize);
        bw.Write(ob.ZSize);
        bw.Write(ob.YPos);
        bw.Write(ob.Room);
        bw.Write(ob.ObjectId);
        bw.Write(ob.Ocb);
        bw.Write(ob.Orientation);
        bw.Write(ob.WorldZ);
        bw.Write(ob.WorldY);
        bw.Write(ob.WorldX);
        bw.Write(ob.What5);
        bw.Write(ob.Facing);
        bw.Write(ob.Roll);
        bw.Write(ob.Tint);
        bw.Write(ob.Timer);
        if (ob.Id == 0x10)
        {
            bw.Write(ob.TriggerType);
            bw.Write(ob.ItemNumber);
            bw.Write(ob.TrigTimer);
            bw.Write(ob.Switches);
            bw.Write(ob.ItemType);
        }
    }

    private static void WriteLight(Light l, BinaryWriter bw)
    {
        bw.Write(l.Id);
        switch (l.Id)
        {
            case 0x4000 or 0x6000 or 0x4200 or 0x5000 or 0x4100 or 0x4020:
                foreach (var b in l.LightData) bw.Write(b);
                break;
            case 0x4C00 or 0x4400 or 0x4800 or 0x4080 or 0x4040:
                foreach (var b in l.Data) bw.Write(b);
                break;
        }
    }

    private void WriteRoom(int index, BinaryWriter bw)
    {
        if (Rooms.Length == 0 || index > Rooms.Length - 1) return;
        var r = Rooms[index];
        bw.Write(r.Id);
        if (r.Id == 1) return;
        WriteSz(r.Name, r.Name.Length, bw);
        bw.Write(r.Z);
        bw.Write(r.Y);
        bw.Write(r.X);
        bw.Write(r.Unknown2);
        bw.Write(r.What);
        bw.Write(r.XOffset);
        bw.Write(r.ZOffset);
        bw.Write(r.XSize);
        bw.Write(r.ZSize);
        bw.Write(r.XPos);
        bw.Write(r.ZPos);
        bw.Write(r.Link);
        bw.Write(r.NumDoors);
        if (r.NumDoors > 0)
        {
            WriteArray(r.DoorThingIndex, bw);
            for (int i = 0; i < r.NumDoors; i++)
                WriteDoor(r.Doors[i], bw);
        }
        bw.Write(r.NumObjects);
        if (r.NumObjects > 0)
        {
            WriteArray(r.ObjThingIndex, bw);
            for (int i = 0; i < r.NumObjects; i++)
                WriteObj(r.Objects[i], bw);
        }
        bw.Write(r.Ambient.R);
        bw.Write(r.Ambient.G);
        bw.Write(r.Ambient.B);
        bw.Write(r.Ambient.A);
        bw.Write(r.NumLights);
        if (r.NumLights > 0)
        {
            WriteArray(r.LightThingIndex, bw);
            foreach (var light in r.Lights)
                WriteLight(light, bw);
        }
        bw.Write(r.FlipRoom);
        bw.Write(r.Flags1);
        bw.Write(r.Water);
        bw.Write(r.Mist);
        bw.Write(r.Reflection);
        bw.Write(r.Flags2);
        foreach (var block in r.Blocks)
            WriteBlock(block, bw);
    }

    private static string ReadStr(BinaryReader br)
    {
        var sb = new StringBuilder();
        char c;
        do
        {
            c = br.ReadChar();
            sb.Append(c);
        } while (c != ' ');
        return sb.ToString();
    }

    private static RoomObj ReadRoomObj(BinaryReader br, ushort id)
    {
        var ob = new RoomObj { Id = id };
        ob.XPos = br.ReadInt16();
        ob.ZPos = br.ReadInt16();
        ob.XSize = br.ReadInt16();
        ob.ZSize = br.ReadInt16();
        ob.YPos = br.ReadUInt16();
        ob.Room = br.ReadUInt16();
        ob.ObjectId = br.ReadUInt16();
        ob.Ocb = br.ReadUInt16();
        ob.Orientation = br.ReadUInt16();
        ob.WorldZ = br.ReadInt32();
        ob.WorldY = br.ReadInt32();
        ob.WorldX = br.ReadInt32();
        ob.What5 = br.ReadUInt16();
        ob.Facing = br.ReadUInt16();
        ob.Roll = br.ReadInt16();
        ob.Tint = br.ReadUInt16();
        ob.Timer = br.ReadInt16();
        if (id == 0x10)
        {
            ob.TriggerType = br.ReadUInt16();
            ob.ItemNumber = br.ReadUInt16();
            ob.TrigTimer = br.ReadInt16();
            ob.Switches = br.ReadUInt16();
            ob.ItemType = br.ReadUInt16();
        }
        return ob;
    }

    private static Block ReadBlock(BinaryReader br)
    {
        var b = new Block();
        b.Id = br.ReadUInt16();
        b.Flags1 = br.ReadUInt16();
        b.Floor = br.ReadInt16();
        b.Ceiling = br.ReadInt16();
        for (int i = 0; i < 4; i++) b.FloorCorner[i] = br.ReadSByte();
        for (int i = 0; i < 4; i++) b.CeilCorner[i] = br.ReadSByte();
        for (int i = 0; i < 4; i++) b.FDiv[i] = br.ReadSByte();
        for (int i = 0; i < 4; i++) b.CDiv[i] = br.ReadSByte();
        for (int i = 0; i < 14; i++)
        {
            b.Textures[i] = new BlockTex
            {
                Tipo = br.ReadUInt16(),
                Index = br.ReadByte(),
                Flags1 = br.ReadByte(),
                Rotation = br.ReadByte(),
                Triangle = br.ReadByte(),
                Filler = br.ReadUInt16()
            };
        }
        b.Flags2 = br.ReadUInt16();
        b.Flags3 = br.ReadUInt16();
        return b;
    }

    public bool Save(string filename)
    {
        using var memfile = new MemoryStream();
        using (var bw = new BinaryWriter(memfile, Encoding.ASCII, leaveOpen: true))
        {
            WriteSz(Signature, Signature.Length, bw);
            bw.Write(NumRoomSlots);
            for (int i = 0; i < NumRoomSlots; i++)
                WriteRoom(i, bw);
            bw.Write(NumThings);
            bw.Write(MaxThings);
            WriteArray(UnusedThings, bw);
            bw.Write(NumLights);
            WriteArray(UnusedLights, bw);
            bw.Write(NumTriggers);
            WriteArray(UnusedTriggers, bw);
            WriteStr(TgaFilePath, bw);
            if (TgaFilePath != "NA ")
            {
                bw.Write(NumTextures);
                foreach (var t in Textures)
                    WriteTex(t, bw);
            }
            WriteStr(WasFilePath, bw);
            if (WasFilePath != "NA ")
            {
                bw.Write(NumObjects);
                foreach (var ob in WasObjects)
                    WriteWasObj(ob, bw);
            }
            bw.Write(NumAnimRanges);
            WriteArray(UnusedAnimRanges, bw);
            WriteArray(AnimTextures, bw);
            foreach (var ar in AnimRanges)
            {
                bw.Write(ar.Defined);
                bw.Write(ar.FirstTile);
                bw.Write(ar.LastTile);
            }
            WriteArray(TextureSounds, bw);
            WriteArray(BumpSettings, bw);
        }
        File.WriteAllBytes(filename, memfile.ToArray());
        return true;
    }

    public byte Load(string filename)
    {
        NumUsedRooms = 0;
        var bytes = File.ReadAllBytes(filename);
        using var memfile = new MemoryStream(bytes);
        using var br = new BinaryReader(memfile, Encoding.ASCII, leaveOpen: true);
        var sig = Encoding.ASCII.GetString(br.ReadBytes(12));
        if (sig != "PROJFILE1\0\0\0")
            return 1;

        NumRoomSlots = br.ReadUInt32();
        Rooms = new PrjRoom[NumRoomSlots];
        for (int i = 0; i < Rooms.Length; i++)
        {
            Rooms[i] = new PrjRoom();
            Rooms[i].Id = br.ReadUInt16();
            if (Rooms[i].Id == 1) continue;
            NumUsedRooms++;
            var roomName = br.ReadBytes(80);
            for (int j = 0; j < roomName.Length; j++)
                Rooms[i].Name[j] = (char)roomName[j];
            Rooms[i].Z = br.ReadUInt32();
            Rooms[i].Y = br.ReadInt32();
            Rooms[i].X = br.ReadUInt32();
            Rooms[i].Unknown2 = br.ReadUInt32();
            Rooms[i].What = br.ReadUInt16();
            Rooms[i].XOffset = br.ReadUInt16();
            Rooms[i].ZOffset = br.ReadUInt16();
            Rooms[i].XSize = br.ReadInt16();
            Rooms[i].ZSize = br.ReadInt16();
            Rooms[i].XPos = br.ReadInt16();
            Rooms[i].ZPos = br.ReadInt16();
            Rooms[i].Link = br.ReadUInt16();
            Rooms[i].NumDoors = br.ReadUInt16();
            if (Rooms[i].NumDoors > 0)
            {
                Rooms[i].DoorThingIndex = new ushort[Rooms[i].NumDoors];
                for (int j = 0; j < Rooms[i].NumDoors; j++)
                    Rooms[i].DoorThingIndex[j] = br.ReadUInt16();
                Rooms[i].Doors = new Door[Rooms[i].NumDoors];
                for (int j = 0; j < Rooms[i].NumDoors; j++)
                {
                    var d = new Door
                    {
                        Id = br.ReadUInt16(),
                        XPos = br.ReadInt16(),
                        ZPos = br.ReadInt16(),
                        XSize = br.ReadInt16(),
                        ZSize = br.ReadInt16(),
                        YClickAboveFloor = br.ReadUInt16(),
                        Room = br.ReadUInt16(),
                        Slot = br.ReadUInt16()
                    };
                    for (int k = 0; k < d.Filler.Length; k++)
                        d.Filler[k] = br.ReadUInt16();
                    Rooms[i].Doors[j] = d;
                }
            }
            Rooms[i].NumObjects = br.ReadUInt16();
            if (Rooms[i].NumObjects > 0)
            {
                Rooms[i].ObjThingIndex = new ushort[Rooms[i].NumObjects];
                for (int j = 0; j < Rooms[i].NumObjects; j++)
                    Rooms[i].ObjThingIndex[j] = br.ReadUInt16();
                Rooms[i].Objects = new RoomObj[Rooms[i].NumObjects];
                for (int j = 0; j < Rooms[i].NumObjects; j++)
                {
                    var id = br.ReadUInt16();
                    memfile.Position -= 2;
                    Rooms[i].Objects[j] = ReadRoomObj(br, id);
                }
            }
            Rooms[i].Ambient.R = br.ReadByte();
            Rooms[i].Ambient.G = br.ReadByte();
            Rooms[i].Ambient.B = br.ReadByte();
            Rooms[i].Ambient.A = br.ReadByte();
            Rooms[i].NumLights = br.ReadUInt16();
            if (Rooms[i].NumLights > 0)
            {
                Rooms[i].LightThingIndex = new ushort[Rooms[i].NumLights];
                for (int j = 0; j < Rooms[i].NumLights; j++)
                    Rooms[i].LightThingIndex[j] = br.ReadUInt16();
                Rooms[i].Lights = new Light[Rooms[i].NumLights];
                for (int j = 0; j < Rooms[i].NumLights; j++)
                {
                    var light = new Light { Id = br.ReadUInt16() };
                    switch (light.Id)
                    {
                        case 0x4000 or 0x6000 or 0x4200 or 0x5000 or 0x4100 or 0x4020:
                            light.LightData = br.ReadBytes(70);
                            break;
                        case 0x4C00 or 0x4400 or 0x4800 or 0x4080 or 0x4040:
                            light.Data = br.ReadBytes(40);
                            break;
                    }
                    Rooms[i].Lights[j] = light;
                }
            }
            Rooms[i].FlipRoom = br.ReadInt16();
            Rooms[i].Flags1 = br.ReadUInt16();
            Rooms[i].Water = br.ReadByte();
            Rooms[i].Mist = br.ReadByte();
            Rooms[i].Reflection = br.ReadByte();
            Rooms[i].Flags2 = br.ReadUInt16();
            int numBlocks = Rooms[i].XSize * Rooms[i].ZSize;
            Rooms[i].Blocks = new Block[numBlocks];
            for (int j = 0; j < numBlocks; j++)
                Rooms[i].Blocks[j] = ReadBlock(br);
        }

        NumThings = br.ReadUInt32();
        MaxThings = br.ReadUInt32();
        UnusedThings = new uint[MaxThings];
        for (int i = 0; i < MaxThings; i++)
            UnusedThings[i] = br.ReadUInt32();
        NumLights = br.ReadUInt32();
        UnusedLights = new uint[768];
        for (int i = 0; i < 768; i++)
            UnusedLights[i] = br.ReadUInt32();
        NumTriggers = br.ReadUInt32();
        UnusedTriggers = new uint[512];
        for (int i = 0; i < 512; i++)
            UnusedTriggers[i] = br.ReadUInt32();
        TgaFilePath = ReadStr(br);
        if (TgaFilePath != "NA ")
        {
            NumTextures = br.ReadUInt32();
            Textures = new TexInfo[NumTextures];
            for (int i = 0; i < NumTextures; i++)
            {
                Textures[i] = new TexInfo
                {
                    X = br.ReadByte(),
                    Y = br.ReadUInt16(),
                    Unused = br.ReadByte(),
                    FlipX = br.ReadByte(),
                    Right = br.ReadByte(),
                    FlipY = br.ReadByte(),
                    Bottom = br.ReadByte()
                };
            }
        }
        WasFilePath = ReadStr(br);
        if (WasFilePath != "NA ")
        {
            NumObjects = br.ReadUInt32();
            WasObjects = new WasObject[NumObjects];
            for (int i = 0; i < NumObjects; i++)
            {
                WasObjects[i] = new WasObject { SlotType = br.ReadUInt16() };
                if (WasObjects[i].SlotType == 0) continue;
                WasObjects[i].Name = ReadStr(br).TrimEnd(' ');
                WasObjects[i].Slot = br.ReadUInt32();
                WasObjects[i].W = br.ReadUInt16();
                WasObjects[i].N = br.ReadUInt16();
                WasObjects[i].E = br.ReadUInt16();
                WasObjects[i].S = br.ReadUInt16();
                for (int r = 1; r <= 5; r++)
                for (int c = 1; c <= 5; c++)
                    WasObjects[i].Collision[r, c] = br.ReadInt16();
                for (int r = 1; r <= 5; r++)
                for (int c = 1; c <= 5; c++)
                    WasObjects[i].Mode[r, c] = br.ReadInt16();
            }
        }
        NumAnimRanges = br.ReadUInt32();
        UnusedAnimRanges = new uint[40];
        for (int i = 0; i < 40; i++)
            UnusedAnimRanges[i] = br.ReadUInt32();
        AnimTextures = new uint[256];
        for (int i = 0; i < 256; i++)
            AnimTextures[i] = br.ReadUInt32();
        AnimRanges = new AnimTex[40];
        for (int i = 0; i < 40; i++)
        {
            AnimRanges[i] = new AnimTex
            {
                Defined = br.ReadUInt32(),
                FirstTile = br.ReadUInt32(),
                LastTile = br.ReadUInt32()
            };
        }
        TextureSounds = br.ReadBytes(256);
        BumpSettings = br.ReadBytes(256);
        return 0;
    }

    public bool InvalidBlockHeights
    {
        get
        {
            InvalidHeights.Clear();
            for (int r = 0; r < Rooms.Length; r++)
            {
                var rm = Rooms[r];
                if (rm.Id == 1) continue;
                for (int z = 0; z < rm.ZSize; z++)
                for (int x = 0; x < rm.XSize; x++)
                {
                    int b = (z * rm.XSize) + x;
                    if (rm.Blocks[b].Floor >= rm.Blocks[b].Ceiling)
                    {
                        var s = $"Room {r,3} Block {x + 1,3}, {z + 1,3} :: f {rm.Blocks[b].Floor,3} c {rm.Blocks[b].Ceiling,3}";
                        if (x == 0 || x == rm.XSize - 1 || z == 0 || z == rm.ZSize - 1)
                            s += " (outer border block)";
                        InvalidHeights.Add(s);
                    }
                }
            }
            if (InvalidHeights.Count > 0)
            {
                var header = "The following blocks had a floor height >= ceiling height which is invalid in NGLE." + Environment.NewLine +
                             "NGLE will report as errors and repair to make floor height = (ceiling height-1)." + Environment.NewLine +
                             "If block is outer border wall block only need to fix for texturing." + Environment.NewLine +
                             "If block is outer border door block, need to fix corresponding block adjacent to door in other room." + Environment.NewLine +
                             "Block 6, 4 means 6th block in row from west side and 4th block in column down from north side." + Environment.NewLine +
                             $"{InvalidHeights.Count} errors";
                InvalidHeights.Insert(0, header);
            }
            return InvalidHeights.Count > 0;
        }
    }

    public bool CopyDoorsFromPrj(AktrekkerPrj p)
    {
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = p.Rooms[i];
            if (r.Id == 1) continue;
            Rooms[i].Link = r.Link;
            Rooms[i].NumDoors = r.NumDoors;
            Rooms[i].DoorThingIndex = r.DoorThingIndex.ToArray();
            Rooms[i].Doors = r.Doors.Select(d => new Door
            {
                Id = d.Id, XPos = d.XPos, ZPos = d.ZPos, XSize = d.XSize, ZSize = d.ZSize,
                YClickAboveFloor = d.YClickAboveFloor, Room = d.Room, Slot = d.Slot
            }).ToArray();
            for (int j = 0; j < r.ZSize; j++)
            for (int k = 0; k < r.XSize; k++)
            {
                int b = j * r.XSize + k;
                var blok = r.Blocks[b];
                if (blok.Id is 0x7 or 0x3 or 0x5 or 0x6)
                {
                    Rooms[i].Blocks[b].Id = blok.Id;
                    if (blok.Id == 0x6)
                    {
                        Rooms[i].Blocks[b].Floor = blok.Floor;
                        Rooms[i].Blocks[b].Ceiling = blok.Ceiling;
                    }
                }
            }
        }
        return true;
    }

    public bool CopyTexFromPrj(AktrekkerPrj p)
    {
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = p.Rooms[i];
            if (r.Id == 1) continue;
            for (int j = 0; j < r.ZSize; j++)
            for (int k = 0; k < r.XSize; k++)
            {
                int b = j * r.XSize + k;
                var blok = r.Blocks[b];
                for (int ii = 0; ii < Rooms[i].Blocks[b].Textures.Length; ii++)
                {
                    Rooms[i].Blocks[b].Textures[ii].Tipo = blok.Textures[ii].Tipo;
                    Rooms[i].Blocks[b].Textures[ii].Index = blok.Textures[ii].Index;
                    Rooms[i].Blocks[b].Textures[ii].Flags1 = blok.Textures[ii].Flags1;
                    Rooms[i].Blocks[b].Textures[ii].Rotation = blok.Textures[ii].Rotation;
                    Rooms[i].Blocks[b].Textures[ii].Triangle = blok.Textures[ii].Triangle;
                }
                if (Rooms[i].Blocks[b].Id is 0x1E or 0xE)
                {
                    Rooms[i].Blocks[b].Floor = blok.Floor;
                    Rooms[i].Blocks[b].Ceiling = blok.Ceiling;
                }
                for (int ii = 0; ii < 4; ii++)
                {
                    Rooms[i].Blocks[b].FDiv[ii] = blok.FDiv[ii];
                    Rooms[i].Blocks[b].CDiv[ii] = blok.CDiv[ii];
                }
            }
        }
        NumTextures = p.NumTextures;
        Textures = p.Textures.Select(t => new TexInfo
        {
            X = t.X, Y = t.Y, Unused = t.Unused, FlipX = t.FlipX,
            Right = t.Right, FlipY = t.FlipY, Bottom = t.Bottom
        }).ToArray();
        return true;
    }

    public bool CopyLightsFromPrj(AktrekkerPrj p)
    {
        ushort lightCount = 0;
        ushort lightCount2 = 0;
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = p.Rooms[i];
            if (r.Id == 1) continue;
            Rooms[i].NumLights = r.NumLights;
            Rooms[i].LightThingIndex = new ushort[r.NumLights];
            for (int j = 0; j < r.NumLights; j++)
            {
                Rooms[i].LightThingIndex[j] = (ushort)(NumThings + lightCount);
                lightCount++;
            }
            Rooms[i].Lights = new Light[r.NumLights];
            for (int j = 0; j < r.NumLights; j++)
            {
                Rooms[i].Lights[j] = new Light
                {
                    Id = r.Lights[j].Id,
                    LightData = r.Lights[j].LightData.ToArray(),
                    Slot = lightCount2
                };
                lightCount2++;
                if (Rooms[i].Lights[j].Id == 0x6000)
                    Rooms[i].Lights[j].Intensity = (short)-Rooms[i].Lights[j].Intensity;
            }
        }
        NumLights = lightCount;
        NumThings += NumLights;
        return true;
    }

    public bool IsCompatible(AktrekkerPrj p)
    {
        if (p.NumRoomSlots < NumRoomSlots) return false;
        if (NumUsedRooms != p.NumUsedRooms) return false;
        for (int i = 0; i < Rooms.Length; i++)
        {
            var r = p.Rooms[i];
            if (Rooms[i].Id != r.Id) return false;
            if (Rooms[i].Id == 1) continue;
            if (Rooms[i].XSize != r.XSize) return false;
            if (Rooms[i].ZSize != r.ZSize) return false;
        }
        return true;
    }
}

public class AktrekkerPrj : TrProject
{
    public AktrekkerPrj(ushort numRooms, uint numSlots) : base(numRooms, numSlots) { }
    public AktrekkerPrj() { }
}
