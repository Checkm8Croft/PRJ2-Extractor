using System.IO;
using System.Numerics;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;

namespace PRJ2_Extractor.Core;

/// <summary>Converts a compiled TombEngine level (<see cref="TenLevel"/>) into a PRJ2 project: rooms, sectors, portals and room flags.</summary>
public static class TenExporter
{
    public static Level BuildLevel(TenLevel ten, string prj2FilePath, List<string> warnings, bool textures = true)
    {
        var level = new Level();
        level.Settings.GameVersion = TRVersion.Game.TombEngine;
        level.Settings.LevelFilePath = prj2FilePath;

        var rooms = new Room[ten.Rooms.Count];
        for (int i = 0; i < ten.Rooms.Count; i++)
            rooms[i] = BuildRoom(level, ten.Rooms[i], i, warnings);
        // Rooms are placed in their compiled slot, which is also what flip rooms and portals refer to.
        for (int i = 0; i < rooms.Length; i++) level.Rooms[i] = rooms[i];

        // Flip rooms: a base room names its flipped room; the flip number is the alternate group.
        for (int i = 0; i < ten.Rooms.Count; i++)
        {
            var t = ten.Rooms[i];
            if (t.FlippedRoom >= 0 && t.FlippedRoom < rooms.Length)
            {
                rooms[i].AlternateRoom = rooms[t.FlippedRoom];
                rooms[i].AlternateGroup = (short)t.FlipNumber;
                rooms[t.FlippedRoom].AlternateGroup = (short)t.FlipNumber;
            }
        }

        AddPortals(level, ten, rooms, warnings);
        var registry = new ObjectRegistry();
        TenObjects.Add(ten, rooms, registry, warnings);
        // Triggers last: their targets are the objects added above.
        TriggerExporter.ExportCore(rooms, ten.Rooms.Count,
            i => (ten.Rooms[i].XSize, ten.Rooms[i].ZSize),
            (i, x, z) => ten.ReadTrigger(ten.Rooms[i].Sectors[x * ten.Rooms[i].ZSize + z].TriggerIndex),
            false, true, registry, warnings);

        if (textures)
        {
            string dir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(prj2FilePath)) ?? ".", Path.GetFileNameWithoutExtension(prj2FilePath) + "_textures");
            var roomTextures = TenTextures.RegisterAtlases(level, ten.RoomAtlases, dir, "room", warnings);
            var animatedTextures = TenTextures.RegisterAtlases(level, ten.AnimatedAtlases, dir, "animated", warnings);
            var stats = TenTextures.Apply(ten, rooms, roomTextures, animatedTextures, warnings);
            warnings.Add($"Info: {stats.FacesTextured} of {stats.Faces} room faces textured ({stats.FacesWithoutPolygon} faces have no polygon, {stats.PolygonsUnmatched} of {stats.Polygons} polygons matched no face, {stats.MissingAtlas} faces lack an atlas).");
        }
        return level;
    }

    private static Room BuildRoom(Level level, TenRoom t, int index, List<string> warnings)
    {
        string name = string.IsNullOrWhiteSpace(t.Name) ? $"Room{index}" : t.Name;
        // The file stores half of the editor's ambient value.
        var room = new Room(level, t.XSize, t.ZSize, t.Ambient * 2, name);
        // World X/Z are in sector units, Y in world units. BottomHeight is only the lowest corner, not Position.Y; the exact
        // Position.Y is the negated plane height of any complete wall (every room has some: the border).
        int posY = -t.BottomHeight;
        var wall = t.Sectors.FirstOrDefault(s => TenSectors.IsFullWall(s));
        if (wall != null) posY = -(int)Math.Round(wall.FloorPlane[0].Z);
        room.Position = new VectorInt3(t.X / 1024, posY, t.Z / 1024);
        foreach (var tag in t.Tags) room.Properties.Tags.Add(tag);

        // Room flags as written by TombLib's TombEngine compiler (Compilers/TombEngine/Rooms.cs).
        var props = room.Properties;
        if ((t.Flags & 0x0001) != 0) props.Type = RoomType.Water;
        else if ((t.Flags & 0x0004) != 0) props.Type = RoomType.Quicksand;
        props.FlagHorizon = (t.Flags & 0x0008) != 0;
        props.FlagOutside = (t.Flags & 0x0020) != 0;
        props.FlagNoLensflare = (t.Flags & 0x0080) != 0;
        props.FlagDamage = (t.Flags & 0x0800) != 0;
        props.FlagCold = (t.Flags & 0x1000) != 0;

        for (int x = 0; x < t.XSize; x++)
        for (int z = 0; z < t.ZSize; z++)
            ApplySector(room, t, x, z, warnings, index);
        return room;
    }

    private static void ApplySector(Room room, TenRoom t, int x, int z, List<string> warnings, int roomIndex)
    {
        var ts = t.Sectors[x * t.ZSize + z];
        var sector = room.Sectors[x, z];
        int roomY = room.Position.Y;
        bool border = x == 0 || z == 0 || x == t.XSize - 1 || z == t.ZSize - 1;

        if (ts.Death) sector.Flags |= SectorFlags.DeathFire;
        if (ts.Monkeyswing) sector.Flags |= SectorFlags.Monkey;
        if (ts.ClimbNorth) sector.Flags |= SectorFlags.ClimbPositiveZ;
        if (ts.ClimbSouth) sector.Flags |= SectorFlags.ClimbNegativeZ;
        if (ts.ClimbEast) sector.Flags |= SectorFlags.ClimbPositiveX;
        if (ts.ClimbWest) sector.Flags |= SectorFlags.ClimbNegativeX;
        if (ts.MarkBeetle) sector.Flags |= SectorFlags.Beetle;
        if (ts.MarkTriggerer) sector.Flags |= SectorFlags.TriggerTriggerer;

        if (TenSectors.IsFullWall(ts))
        {
            sector.Type = border ? SectorType.BorderWall : SectorType.Wall;
            return;
        }

        var floor = TenSectors.Decode(ts.FloorSplitAngle, ts.FloorPlane, roomY);
        var ceiling = TenSectors.Decode(ts.CeilingSplitAngle, ts.CeilingPlane, roomY);
        if (!floor.Exact || !ceiling.Exact)
            warnings.Add($"Room {roomIndex} sector ({x},{z}): the {(floor.Exact ? "ceiling" : "floor")} shape is not reproducible with a TombLib surface");

        bool wallTriangle = floor.WallFirst || floor.WallSecond || ceiling.WallFirst || ceiling.WallSecond;
        sector.Type = wallTriangle ? SectorType.Wall : SectorType.Floor;

        sector.Floor.XnZn = (short)floor.XnZn; sector.Floor.XpZn = (short)floor.XpZn;
        sector.Floor.XnZp = (short)floor.XnZp; sector.Floor.XpZp = (short)floor.XpZp;
        sector.Ceiling.XnZn = (short)ceiling.XnZn; sector.Ceiling.XpZn = (short)ceiling.XpZn;
        sector.Ceiling.XnZp = (short)ceiling.XnZp; sector.Ceiling.XpZp = (short)ceiling.XpZp;
        sector.Floor.DiagonalSplit = wallTriangle && !floor.WallFirst && !floor.WallSecond ? ceiling.Split : floor.Split;
        sector.Ceiling.DiagonalSplit = wallTriangle && !ceiling.WallFirst && !ceiling.WallSecond ? floor.Split : ceiling.Split;
        sector.Floor.SplitDirectionIsXEqualsZ = floor.SplitDirectionIsXEqualsZ;
        sector.Ceiling.SplitDirectionIsXEqualsZ = ceiling.SplitDirectionIsXEqualsZ;
    }

    private static void AddPortals(Level level, TenLevel ten, Room[] rooms, List<string> warnings)
    {
        for (int i = 0; i < ten.Rooms.Count; i++)
        foreach (var portal in ten.Rooms[i].Portals)
        {
            int target = portal.RoomNumber;
            // AddObject also creates the mirrored portal in the adjoining room, so only the lower-indexed room adds it.
            if (target < 0 || target >= rooms.Length || target < i) continue;

            PortalDirection direction;
            if (portal.NormalY == 1) direction = PortalDirection.Ceiling;
            else if (portal.NormalY == -1) direction = PortalDirection.Floor;
            else if (portal.NormalZ == 1) direction = PortalDirection.WallNegativeZ;
            else if (portal.NormalZ == -1) direction = PortalDirection.WallPositiveZ;
            else if (portal.NormalX == 1) direction = PortalDirection.WallNegativeX;
            else if (portal.NormalX == -1) direction = PortalDirection.WallPositiveX;
            else { warnings.Add($"Room {i}: portal to room {target} has an unsupported normal"); continue; }

            int minX = int.MaxValue, maxX = int.MinValue, minZ = int.MaxValue, maxZ = int.MinValue;
            for (int v = 0; v < 4; v++)
            {
                int vx = portal.Vertices[v * 3], vz = portal.Vertices[v * 3 + 2];
                minX = Math.Min(minX, vx); maxX = Math.Max(maxX, vx);
                minZ = Math.Min(minZ, vz); maxZ = Math.Max(maxZ, vz);
            }

            // Wall portals sit one unit inside the sector that holds them (x or z = 1023 / 13312 ...); the other axis spans [min, max).
            int x0, x1, z0, z1;
            switch (direction)
            {
                case PortalDirection.WallNegativeX: case PortalDirection.WallPositiveX:
                    x0 = x1 = minX / 1024; z0 = minZ / 1024; z1 = maxZ / 1024 - 1; break;
                case PortalDirection.WallNegativeZ: case PortalDirection.WallPositiveZ:
                    z0 = z1 = minZ / 1024; x0 = minX / 1024; x1 = maxX / 1024 - 1; break;
                default:
                    x0 = minX / 1024; x1 = maxX / 1024 - 1; z0 = minZ / 1024; z1 = maxZ / 1024 - 1; break;
            }

            // A portal whose plane carries textured polygons is a portal with visible faces (traversable); the file has no flag for it.
            var instance = new PortalInstance(new RectangleInt2(x0, z0, x1, z1), direction, rooms[target]);
            if (HasPolygonsOn(ten.Rooms[i], portal)) instance.Opacity = PortalOpacity.TraversableFaces;
            try
            {
                rooms[i].AddObject(level, instance);
                // AddObject also made the mirrored portal in the adjoining room; its own side may carry textured faces too.
                var opposite = Opposite(direction);
                var mirror = rooms[target].Portals.FirstOrDefault(m => m.AdjoiningRoom == rooms[i] && m.Direction == opposite && m.Opacity == PortalOpacity.None);
                var mirrorSource = ten.Rooms[target].Portals.FirstOrDefault(m => m.RoomNumber == i && Normal(m) == opposite);
                if (mirror != null && mirrorSource != null && HasPolygonsOn(ten.Rooms[target], mirrorSource)) mirror.Opacity = PortalOpacity.TraversableFaces;
            }
            catch (Exception ex) { warnings.Add($"Room {i}: portal to room {target} [{direction}] ({x0},{z0})-({x1},{z1}) skipped: {ex.Message}"); }
        }
    }

    private static PortalDirection Opposite(PortalDirection d) => d switch
    {
        PortalDirection.Floor => PortalDirection.Ceiling,
        PortalDirection.Ceiling => PortalDirection.Floor,
        PortalDirection.WallNegativeX => PortalDirection.WallPositiveX,
        PortalDirection.WallPositiveX => PortalDirection.WallNegativeX,
        PortalDirection.WallNegativeZ => PortalDirection.WallPositiveZ,
        _ => PortalDirection.WallNegativeZ,
    };

    private static PortalDirection? Normal(TenPortal p)
    {
        if (p.NormalY == 1) return PortalDirection.Ceiling;
        if (p.NormalY == -1) return PortalDirection.Floor;
        if (p.NormalZ == 1) return PortalDirection.WallNegativeZ;
        if (p.NormalZ == -1) return PortalDirection.WallPositiveZ;
        if (p.NormalX == 1) return PortalDirection.WallNegativeX;
        if (p.NormalX == -1) return PortalDirection.WallPositiveX;
        return null;
    }
    /// <summary>True when the room has a polygon lying on the portal's plane inside its extent (portal vertices sit one unit off the face).</summary>
    private static bool HasPolygonsOn(TenRoom room, TenPortal portal)
    {
        var verts = Enumerable.Range(0, 4).Select(v => new Vector3(portal.Vertices[v * 3], portal.Vertices[v * 3 + 1], portal.Vertices[v * 3 + 2])).ToArray();
        var normal = Vector3.Normalize(new Vector3(portal.NormalX, portal.NormalY, portal.NormalZ));
        var min = new Vector3(verts.Min(v => v.X), verts.Min(v => v.Y), verts.Min(v => v.Z)) - Vector3.One * 3;
        var max = new Vector3(verts.Max(v => v.X), verts.Max(v => v.Y), verts.Max(v => v.Z)) + Vector3.One * 3;
        foreach (var bucket in room.Buckets)
        foreach (var poly in bucket.Polygons)
        {
            bool onPlane = true;
            foreach (int idx in poly.Indices)
            {
                var p = room.Positions[idx];
                if (Math.Abs(Vector3.Dot(normal, p - verts[0])) > 3 || p.X < min.X || p.X > max.X || p.Y < min.Y || p.Y > max.Y || p.Z < min.Z || p.Z > max.Z) { onPlane = false; break; }
            }
            if (onPlane) return true;
        }
        return false;
    }
    public static List<string> Export(TenLevel ten, string prj2FilePath)
    {
        var warnings = new List<string>();
        var level = BuildLevel(ten, prj2FilePath, warnings);
        Prj2Writer.SaveToPrj2(prj2FilePath, level);
        return warnings;
    }
}
