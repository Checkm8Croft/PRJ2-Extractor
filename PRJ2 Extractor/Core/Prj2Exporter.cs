using System.Numerics;
using PRJ2_Extractor.Models;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Converts a parsed TR4 level (<see cref="TrLevel"/>) into a native Tomb Editor PRJ2 file,
/// by building an in-memory TombLib.dll <see cref="Level"/>/<see cref="Room"/> object graph
/// and delegating serialization entirely to TombLib's own <see cref="Prj2Writer"/>.
///
/// Room/sector geometry, alternate (flip) room linking and portal detection are reused from
/// the already-working <see cref="TrLevel.ConvertToPrj"/> classic-PRJ conversion pipeline,
/// since that model (<see cref="Block"/> with per-corner byte deltas and split arrays) mirrors
/// the on-disk classic PRJ sector layout that TombLib's own PrjLoader reads. We are simply
/// re-targeting the *output* side from classic PRJ bytes to the TombLib object model.
/// </summary>
public static class Prj2Exporter
{
    public static List<string> Export(TrLevel trLevel, string prj2FilePath)
    {
        var warnings = new List<string>();

        // Reuse the existing, proven TR4 -> classic-PRJ-model conversion for all the hard geometry work
        // (floor data / tilts / splits / door-portal detection / alternate room bookkeeping).
        // fixFdivs=false: that flag is an NGLE/classic-PRJ-only workaround that stamps a synthetic
        // non-zero FDiv/CDiv value onto almost every block (floor/ceiling distance to room bounds),
        // not just genuinely split ones. In TombLib any non-zero SetHeight(Floor2/Ceiling2, ...) call
        // creates real diagonal-split geometry, so leaving it on turns flat/tilted terrain into spiky,
        // invalid sectors. Real splits from TR4 FloorData (Split1-4) are still applied unconditionally
        // by ApplyFloorSplit/ApplyCeilingSplit regardless of this flag.
        TrProject p = trLevel.ConvertToPrj(prj2FilePath, saveTga: false, fixFdivs: false);

        // Resolves portals into p.Rooms[i].Doors AND corrects floor/ceiling heights and sector
        // Ids of border-wall blocks adjacent to a door (MarkDoorBlocks). Must run before we read
        // sector geometry below. The tr2PrjLinks flag only affects the legacy classic-PRJ room
        // chain-link field (Room.Link), irrelevant to TombLib/PRJ2, so we pass false.
        trLevel.MakeDoors(p, tr2PrjLinks: false);

        var level = new Level();
        var tombRooms = new Room?[p.Rooms.Length];

        // --- Pass 1: create rooms and sector geometry ---
        for (int i = 0; i < p.Rooms.Length; i++)
        {
            var pr = p.Rooms[i];
            if (pr.Id == 1) continue; // undefined room slot

            string roomName = new string(pr.Name).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(roomName)) roomName = $"Room{i}";

            var room = new Room(level, pr.XSize, pr.ZSize, Vector3.One, roomName);
            room.Position = new VectorInt3(pr.XPos, pr.YBottom, pr.ZPos);

            for (int z = 0; z < pr.ZSize; z++)
            for (int x = 0; x < pr.XSize; x++)
            {
                int b = z * pr.XSize + x;
                var block = pr.Blocks[b];
                var sector = room.Sectors[x, z];

                sector.Type = block.Id switch
                {
                    0x01 or 0x05 or 0x07 or 0x03 => SectorType.Floor,
                    0x1E or 0x06 => SectorType.BorderWall,
                    0x0E => SectorType.Wall,
                    _ => SectorType.Floor
                };

                if (sector.Type == SectorType.Floor)
                {
                    // Base height + per-corner delta (in clicks) -> world units.
                    // Corner order follows the classic PRJ on-disk layout used by TombLib's PrjLoader:
                    // floor corners are [XpZn, XnZn, XnZp, XpZp].
                    sector.Floor.XpZn = (short)Clicks.ToWorld(block.FloorCorner[0] + block.Floor);
                    sector.Floor.XnZn = (short)Clicks.ToWorld(block.FloorCorner[1] + block.Floor);
                    sector.Floor.XnZp = (short)Clicks.ToWorld(block.FloorCorner[2] + block.Floor);
                    sector.Floor.XpZp = (short)Clicks.ToWorld(block.FloorCorner[3] + block.Floor);

                    // NOTE (interpretazione, verificata contro l'ordine di lettura in PrjLoader.cs):
                    // nel formato PRJ classico l'ordine degli angoli del soffitto è invertito
                    // rispetto al pavimento: [XpZp, XnZp, XnZn, XpZn].
                    sector.Ceiling.XpZp = (short)Clicks.ToWorld(block.CeilCorner[0] + block.Ceiling);
                    sector.Ceiling.XnZp = (short)Clicks.ToWorld(block.CeilCorner[1] + block.Ceiling);
                    sector.Ceiling.XnZn = (short)Clicks.ToWorld(block.CeilCorner[2] + block.Ceiling);
                    sector.Ceiling.XpZn = (short)Clicks.ToWorld(block.CeilCorner[3] + block.Ceiling);

                    // Diagonal-split triangulation (TR FloorData functions 0x07-0x12). TombLib's
                    // SectorSurface.SplitDirectionIsXEqualsZ (NOT the DiagonalSplit enum, which is
                    // only for portal sub-triangles we don't yet model) picks which diagonal the
                    // sector's 2 collision/render triangles are split along. When left at its
                    // default (auto-detected from the 4 corners), a non-coplanar quad still renders
                    // as 2 triangles, but along whichever diagonal happens to look flattest -- not
                    // necessarily the one TR4 actually intended, which is what was producing
                    // "illegal slope" sectors even though the 4 corner heights were individually correct.
                    if (block.FloorSplitXEqualsZ.HasValue)
                        sector.Floor.SplitDirectionIsXEqualsZ = block.FloorSplitXEqualsZ.Value;
                    if (block.CeilingSplitXEqualsZ.HasValue)
                        sector.Ceiling.SplitDirectionIsXEqualsZ = block.CeilingSplitXEqualsZ.Value;
                }
                else
                {
                    // Wall / BorderWall sectors: classic-PRJ perimeter placeholder blocks. They still
                    // carry leftover per-corner deltas from whatever real floor data was computed before
                    // being downgraded to a wall/border sentinel (their Ceiling is forced to a fixed
                    // value but Floor/FloorCorner are not), which can encode near-vertical fake slopes.
                    // Since these sectors have no floor/ceiling collision semantics, use flat heights
                    // instead of the per-corner deltas to avoid feeding TombLib invalid steep geometry.
                    short flatFloor = (short)Clicks.ToWorld(block.Floor);
                    short flatCeiling = (short)Clicks.ToWorld(block.Ceiling);
                    sector.Floor.XpZn = sector.Floor.XnZn = sector.Floor.XnZp = sector.Floor.XpZp = flatFloor;
                    sector.Ceiling.XpZp = sector.Ceiling.XnZp = sector.Ceiling.XnZn = sector.Ceiling.XpZn = flatCeiling;
                }
            }

            room.NormalizeRoomY();
            level.Rooms[i] = room;
            tombRooms[i] = room;
        }

