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
    public static void Export(TrLevel trLevel, string prj2FilePath)
    {
        // Reuse the existing, proven TR4 -> classic-PRJ-model conversion for all the hard geometry work
        // (floor data / tilts / splits / door-portal detection / alternate room bookkeeping).
        TrProject p = trLevel.ConvertToPrj(prj2FilePath, saveTga: false, fixFdivs: true);

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

                // Base height + per-corner delta (in clicks) -> world units.
                // Corner order follows the classic PRJ on-disk layout used by TombLib's PrjLoader:
                // floor corners are [XpZn, XnZn, XnZp, XpZp].
                sector.Floor.XpZn = (short)Clicks.ToWorld(block.FloorCorner[0] + block.Floor);
                sector.Floor.XnZn = (short)Clicks.ToWorld(block.FloorCorner[1] + block.Floor);
                sector.Floor.XnZp = (short)Clicks.ToWorld(block.FloorCorner[2] + block.Floor);
                sector.Floor.XpZp = (short)Clicks.ToWorld(block.FloorCorner[3] + block.Floor);

                // NOTE (interpretazione, da verificare visivamente in Tomb Editor):
                // nel formato PRJ classico l'ordine degli angoli del soffitto è invertito
                // rispetto al pavimento: [XpZp, XnZp, XnZn, XpZn]. TrLevel.ApplyFloorData
                // popola CeilCorner con la stessa simmetria invertita (vedi caso Roof),
                // quindi applichiamo qui la stessa corrispondenza.
                sector.Ceiling.XpZp = (short)Clicks.ToWorld(block.CeilCorner[0] + block.Ceiling);
                sector.Ceiling.XnZp = (short)Clicks.ToWorld(block.CeilCorner[1] + block.Ceiling);
                sector.Ceiling.XnZn = (short)Clicks.ToWorld(block.CeilCorner[2] + block.Ceiling);
                sector.Ceiling.XpZn = (short)Clicks.ToWorld(block.CeilCorner[3] + block.Ceiling);

                if (block.FDiv[0] != 0 || block.FDiv[1] != 0 || block.FDiv[2] != 0 || block.FDiv[3] != 0)
                {
                    sector.SetHeight(SectorVerticalPart.Floor2, SectorEdge.XpZn, Clicks.ToWorld(block.FDiv[0] + block.Floor));
                    sector.SetHeight(SectorVerticalPart.Floor2, SectorEdge.XnZn, Clicks.ToWorld(block.FDiv[1] + block.Floor));
                    sector.SetHeight(SectorVerticalPart.Floor2, SectorEdge.XnZp, Clicks.ToWorld(block.FDiv[2] + block.Floor));
                    sector.SetHeight(SectorVerticalPart.Floor2, SectorEdge.XpZp, Clicks.ToWorld(block.FDiv[3] + block.Floor));
                }

                if (block.CDiv[0] != 0 || block.CDiv[1] != 0 || block.CDiv[2] != 0 || block.CDiv[3] != 0)
                {
                    sector.SetHeight(SectorVerticalPart.Ceiling2, SectorEdge.XpZp, Clicks.ToWorld(block.CDiv[0] + block.Ceiling));
                    sector.SetHeight(SectorVerticalPart.Ceiling2, SectorEdge.XnZp, Clicks.ToWorld(block.CDiv[1] + block.Ceiling));
                    sector.SetHeight(SectorVerticalPart.Ceiling2, SectorEdge.XnZn, Clicks.ToWorld(block.CDiv[2] + block.Ceiling));
                    sector.SetHeight(SectorVerticalPart.Ceiling2, SectorEdge.XpZn, Clicks.ToWorld(block.CDiv[3] + block.Ceiling));
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

        // --- Pass 3: portals, built from the doors already resolved by ConvertToPrj ---
        for (int i = 0; i < p.Rooms.Length; i++)
        {
            var pr = p.Rooms[i];
            var room = tombRooms[i];
            if (pr.Id == 1 || room == null) continue;

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

                var area = new RectangleInt2(x0, z0, x1, z1);
                var portal = new PortalInstance(area, direction.Value, adjoiningRoom);
                room.AddObject(level, portal);
            }
        }

        Prj2Writer.SaveToPrj2(prj2FilePath, level);
    }
}
