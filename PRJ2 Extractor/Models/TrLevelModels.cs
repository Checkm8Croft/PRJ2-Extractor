namespace PRJ2_Extractor.Models;

public enum FloorType
{
    Floor = 0, Door, Tilt, Roof, Trigger, Lava, Climb, Split1, Split2, Split3, Split4,
    Nocol1, Nocol2, Nocol3, Nocol4, Nocol5, Nocol6, Nocol7, Nocol8, Monkey, Trigtrig, Beetle
}

public class ParsedFloorData
{
    public FloorType Tipo;
    public ushort ToRoom;
    public sbyte AddX, AddZ;
    public bool N, S, E, W;
    public int TriHLo, TriHHi;
    public ushort[] Corners = new ushort[4];
}

public class LevelSector
{
    public ushort FdIndex, BoxIndex;
    public byte RoomBelow;
    public sbyte Floor;
    public byte RoomAbove;
    public sbyte Ceiling;
    public List<ParsedFloorData>? FloorInfo;
    public bool HasFd;
}

public class LevelBox
{
    public byte XMin, XMax, ZMin, ZMax;
    public short TrueFloor, OverlapIndex;
}

public class Vertex
{
    public short X, Y, Z;
}

public class Portal
{
    public ushort ToRoom;
    public Vertex Normal = new();
    public Vertex[] Vertices = { new(), new(), new(), new() };
}

public class RoomColour
{
    public byte B, G, R, A;
}

public class LevelRoom
{
    public int X, Z, YBottom, YTop;
    public ushort NumZ, NumX;
    public LevelSector[] Sectors = [];
    public ushort NumPortals;
    public Portal[] Portals = [];
    public RoomColour Colour = new();
    public short AltRoom;
    public ushort Flags;
    public byte WaterScheme, Reverb, AltGroup;
    public bool IsFlipRoom;
    public short OriginalRoom = -1;
}

public static class PortalExtensions
{
    public static bool Adjoins(this Portal self, Portal other, int x, int z, int x1, int z1)
    {
        if (self.Normal.X != -other.Normal.X ||
            self.Normal.Y != -other.Normal.Y ||
            self.Normal.Z != -other.Normal.Z)
            return false;

        int minx = self.Vertices[0].X, miny = self.Vertices[0].Y, minz = self.Vertices[0].Z;
        int maxx = minx, maxy = miny, maxz = minz;
        int minx1 = other.Vertices[0].X, miny1 = other.Vertices[0].Y, minz1 = other.Vertices[0].Z;
        int maxx1 = minx1, maxy1 = miny1, maxz1 = minz1;

        for (int i = 1; i < 4; i++)
        {
            var v = self.Vertices[i];
            var v1 = other.Vertices[i];
            if (v.X < minx) minx = v.X;
            if (v.Y < miny) miny = v.Y;
            if (v.Z < minz) minz = v.Z;
            if (v.X > maxx) maxx = v.X;
            if (v.Y > maxy) maxy = v.Y;
            if (v.Z > maxz) maxz = v.Z;
            if (v1.X < minx1) minx1 = v1.X;
            if (v1.Y < miny1) miny1 = v1.Y;
            if (v1.Z < minz1) minz1 = v1.Z;
            if (v1.X > maxx1) maxx1 = v1.X;
            if (v1.Y > maxy1) maxy1 = v1.Y;
            if (v1.Z > maxz1) maxz1 = v1.Z;
        }

        minx += x; maxx += x;
        minz += z; maxz += z;
        minx1 += x1; maxx1 += x1;
        minz1 += z1; maxz1 += z1;

        if (self.Normal.Z != 0)
            return minx == minx1 && miny == miny1 && maxx == maxx1 && maxy == maxy1;
        if (self.Normal.X != 0)
            return miny == miny1 && minz == minz1 && maxy == maxy1 && maxz == maxz1;
        return minx == minx1 && minz == minz1 && maxx == maxx1 && maxz == maxz1;
    }

    public static bool PortalEquals(this Portal self, Portal other)
    {
        if (self.Normal.X != other.Normal.X ||
            self.Normal.Y != other.Normal.Y ||
            self.Normal.Z != other.Normal.Z ||
            self.ToRoom != other.ToRoom)
            return false;

        int minx = self.Vertices[0].X, miny = self.Vertices[0].Y, minz = self.Vertices[0].Z;
        int maxx = minx, maxy = miny, maxz = minz;
        int minx1 = other.Vertices[0].X, miny1 = other.Vertices[0].Y, minz1 = other.Vertices[0].Z;
        int maxx1 = minx1, maxy1 = miny1, maxz1 = minz1;

        for (int i = 1; i < 4; i++)
        {
            var v = self.Vertices[i];
            var v1 = other.Vertices[i];
            if (v.X < minx) minx = v.X;
            if (v.Y < miny) miny = v.Y;
            if (v.Z < minz) minz = v.Z;
            if (v.X > maxx) maxx = v.X;
            if (v.Y > maxy) maxy = v.Y;
            if (v.Z > maxz) maxz = v.Z;
            if (v1.X < minx1) minx1 = v1.X;
            if (v1.Y < miny1) miny1 = v1.Y;
            if (v1.Z < minz1) minz1 = v1.Z;
            if (v1.X > maxx1) maxx1 = v1.X;
            if (v1.Y > maxy1) maxy1 = v1.Y;
            if (v1.Z > maxz1) maxz1 = v1.Z;
        }

        return minx == minx1 && miny == miny1 && minz == minz1 &&
               maxx == maxx1 && maxy == maxy1 && maxz == maxz1;
    }
}