        // --- Pass 2: alternate (flip) room linking ---
        // Uses the raw parsed TR4 room data directly (r1.AltRoom / r1.AltGroup / r1.IsFlipRoom)
        // rather than the classic-PRJ intermediate model, to avoid relying on TrProject.Flags2
        // (which mixes flag bits and the alternate group id via bitwise OR).
        for (int i = 0; i < trLevel.Rooms.Length; i++)
        {
            var r1 = trLevel.Rooms[i];
            if (r1.IsFlipRoom || r1.AltRoom < 0 || r1.AltRoom >= tombRooms.Length) continue;

            var baseRoom = tombRooms[i];
            var altRoom = tombRooms[r1.AltRoom];
            if (baseRoom == null || altRoom == null) continue;

            baseRoom.AlternateRoom = altRoom;
            baseRoom.AlternateGroup = r1.AltGroup;
            altRoom.AlternateBaseRoom = baseRoom;
            altRoom.AlternateGroup = r1.AltGroup;
            altRoom.Position = new VectorInt3(baseRoom.Position.X, altRoom.Position.Y, baseRoom.Position.Z);
        }

        // --- Pass 3: portals, built from the doors already resolved by ConvertToPrj/MakeDoors ---
        // TR4 sometimes encodes a single opening as several adjacent/overlapping quads (e.g. large
        // or non-trivially shaped portals get split during level compilation). TombLib only allows
        // one portal per sector face, so we group raw doors by (room, direction, adjoining room) and
        // add a single portal covering the union of their sector areas.
        for (int i = 0; i < p.Rooms.Length; i++)
        {
            var pr = p.Rooms[i];
            var room = tombRooms[i];
            if (pr.Id == 1 || room == null) continue;

            var groups = new Dictionary<(PortalDirection Direction, int Target), (int X0, int Z0, int X1, int Z1)>();

            foreach (var door in pr.Doors)
            {
                int targetIndex = door.Filler[0];
                if (targetIndex < 0 || targetIndex >= tombRooms.Length) continue;
                var adjoiningRoom = tombRooms[targetIndex];
                if (adjoiningRoom == null || adjoiningRoom == room) continue;

                PortalDirection? direction = door.Id switch
                {
                    1 => PortalDirection.WallNegativeZ,
                    2 => PortalDirection.WallNegativeX,
                    4 => PortalDirection.Floor,
                    0xFFFE => PortalDirection.WallPositiveZ,
                    0xFFFD => PortalDirection.WallPositiveX,
                    0xFFFB => PortalDirection.Ceiling,
                    _ => null
                };
                if (direction == null) continue; // unknown/unsupported door type, skip rather than fail the whole export

                int x0 = Math.Clamp((int)door.XPos, 0, room.NumXSectors - 1);
                int z0 = Math.Clamp((int)door.ZPos, 0, room.NumZSectors - 1);
                int x1 = Math.Clamp(door.XPos + door.XSize - 1, x0, room.NumXSectors - 1);
                int z1 = Math.Clamp(door.ZPos + door.ZSize - 1, z0, room.NumZSectors - 1);

                var key = (direction.Value, targetIndex);
                if (groups.TryGetValue(key, out var acc))
                    groups[key] = (Math.Min(acc.X0, x0), Math.Min(acc.Z0, z0), Math.Max(acc.X1, x1), Math.Max(acc.Z1, z1));
                else
                    groups[key] = (x0, z0, x1, z1);
            }

            foreach (var (key, rect) in groups)
            {
                var adjoiningRoom = tombRooms[key.Target]!;
                var area = new RectangleInt2(rect.X0, rect.Z0, rect.X1, rect.Z1);
                var portal = new PortalInstance(area, key.Direction, adjoiningRoom);
                try
                {
                    room.AddObject(level, portal);
                }
                catch (Exception ex)
                {
                    warnings.Add($"Room {i} ({room.Name}): portal to room {key.Target} [{key.Direction}] area ({rect.X0},{rect.Z0})-({rect.X1},{rect.Z1}) skipped: {ex.Message}");
                }
            }
        }

        Prj2Writer.SaveToPrj2(prj2FilePath, level);
        return warnings;
    }
}
