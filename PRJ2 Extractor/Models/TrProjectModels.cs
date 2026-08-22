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
    // Widened from byte to int: the classic-PRJ on-disk format packs this into 8 bits (plus 2 more
    // borrowed from Flags1, for a 10-bit/1024 ceiling), but this project no longer round-trips
    // through that on-disk representation for texture data -- Prj2Exporter reads this field to
    // build a modern TombLib TextureArea directly (no 10-bit limit there). Keeping it byte-sized
    // silently truncated/wrapped every texture index above 1023, which for TombLib-compiled levels
    // (routinely thousands of ObjectTextures) corrupted effectively all room-face texture
    // assignments (verified: alexhub2 has 656/656 distinct room-face texture indices > 1023).
    public int Index;
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

    // Diagonal-split triangulation (TR3+ FloorData functions 0x07-0x12), used to build
    // TombLib's Sector.Floor/Ceiling.DiagonalSplit -- NOT related to FDiv/CDiv above, which
    // encode an unrelated classic-PRJ/NGLE "extra floor level" feature.
    // FloorSplit/CeilingSplit: null = no triangulation (single plane / Tilt / flat).
    // true = diagonal runs XnZn-XpZp ("NE-SW"), false = diagonal runs XnZp-XpZn ("NW-SE").
    public bool? FloorSplitXEqualsZ;
    public bool? CeilingSplitXEqualsZ;

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

    public static ushort[] GetBlockIndices(this Door self, int roomZ)
    {
        // Blocks[] is populated X-major (TrLevel.cs): b = X_idx*NumZ + Z_idx, i.e. Z is the
        // fast/inner axis, and moving one step along X jumps by NumZ (=room.ZSize, passed here
        // as roomZ). XPos/XSize is the X-column range, ZPos/ZSize is the Z-row range.
        // For wall doors either XSize or ZSize is 1, so this degenerates correctly to a single
        // line of blocks along the wall.
        var result = new ushort[self.XSize * self.ZSize];
        for (int y = 0; y < self.ZSize; y++)
        for (int x = 0; x < self.XSize; x++)
            result[x + self.XSize * y] = (ushort)((self.XPos + x) * roomZ + (self.ZPos + y));
        return result;
    }

    public static ushort[] GetAdjacentBlockIndices(this Door self, int roomZ)
    {
        // The "adjacent" block is one step further in the direction the portal's normal points.
        // In the X-major Blocks[] layout (b = X_idx*NumZ + Z_idx), Z is the fast axis (step=1)
        // and X is the slow axis (step=roomZ):
        // Id 1 (Normal.Z==1) -> +Z (+1); Id 0xFFFE (Normal.Z==-1) -> -Z (-1);
        // Id 2 (Normal.X==1) -> +X (+roomZ); Id 0xFFFD (Normal.X==-1) -> -X (-roomZ).
        var result = self.GetBlockIndices(roomZ);
        if (self.Id == 1)
            for (int i = 0; i < result.Length; i++) result[i]++;
        if (self.Id == 0xFFFE)
            for (int i = 0; i < result.Length; i++) result[i]--;
        if (self.Id == 2)
            for (int i = 0; i < result.Length; i++) result[i] = (ushort)(result[i] + roomZ);
        if (self.Id == 0xFFFD)
            for (int i = 0; i < result.Length; i++) result[i] = (ushort)(result[i] - roomZ);
        return result;
    }

    public static void MarkDoorBlocks(this Door self, PrjRoom room)
    {
        var bloks = self.GetBlockIndices(room.ZSize).Where(b => b < room.Blocks.Length).ToArray();
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
