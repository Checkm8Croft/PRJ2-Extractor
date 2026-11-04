using System.Numerics;
using TombLib.LevelData;
using TombLib.LevelData.SectorEnums;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Inverse of TombLib's TombEngine collision compiler (Compilers/TombEngine/FloorData.cs, RoomSectorShape and
/// BuildFloorDataCollision): rebuilds a sector's floor or ceiling surface (four corner heights, diagonal split
/// type and split direction) from the two "fake planes" stored in the .ten file. Heights are TombLib world units
/// relative to the room's Position.Y.
/// </summary>
internal static class TenSectors
{
    internal struct SurfaceInfo
    {
        public int XnZn, XpZn, XnZp, XpZp;
        public DiagonalSplit Split;
        public bool SplitDirectionIsXEqualsZ;
        public bool WallFirst, WallSecond;     // a triangle that is a wall (plane marked -float.MaxValue)
        public bool Exact;                     // false when no TombLib surface reproduces the planes
    }

    private const float HalfSector = 512f;

    private static bool IsWallMarker(Vector3 plane) => plane.Z < -1e30f;

    // y = c + a * xl + b * zl (engine Y, down is positive); H = -y - roomY
    private static int Height(Vector3 plane, float xl, float zl, int roomY)
        => (int)Math.Round(-(plane.Z + plane.X * xl + plane.Y * zl) - roomY);

    /// <summary>
    /// The compiler writes Planes[*].Z = -room.Position.Y for all four planes of a complete wall, so floor and ceiling are flat, equal
    /// and unsplit (an open sector never has its floor at its ceiling). That also gives the room's real Position.Y.
    /// </summary>
    public static bool IsFullWall(TenSector s)
    {
        return Math.Abs(s.FloorPlane[0].X) < 1e-6f && Math.Abs(s.FloorPlane[0].Y) < 1e-6f
            && Math.Abs(s.CeilingPlane[0].X) < 1e-6f && Math.Abs(s.CeilingPlane[0].Y) < 1e-6f
            && s.FloorPlane[0] == s.FloorPlane[1] && s.CeilingPlane[0] == s.CeilingPlane[1]
            && s.FloorPlane[0].Z == s.CeilingPlane[0].Z
            && s.FloorSplitAngle == 0 && s.CeilingSplitAngle == 0;
    }

    public static SurfaceInfo Decode(float splitAngle, Vector3[] planes, int roomY)
    {
        var res = new SurfaceInfo { Exact = true };
        bool wall0 = IsWallMarker(planes[0]), wall1 = IsWallMarker(planes[1]);
        bool split = splitAngle > 0.1f;
        bool xEqualsZ = split && Math.Abs(splitAngle - (float)(Math.PI / 4)) < 0.01f;

        if (!split)
        {
            var p = planes[0];
            res.XnZn = Height(p, -HalfSector, -HalfSector, roomY);
            res.XpZn = Height(p, HalfSector, -HalfSector, roomY);
            res.XnZp = Height(p, -HalfSector, HalfSector, roomY);
            res.XpZp = Height(p, HalfSector, HalfSector, roomY);
            res.Split = DiagonalSplit.None;
            // A planar sector has no split in the file; the editor's default direction is X = Z, which also decides how TombLib lays out
            // the quad's vertices (and with it how a texture is attached to the corners).
            res.SplitDirectionIsXEqualsZ = true;
            return res;
        }

        res.SplitDirectionIsXEqualsZ = xEqualsZ;

        if (wall0 || wall1)
        {
            // Diagonal wall: only one triangle has a plane. With a step of 0 the surface heights equal the shape heights, and the
            // unknown corner copies a neighbour (the editor never stores heights for the wall corner).
            var p = wall0 ? planes[1] : planes[0];
            int xnzn = Height(p, -HalfSector, -HalfSector, roomY), xpzn = Height(p, HalfSector, -HalfSector, roomY);
            int xnzp = Height(p, -HalfSector, HalfSector, roomY), xpzp = Height(p, HalfSector, HalfSector, roomY);
            res.XnZn = xnzn; res.XpZn = xpzn; res.XnZp = xnzp; res.XpZp = xpzp;
            if (xEqualsZ) res.Split = wall0 ? DiagonalSplit.XnZp : DiagonalSplit.XpZn;
            else res.Split = wall0 ? DiagonalSplit.XpZp : DiagonalSplit.XnZn;
            res.WallFirst = wall0; res.WallSecond = wall1;
            return res;
        }

        int sXnZn, sXpZn, sXnZp, sXpZp, step;
        if (xEqualsZ)
        {
            // T0 through (XnZp, XpZp, XnZn); T1 through (XpZn, XnZn + step, XpZp + step)
            sXnZp = Height(planes[0], -HalfSector, HalfSector, roomY);
            sXpZp = Height(planes[0], HalfSector, HalfSector, roomY);
            sXnZn = Height(planes[0], -HalfSector, -HalfSector, roomY);
            sXpZn = Height(planes[1], HalfSector, -HalfSector, roomY);
            step = Height(planes[1], -HalfSector, -HalfSector, roomY) - sXnZn;
        }
        else
        {
            // T0 through (XpZp, XpZn + step, XnZp + step); T1 through (XnZn, XnZp, XpZn)
            sXpZp = Height(planes[0], HalfSector, HalfSector, roomY);
            sXnZn = Height(planes[1], -HalfSector, -HalfSector, roomY);
            sXnZp = Height(planes[1], -HalfSector, HalfSector, roomY);
            sXpZn = Height(planes[1], HalfSector, -HalfSector, roomY);
            step = Height(planes[0], HalfSector, -HalfSector, roomY) - sXpZn;
        }

        res.XnZn = sXnZn; res.XpZn = sXpZn; res.XnZp = sXnZp; res.XpZp = sXpZp;
        if (step == 0) { res.Split = DiagonalSplit.None; return res; }

        // Try the diagonal split types that match the split direction (XnZp / XpZn use XEqualsZ, XnZn / XpZp do not).
        if (xEqualsZ)
        {
            if (step == sXpZn - sXpZp) { res.Split = DiagonalSplit.XnZp; return res; }
            if (sXnZp == sXnZn) { res.Split = DiagonalSplit.XpZn; res.XnZn = sXnZn + step; res.XpZp = sXpZp + step; return res; }
        }
        else
        {
            if (step == sXpZp - sXnZp) { res.Split = DiagonalSplit.XnZn; return res; }
            if (sXnZn == sXpZn) { res.Split = DiagonalSplit.XpZp; res.XpZn = sXpZn + step; res.XnZp = sXnZp + step; return res; }
        }
        res.Exact = false;
        res.Split = DiagonalSplit.None;
        return res;
    }
}
