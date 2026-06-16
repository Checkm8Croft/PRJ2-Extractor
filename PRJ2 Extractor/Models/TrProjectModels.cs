namespace PRJ2_Extractor.Models;

public class Color4
{
    public byte R, G, B, A;
}

public class Light
{
    public ushort Id;
    public byte[] LightData = new byte[70];
    public byte[] Data = new byte[40];

    // Parsed light fields (variant 3)
    public short XPos, ZPos, XSize, ZSize;
    public ushort YPos, Room, Slot, Timer, Orientation;
    public int Z, Y, X;
    public ushort What5, Facing;
    public short Roll;
    public ushort Speed, Ocb;
    public short Intensity;
    public float In, Out, X_, Y_, Length, Cut;
    public byte R, G, B, On;
}

public class RoomObj
{
    public ushort Id;
    public short XPos, ZPos, XSize, ZSize;
    public ushort YPos, Room, ObjectId, Ocb, Orientation;
    public int WorldZ, WorldY, WorldX;
    public ushort What5, Facing;
    public short Roll;
    public ushort Tint;
    public short Timer;
    public ushort TriggerType, ItemNumber;
    public short TrigTimer;
    public ushort Switches, ItemType;
}

public class Door
{
    public ushort Id;
    public short XPos, ZPos, XSize, ZSize;
    public ushort YClickAboveFloor;
    public ushort Room, Slot;
    public ushort[] Filler = new ushort[13];
}

public class BlockTex
{
    public ushort Tipo;
    public byte Index;
    public byte Flags1, Rotation, Triangle;
    public ushort Filler;
}

public class Block
{
    public ushort Id, Flags1;
    public short Floor, Ceiling;
    public sbyte[] FloorCorner = new sbyte[4];
    public sbyte[] CeilCorner = new sbyte[4];
    public sbyte[] FDiv = new sbyte[4];
    public sbyte[] CDiv = new sbyte[4];
    public BlockTex[] Textures = Enumerable.Range(0, 14).Select(_ => new BlockTex()).ToArray();
    public ushort Flags2, Flags3;

    public bool HasCornerDataFloor =>
        FloorCorner.Any(c => c != 0);

    public bool HasCornerDataCeil =>
        CeilCorner.Any(c => c != 0);
}

public class PrjRoom
{
    public ushort Id;
    public char[] Name = new char[80];
    public uint X, Z;
    public int Y;
    public uint Unknown2;
    public ushort What;
    public ushort XOffset, ZOffset;
    public short XSize, ZSize;
    public short XPos, ZPos;
    public ushort Link;
    public ushort NumDoors;
    public ushort[] DoorThingIndex = [];
    public Door[] Doors = [];
    public ushort NumObjects;
    public ushort[] ObjThingIndex = [];
    public RoomObj[] Objects = [];
    public Color4 Ambient = new();
    public ushort NumLights;
    public ushort[] LightThingIndex = [];
    public Light[] Lights = [];
    public short FlipRoom;
    public ushort Flags1;
    public byte Water, Mist, Reflection;
    public ushort Flags2;
    public Block[] Blocks = [];
    public int YTop, YBottom;
    public bool IsFlipRoom;
}

public class AnimTex
{
    public uint Defined, FirstTile, LastTile;
}

public class TexInfo
{
    public byte X;
    public ushort Y;
    public byte Unused, FlipX, Right, FlipY, Bottom;
}

public class WasObject
{
    public ushort SlotType;
    public string Name = "";
    public uint Slot;
    public ushort W, N, E, S;
    public short[,] Collision = new short[6, 6];
    public short[,] Mode = new short[6, 6];
}

public static class DoorExtensions
{
    public static bool SameDoor(this Door self, Door other) =>
        self.Id == (ushort)~other.Id &&
        self.XSize == other.XSize &&
        self.ZSize == other.ZSize &&
        self.Room == other.Filler[0];

    public static ushort[] GetBlockIndices(this Door self, int roomX)
    {
        if (self.Id == 1)
        {
            var result = new ushort[self.ZSize];
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)((self.ZPos * roomX) + (i * roomX));
            return result;
        }
        if (self.Id == 0xFFFE)
        {
            var result = new ushort[self.ZSize];
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)(((self.ZPos + 1) * roomX - 1) + (i * roomX));
            return result;
        }
        if (self.Id == 2)
        {
            var result = new ushort[self.XSize];
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)(self.XPos + i);
            return result;
        }
        if (self.Id == 0xFFFD)
        {
            var result = new ushort[self.XSize];
            for (int i = 0; i < result.Length; i++)
                result[i] = (ushort)((roomX * self.ZPos) + self.XPos + i);
            return result;
        }
        if (self.Id == 4 || self.Id == 0xFFFB)
        {
            var result = new ushort[self.XSize * self.ZSize];
            for (int y = 0; y < self.ZSize; y++)
            for (int x = 0; x < self.XSize; x++)
            {
                int i = x + self.XSize * y;
                result[i] = (ushort)((roomX * self.ZPos) + self.XPos + (roomX * y + x));
            }
            return result;
        }
        return [];
    }

    public static ushort[] GetAdjacentBlockIndices(this Door self, int roomX)
    {
        var result = self.GetBlockIndices(roomX);
        if (self.Id == 1)
            for (int i = 0; i < result.Length; i++) result[i]++;
        if (self.Id == 0xFFFE)
            for (int i = 0; i < result.Length; i++) result[i]--;
        if (self.Id == 2)
            for (int i = 0; i < result.Length; i++) result[i] = (ushort)(result[i] + roomX);
        if (self.Id == 0xFFFD)
            for (int i = 0; i < result.Length; i++) result[i] = (ushort)(result[i] - roomX);
        return result;
    }

    public static void MarkDoorBlocks(this Door self, PrjRoom room)
    {
        var bloks = self.GetBlockIndices(room.XSize);
        if (self.Id == 4 || self.Id == 0xFFFB)
        {
            foreach (var b in bloks)
            {
                var blok = room.Blocks[b];
                if (blok.Id == 0xE) continue;
                if (self.Id == 4)
                {
                    if (blok.Floor > room.YBottom || blok.HasCornerDataFloor || (blok.Flags2 & 0x6) > 0)
                        continue;
                }
                else
                {
                    if (blok.Ceiling < room.YTop || blok.HasCornerDataCeil || (blok.Flags2 & 0x18) > 0)
                        continue;
                }
                if (self.Id == 4 && blok.Id is not (3 or 7))
                    room.Blocks[b].Flags1 |= 2;
                if (self.Id == 0xFFFB && blok.Id is not (5 or 7))
                    room.Blocks[b].Flags1 |= 4;
            }
        }
        else
        {
            foreach (var b in bloks)
            {
                if (room.Blocks[b].Id == 0x1E)
                {
                    room.Blocks[b].Id = 6;
                    room.Blocks[b].Flags1 |= 8;
                }
            }
        }
    }
}
