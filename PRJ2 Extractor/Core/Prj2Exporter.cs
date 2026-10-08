using System.Numerics;
using System.IO;
using PRJ2_Extractor.Models;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;
using TombLib.Utils;
using System.Diagnostics;

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
        var problematicRows = new HashSet<int> { 104, 103, 60, 61, 48, 49, 52, 24, 17 };

        // Reuse the existing, proven TR4 -> classic-PRJ-model conversion for all the hard geometry work
        // (floor data / tilts / splits / door-portal detection / alternate room bookkeeping).
        // fixFdivs=false: that flag is an NGLE/classic-PRJ-only workaround that stamps a synthetic
        // non-zero FDiv/CDiv value onto almost every block (floor/ceiling distance to room bounds),
        // not just genuinely split ones. In TombLib any non-zero SetHeight(Floor2/Ceiling2, ...) call
        // creates real diagonal-split geometry, so leaving it on turns flat/tilted terrain into spiky,
        // invalid sectors. Real splits from TR4 FloorData (Split1-4) are still applied unconditionally
        // by ApplyFloorSplit/ApplyCeilingSplit regardless of this flag.
        // saveTga=true: writes the room-texture atlas next to the .prj2 as a real .tga file and
        // populates p.TgaFilePath -- needed below to build a TombLib LevelTexture that actual face
        // textures can reference (SetFaceTexture requires a real Texture with a loadable image).
        TrProject p = trLevel.ConvertToPrj(prj2FilePath, saveTga: true, fixFdivs: false);

        // Resolves portals into p.Rooms[i].Doors AND corrects floor/ceiling heights and sector
        // Ids of border-wall blocks adjacent to a door (MarkDoorBlocks). Must run before we read
        // sector geometry below. The tr2PrjLinks flag only affects the legacy classic-PRJ room
        // chain-link field (Room.Link), irrelevant to TombLib/PRJ2, so we pass false.
        trLevel.MakeDoors(p, tr2PrjLinks: false);

        var level = new Level();
        // Needed before MakeRelative/MakeAbsolute calls below (texture registration) -- mirrors
        // PrjLoader's own "level.Settings.LevelFilePath = ..." setup at the start of LoadFromPrj.
        level.Settings.LevelFilePath = prj2FilePath;
        var tombRooms = new Room?[p.Rooms.Length];

        // Register the room-texture atlas TGA (written above by ConvertToPrj) as a TombLib
        // LevelTexture, so sector face textures below can reference real image data. Mirrors
        // PrjLoader's own texture-loading step ("Read texture" in LoadFromPrj): same
        // convert512PixelsToDoubleRows=true (a no-op for our always-256-wide atlas, kept only for
        // parity with the reference behaviour).
        LevelTexture? levelTexture = null;
        if (!string.IsNullOrWhiteSpace(p.TgaFilePath))
        {
            string tgaPath = Path.Combine(Path.GetDirectoryName(prj2FilePath) ?? ".", p.TgaFilePath.Trim());
            if (File.Exists(tgaPath))
            {
                levelTexture = new LevelTexture(level.Settings,
                    level.Settings.MakeRelative(tgaPath, VariableType.LevelDirectory), true);
                level.Settings.Textures.Add(levelTexture);
                if (levelTexture.LoadException != null)
                    warnings.Add($"Texture atlas '{tgaPath}' failed to load: {levelTexture.LoadException.Message}");
            }
            else
            {
                warnings.Add($"Texture atlas '{tgaPath}' was not found on disk; face textures will be skipped.");
            }
        }
        else
        {
            warnings.Add("No texture atlas was exported; face textures will be skipped.");
        }

        // --- Pass 1: create rooms and sector geometry ---
        for (int i = 0; i < p.Rooms.Length; i++)
        {
            var pr = p.Rooms[i];
            if (pr.Id == 1) continue; // undefined room slot

            string roomName = new string(pr.Name).TrimEnd('\0', ' ');
            if (string.IsNullOrWhiteSpace(roomName)) roomName = $"Room{i}";

            var room = new Room(level, pr.XSize, pr.ZSize, Vector3.One, roomName);
            // Position.X/Z are sector-grid units (matching XSize/ZSize), but Position.Y must be
            // world units, not clicks (verified: TombLib's Room.Position.Y is compared directly
            // against Prj2Loader-reconstructed sector heights, which are in world units).
            room.Position = new VectorInt3(pr.XPos, Clicks.ToWorld(pr.YBottom), pr.ZPos);

            for (int z = 0; z < pr.ZSize; z++)
            for (int x = 0; x < pr.XSize; x++)
            {
                // pr.Blocks[] is X-major (TrLevel.cs: b = X_idx*ZSize + Z_idx), matching the
                // TRosettaStone file order. Index accordingly (not z*XSize+x).
                int b = x * pr.ZSize + z;
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
                    // Absolute world height = -RoomYBottom + base + corner delta (verified against
                    // TombLib's own compiler, Compilers/Rooms.cs: compiledSector.Floor/Ceiling formula).
                    // floor corners are [XpZn, XnZn, XnZp, XpZp]; ceiling delta is ADDED, floor delta SUBTRACTED.
                    int floorBase = -pr.YBottom + block.Floor;
                    sector.Floor.XpZn = (short)Clicks.ToWorld(floorBase - block.FloorCorner[0]);
                    sector.Floor.XnZn = (short)Clicks.ToWorld(floorBase - block.FloorCorner[1]);
                    sector.Floor.XnZp = (short)Clicks.ToWorld(floorBase - block.FloorCorner[2]);
                    sector.Floor.XpZp = (short)Clicks.ToWorld(floorBase - block.FloorCorner[3]);

                    // NOTE (interpretazione, verificata contro l'ordine di lettura in PrjLoader.cs):
                    // nel formato PRJ classico l'ordine degli angoli del soffitto Ã¨ invertito
                    // rispetto al pavimento: [XpZp, XnZp, XnZn, XpZn].
                    int ceilBase = -pr.YBottom + block.Ceiling;
                    sector.Ceiling.XpZp = (short)Clicks.ToWorld(ceilBase + block.CeilCorner[0]);
                    sector.Ceiling.XnZp = (short)Clicks.ToWorld(ceilBase + block.CeilCorner[1]);
                    sector.Ceiling.XnZn = (short)Clicks.ToWorld(ceilBase + block.CeilCorner[2]);
                    sector.Ceiling.XpZn = (short)Clicks.ToWorld(ceilBase + block.CeilCorner[3]);

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
                    short flatFloor = (short)Clicks.ToWorld(-pr.YBottom + block.Floor);
                    short flatCeiling = (short)Clicks.ToWorld(-pr.YBottom + block.Ceiling);
                    sector.Floor.XpZn = sector.Floor.XnZn = sector.Floor.XnZp = sector.Floor.XpZp = flatFloor;
                    sector.Ceiling.XpZp = sector.Ceiling.XnZp = sector.Ceiling.XnZn = sector.Ceiling.XpZn = flatCeiling;
                }
            }

            SynthesizeWallHeights(room, trLevel.Rooms[i], pr);
            ApplyCompiledTriangleSplits(room, trLevel.Rooms[i], pr);
            room.NormalizeRoomY();
            ExportLights(room, trLevel.Rooms[i], pr);
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
        // add a single portal covering the union of their sector areas. (Tried adding each door
        // individually largest-first instead: empirically worse -- 127 vs 114 conflicts on alexhub2 --
        // so the union grouping stays.)
        for (int i = 0; i < p.Rooms.Length; i++)
        {
            var pr = p.Rooms[i];
            var room = tombRooms[i];
            if (pr.Id == 1 || room == null) continue;

            var groups = new Dictionary<(PortalDirection Direction, int Target), HashSet<(int X, int Z)>>();

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
                if (!groups.TryGetValue(key, out var cells)) groups[key] = cells = new();
                for (int cx = x0; cx <= x1; cx++)
                    for (int cz = z0; cz <= z1; cz++)
                        cells.Add((cx, cz));
            }

            foreach (var (key, cells) in groups)
            {
                // TombLib's Room.AddObject auto-creates the mirrored portal in the adjoining room, so
                // adding it again from that room's own (independent, TR4-sourced) door list would
                // always conflict with the auto-created copy. Process each room pair once, from the
                // lower-indexed room only; this was the dominant cause of "Portal overlaps another".
                if (key.Target < i) continue;

                var adjoiningRoom = tombRooms[key.Target]!;
                foreach (var rect in DecomposeIntoRectangles(cells))
                {
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
        }

        ExportSoundSources(trLevel, tombRooms, warnings);
        ExportSinks(trLevel, tombRooms, warnings);
        ExportCameras(trLevel, tombRooms, warnings);
        ExportFlybyCameras(trLevel, tombRooms, warnings);

        // --- Pass 4: face textures ---
        // Must run after ALL rooms/portals are set up: IsFaceDefined/GetFaceShape below depend on
        // each room's mesh having been built (mirrors PrjLoader's own two-phase "Build geometry"
        // then "Texturize faces" order in LoadFromPrj).
        if (levelTexture != null)
        {
            foreach (var room in tombRooms)
                room?.BuildGeometry(useLegacyCode: false);

            for (int i = 0; i < p.Rooms.Length; i++)
            {
                var pr = p.Rooms[i];
                var room = tombRooms[i];
                if (pr.Id == 1 || room == null) continue;
                ApplyRoomFaceTextures(room, pr, levelTexture, p.Textures);
                FillMissingSplitTriangles(room);
            }
        }

        Prj2Writer.SaveToPrj2(prj2FilePath, level);
        return warnings;
    }

    /// <summary>
    /// Textures the second triangle of a split floor/ceiling sector when the TR4 only compiled the first one (DOCUMENTATION.md 2.33).
    /// A non-planar sector is two triangles in TombLib; where one of them touches the ceiling (zero height) or is otherwise not
    /// compiled, the TR4 has a single triangle and the other face would stay bare. Both triangles are halves of the same texture
    /// square, so the missing one is completed from its textured sibling: the two shared sector corners keep the sibling's UV and the
    /// remaining corner takes the fourth corner of the sibling's right-triangle UV box (P + Q - R, R being the right-angle vertex).
    /// Floors map stored TexCoordJ to vertex Pj; ceilings map it to vertex P(2 - J) (see TryComputeTriangleTexCoords).
    /// </summary>
    private static void FillMissingSplitTriangles(Room room)
    {
        for (int x = 0; x < room.NumXSectors; x++)
            for (int z = 0; z < room.NumZSectors; z++)
            {
                var sector = room.Sectors[x, z];
                foreach (var (first, second) in new[] { (SectorFace.Floor, SectorFace.Floor_Triangle2), (SectorFace.Ceiling, SectorFace.Ceiling_Triangle2) })
                {
                    bool reversed = first == SectorFace.Ceiling;
                    if (!room.IsFaceDefined(x, z, first) || !room.IsFaceDefined(x, z, second)) continue;
                    if (room.GetFaceShape(x, z, first) != FaceShape.Triangle || room.GetFaceShape(x, z, second) != FaceShape.Triangle) continue;
                    var texA = sector.GetFaceTexture(first);
                    var texB = sector.GetFaceTexture(second);
                    SectorFace donor = first, target = second;
                    if (texA.TextureIsUnavailable == texB.TextureIsUnavailable) continue; // both or neither textured
                    if (texA.TextureIsUnavailable) { donor = second; target = first; }
                    var donorTex = sector.GetFaceTexture(donor);

                    if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, donor), out var rd) || rd.Count != 3) continue;
                    if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, target), out var rt) || rt.Count != 3) continue;

                    var stored = new[] { donorTex.TexCoord0, donorTex.TexCoord1, donorTex.TexCoord2 };
                    var donorUv = new Dictionary<(int, int), Vector2>();
                    for (int j = 0; j < 3; j++)
                    {
                        var p = room.RoomGeometry.VertexPositions[rd.Start + j];
                        donorUv[((int)Math.Round(p.X), (int)Math.Round(p.Z))] = stored[reversed ? 2 - j : j];
                    }
                    if (donorUv.Count != 3) continue;

                    // Right-angle vertex of the donor's UV triangle: shares one coordinate with each of the other two.
                    var uvs = donorUv.Values.ToArray();
                    int right = -1;
                    for (int i = 0; i < 3 && right < 0; i++)
                    {
                        var others = Enumerable.Range(0, 3).Where(o => o != i).Select(o => uvs[o]).ToArray();
                        bool sharesX = others.Any(o => Math.Abs(o.X - uvs[i].X) < 0.5f), sharesY = others.Any(o => Math.Abs(o.Y - uvs[i].Y) < 0.5f);
                        bool differentOnes = others.Count(o => Math.Abs(o.X - uvs[i].X) < 0.5f) == 1 && others.Count(o => Math.Abs(o.Y - uvs[i].Y) < 0.5f) == 1;
                        if (sharesX && sharesY && differentOnes) right = i;
                    }
                    if (right < 0) continue;
                    var fourth = uvs[(right + 1) % 3] + uvs[(right + 2) % 3] - uvs[right];

                    var targetUv = new Vector2[3];
                    bool ok = true;
                    for (int j = 0; j < 3; j++)
                    {
                        var p = room.RoomGeometry.VertexPositions[rt.Start + j];
                        var key = ((int)Math.Round(p.X), (int)Math.Round(p.Z));
                        targetUv[reversed ? 2 - j : j] = donorUv.TryGetValue(key, out var shared) ? shared : fourth;
                        if (!donorUv.ContainsKey(key) && j < 0) ok = false;
                    }
                    if (!ok) continue;

                    var filled = donorTex;
                    filled.TexCoord0 = targetUv[0];
                    filled.TexCoord1 = targetUv[1];
                    filled.TexCoord2 = targetUv[2];
                    filled.TexCoord3 = targetUv[2];
                    sector.SetFaceTexture(target, filled);
                }
            }
    }
    /// <summary>
    /// Splits a set of sector cells into disjoint rectangles that cover exactly those cells (DOCUMENTATION.md 2.32). The
    /// portal export used to add ONE portal covering the bounding box of all doors to the same room, which also covered
    /// sectors the TR4 never opened: TombLib then treated them as portal and built no floor/ceiling over them. A single
    /// rectangle comes back unchanged; otherwise rows of the grid are extended greedily (first along Z, then along X).
    /// </summary>
    private static List<(int X0, int Z0, int X1, int Z1)> DecomposeIntoRectangles(HashSet<(int X, int Z)> cells)
    {
        var result = new List<(int, int, int, int)>();
        var left = new HashSet<(int X, int Z)>(cells);
        while (left.Count > 0)
        {
            var (sx, sz) = left.OrderBy(c => c.X).ThenBy(c => c.Z).First();
            int ez = sz;
            while (left.Contains((sx, ez + 1))) ez++;
            int ex = sx;
            while (Enumerable.Range(sz, ez - sz + 1).All(z => left.Contains((ex + 1, z)))) ex++;
            for (int x = sx; x <= ex; x++)
                for (int z = sz; z <= ez; z++)
                    left.Remove((x, z));
            result.Add((sx, sz, ex, ez));
        }
        return result;
    }
    /// <summary>
    /// Sets SplitDirectionIsXEqualsZ of non-planar floor/ceiling sectors from the diagonal the compiled TR4 mesh really
    /// uses (DOCUMENTATION.md 2.28). The flag was only set when the sector carried a FloorData triangulation function, and
    /// the ceiling convention did not always agree with the compiled geometry. A horizontal sector with exactly two
    /// compiled triangles shares one diagonal: if the corner missing from the first triangle is XpZn or XnZp the diagonal
    /// joins XnZn and XpZp (x == z), otherwise it joins XpZn and XnZp. Sectors with a real DiagonalSplit, or without two
    /// consistent triangles, are left alone.
    /// </summary>
    private static void ApplyCompiledTriangleSplits(Room room, LevelRoom r1, PrjRoom pr)
    {
        var found = new Dictionary<(int X, int Z, bool IsFloor), List<int>>(); // missing-corner id per triangle: 0 XnZn, 1 XpZn, 2 XnZp, 3 XpZp
        foreach (var tf in r1.Triangles)
        {
            if (tf.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
            var vs = tf.Vertices.Select(v => r1.Vertices[v]).ToArray();
            int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
            if (maxX - minX < 1000 || maxZ - minZ < 1000) continue; // not a horizontal sector triangle
            int sx = (int)Math.Round(minX / 1024.0), sz = (int)Math.Round(minZ / 1024.0);
            if (sx < 0 || sz < 0 || sx >= room.NumXSectors || sz >= room.NumZSectors) continue;

            var corners = new HashSet<int>();
            foreach (var v in vs)
            {
                int cx = (int)Math.Round(v.X / 1024.0) - sx, cz = (int)Math.Round(v.Z / 1024.0) - sz;
                if (cx is < 0 or > 1 || cz is < 0 or > 1) { corners.Clear(); break; }
                corners.Add(cx + 2 * cz);
            }
            if (corners.Count != 3) continue;
            int missing = Enumerable.Range(0, 4).First(c => !corners.Contains(c));

            var s = room.Sectors[sx, sz];
            double avgH = vs.Average(v => -(double)v.Y) + r1.YBottom;
            double floorH = new[] { s.Floor.XnZn, s.Floor.XpZn, s.Floor.XnZp, s.Floor.XpZp }.Average();
            double ceilH = new[] { s.Ceiling.XnZn, s.Ceiling.XpZn, s.Ceiling.XnZp, s.Ceiling.XpZp }.Average();
            bool isFloor = Math.Abs(avgH - floorH) <= Math.Abs(avgH - ceilH);
            var key = (sx, sz, isFloor);
            if (!found.TryGetValue(key, out var list)) found[key] = list = new();
            list.Add(missing);
        }

        foreach (var ((x, z, isFloor), missing) in found)
        {
            if (missing.Count != 2) continue;
            // The two triangles of one diagonal miss the two corners that are NOT on it, i.e. opposite corners.
            if (missing[0] + missing[1] != 3) continue;
            var sector = room.Sectors[x, z];
            if ((isFloor ? sector.Floor.DiagonalSplit : sector.Ceiling.DiagonalSplit) != DiagonalSplit.None) continue;
            // Missing corners XpZn (1) and XnZp (2) -> diagonal XnZn-XpZp (x == z). SectorSurface is a struct, so write through the field.
            bool xEqualsZ = missing[0] == 1 || missing[0] == 2;
            if (isFloor) sector.Floor.SplitDirectionIsXEqualsZ = xEqualsZ;
            else sector.Ceiling.SplitDirectionIsXEqualsZ = xEqualsZ;
        }
    }
    /// <summary>
    /// Gives Wall/BorderWall sectors that sit next to a real sector the hidden floor/ceiling heights the TR4 mesh
    /// implies. TR4 has no heights for wall sectors, and the flat placeholder heights make TombLib build a single
    /// Middle face, so seams that carry 2-3+ stacked compiled quads lose all but one texture (DOCUMENTATION.md 2.23).
    /// QA [floor, WF] + Middle [WF, WC] + WS [WC, ceiling] always tile the real sector's full height, so the visible
    /// wall does not change; only where one texture band ends and the next begins. Boundaries come from the sorted
    /// quad stack: 2 quads -> WF = WC = their shared edge (QA + WS); 3+ quads -> WF = top of the first quad, WC = bottom
    /// of the last (QA + Middle + WS; at most 3 faces exist per seam). Validated against the hand-authored reference:
    /// on seams with 3 quads and a straight wall edge, the reference's stored WF/WC equal these boundaries in 235 of 248.
    /// A wall corner is shared by two seams; the first seam to claim it wins.
    /// </summary>
    private static void SynthesizeWallHeights(Room room, LevelRoom r1, PrjRoom pr)
    {
        var seams = new Dictionary<(int X, int Z, bool IsX), List<(int Lo, int Hi)>>();
        foreach (var face in r1.Rectangles.Concat(r1.Triangles))
        {
            var vs = face.Vertices.Where(v => v < r1.Vertices.Length).Select(v => r1.Vertices[v]).ToArray();
            if (vs.Length != face.Vertices.Length || vs.Length == 0) continue;
            int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X);
            int minY = vs.Min(v => (int)v.Y), maxY = vs.Max(v => (int)v.Y);
            int minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
            double avgX = vs.Average(v => (double)v.X), avgZ = vs.Average(v => (double)v.Z);
            bool xWall = Math.Abs(maxX - minX) <= 8, zWall = Math.Abs(maxZ - minZ) <= 8;
            if (!xWall && !zWall) continue;
            // Same frame as the sector heights written above: -TR Y + room YBottom.
            var span = (Lo: -maxY + r1.YBottom, Hi: -minY + r1.YBottom);
            void Add(int x, int z, bool isX)
            {
                if (!seams.TryGetValue((x, z, isX), out var list)) seams[(x, z, isX)] = list = new();
                list.Add(span);
            }
            if (xWall)
            {
                int sx = (int)Math.Round(avgX / 1024.0);
                if (sx <= 0 || sx >= pr.XSize) continue;
                for (int z = Math.Clamp(minZ / 1024, 0, pr.ZSize - 1); z <= Math.Clamp((maxZ - 1) / 1024, 0, pr.ZSize - 1); z++) Add(sx, z, true);
            }
            else
            {
                int sz = (int)Math.Round(avgZ / 1024.0);
                if (sz <= 0 || sz >= pr.ZSize) continue;
                for (int x = Math.Clamp(minX / 1024, 0, pr.XSize - 1); x <= Math.Clamp((maxX - 1) / 1024, 0, pr.XSize - 1); x++) Add(x, sz, false);
            }
        }

        bool IsWallBlock(int x, int z)
        {
            int id = pr.Blocks[x * pr.ZSize + z].Id;
            return id == 0x1E || id == 0x06 || id == 0x0E;
        }

        // wall sector -> corner values [XnZn, XpZn, XnZp, XpZp]
        var floorCorners = new Dictionary<(int, int), int?[]>();
        var ceilCorners = new Dictionary<(int, int), int?[]>();

        foreach (var ((ox, oz, isX), quads) in seams.OrderBy(k => k.Key.X).ThenBy(k => k.Key.Z).ThenBy(k => k.Key.IsX))
        {
            if (quads.Count < 2) continue;
            int nx = isX ? ox - 1 : ox, nz = isX ? oz : oz - 1;
            bool ownWall = IsWallBlock(ox, oz), neighborWall = IsWallBlock(nx, nz);
            if (ownWall == neighborWall) continue; // need exactly one wall sector and one real sector

            var sorted = quads.OrderBy(q => q.Lo).ThenBy(q => q.Hi).ToList();
            int wf = sorted[0].Hi;
            int wc = quads.Count == 2 ? wf : sorted[^1].Lo;
            if (wf > wc) (wf, wc) = (wc, wf);
            wf = (int)Math.Round(wf / 256.0) * 256;
            wc = (int)Math.Round(wc / 256.0) * 256;

            var wall = ownWall ? (ox, oz) : (nx, nz);
            int[] corners = isX
                ? (ownWall ? new[] { 0, 2 } : new[] { 1, 3 })   // wall's -X edge, or +X edge
                : (ownWall ? new[] { 0, 1 } : new[] { 2, 3 });  // wall's -Z edge, or +Z edge
            if (!floorCorners.TryGetValue(wall, out var fc)) floorCorners[wall] = fc = new int?[4];
            if (!ceilCorners.TryGetValue(wall, out var cc)) ceilCorners[wall] = cc = new int?[4];
            foreach (int c in corners)
            {
                if (fc[c] == null && cc[c] == null) { fc[c] = wf; cc[c] = wc; }
            }
        }

        foreach (var (wall, fc) in floorCorners)
        {
            var sector = room.Sectors[wall.Item1, wall.Item2];
            var cc = ceilCorners[wall];
            if (fc[0] is int a0) { sector.Floor.XnZn = (short)a0; sector.Ceiling.XnZn = (short)cc[0]!.Value; }
            if (fc[1] is int a1) { sector.Floor.XpZn = (short)a1; sector.Ceiling.XpZn = (short)cc[1]!.Value; }
            if (fc[2] is int a2) { sector.Floor.XnZp = (short)a2; sector.Ceiling.XnZp = (short)cc[2]!.Value; }
            if (fc[3] is int a3) { sector.Floor.XpZp = (short)a3; sector.Ceiling.XpZp = (short)cc[3]!.Value; }
        }
    }

    /// <summary>
    /// Converts raw tr4_room_light entries (TR world-coordinate convention) into TombLib
    /// LightInstance objects, inverting the exact formulas used by TombLib's own compiler
    /// (Compilers/Rooms.cs ConvertLights / BuildRoom) so a round-trip through TombLib reproduces
    /// the original TR4 light data.
    /// </summary>
    private static void ExportLights(Room room, LevelRoom r1, PrjRoom pr)
    {
        foreach (var l in r1.Lights)
        {
            LightType type = l.LightType switch
            {
                0 => LightType.Sun,
                1 => LightType.Point,
                2 => LightType.Spot,
                3 => LightType.Shadow,
                4 => LightType.FogBulb,
                _ => LightType.Point,
            };
            var light = new LightInstance(type)
            {
                // Position is room-relative in TombLib; world position (TR convention) is
                // RoomInfo.X/Z + Position.X/Z, and -(Position.Y + RoomWorldY) for Y.
                Position = new Vector3(l.X - r1.X, -l.Y - room.Position.Y, l.Z - r1.Z),
                Color = new Vector3(l.ColourR / 128.0f, l.ColourG / 128.0f, l.ColourB / 128.0f),
            };

            // Intensity: raw ushort = round(abs(floatIntensity) * 8191), sign restored for Shadow type
            // (TombLib negates Intensity for Shadow lights on load/construction).
            light.Intensity = l.Intensity / 8191.0f;
            if (type == LightType.Shadow) light.Intensity *= -1;

            switch (type)
            {
                case LightType.Point:
                case LightType.Shadow:
                    light.InnerRange = l.In / Level.SectorSizeUnit;
                    light.OuterRange = l.Out / Level.SectorSizeUnit;
                    break;
                case LightType.Spot:
                    light.InnerAngle = (float)(Math.Acos(Math.Clamp(l.In, -1.0, 1.0)) * (180.0 / Math.PI));
                    light.OuterAngle = (float)(Math.Acos(Math.Clamp(l.Out, -1.0, 1.0)) * (180.0 / Math.PI));
                    light.InnerRange = l.Length / Level.SectorSizeUnit;
                    light.OuterRange = l.CutOff / Level.SectorSizeUnit;
                    SetDirection(light, l.DirX, l.DirY, l.DirZ);
                    break;
                case LightType.Sun:
                    SetDirection(light, l.DirX, l.DirY, l.DirZ);
                    break;
                case LightType.FogBulb:
                    light.InnerRange = l.In / Level.SectorSizeUnit;
                    light.OuterRange = l.Out / Level.SectorSizeUnit;
                    light.Intensity = l.Length; // TR5-native storage; TR4 uses a color hack instead
                    break;
            }

            room.AddObject(room.Level, light);
        }
    }

    private static void SetDirection(IRotateableYX light, float dx, float dy, float dz)
    {
        // Inverse of GetDirection()/compiler's DirectionX=-dir.X, DirectionY=dir.Y, DirectionZ=-dir.Z
        // (light-specific encoding).
        ApplyDirection(light, -dx, dy, -dz);
    }

    private static void ApplyDirection(IRotateableYX obj, float dirX, float dirY, float dirZ)
    {
        float rx = (float)Math.Asin(Math.Clamp(dirY, -1.0, 1.0));
        float ry = (float)Math.Atan2(dirX, dirZ);
        obj.SetArbitaryRotationsYX(ry * (180.0f / (float)Math.PI), rx * (180.0f / (float)Math.PI));
    }

    /// <summary>
    /// Sound sources have no room field in the raw tr_sound_source struct; the containing room is
    /// found by testing the raw world position against each room's X/Z/Y bounds (matches the
    /// compiler's own room.WorldPos + instance.Position relationship, inverted).
    /// </summary>
    private static int FindContainingRoom(TrLevel trLevel, int x, int y, int z)
    {
        for (int i = 0; i < trLevel.Rooms.Length; i++)
        {
            var r1 = trLevel.Rooms[i];
            if (r1.NumX == 0 || r1.NumZ == 0) continue;
            if (x < r1.X || x >= r1.X + r1.NumX * 1024) continue;
            if (z < r1.Z || z >= r1.Z + r1.NumZ * 1024) continue;
            if (y < r1.YTop || y > r1.YBottom) continue;
            return i;
        }
        return -1;
    }

    private static void ExportSoundSources(TrLevel trLevel, Room?[] tombRooms, List<string> warnings)
    {
        foreach (var s in trLevel.SoundSources)
        {
            int roomIdx = FindContainingRoom(trLevel, s.X, s.Y, s.Z);
            if (roomIdx < 0 || tombRooms[roomIdx] == null)
            {
                warnings.Add($"Sound source (SoundID={s.SoundId}) at ({s.X},{s.Y},{s.Z}) skipped: no containing room found");
                continue;
            }
            var room = tombRooms[roomIdx]!;
            var r1 = trLevel.Rooms[roomIdx];
            var sound = new SoundSourceInstance
            {
                Position = new Vector3(s.X - r1.X, -s.Y - room.Position.Y, s.Z - r1.Z),
                SoundId = s.SoundId,
                // Flags 0xC0 covers both "Always" and "Automatic in a non-alternated room" (identical
                // encoding); 0x40/0x80 mark automatic play tied to a specific alternate-room state.
                // Empirically (verified against reference data) 0xC0 is used for Automatic in practice,
                // so default there rather than Always.
                PlayMode = s.Flags switch
                {
                    0x80 => SoundSourcePlayMode.OnlyInBaseRoom,
                    0x40 => SoundSourcePlayMode.OnlyInAlternateRoom,
                    _ => SoundSourcePlayMode.Automatic,
                },
            };
            room.AddObject(room.Level, sound);
        }
    }

    /// <summary>
    /// Sinks share the raw tr_camera array with Camera trigger targets; which indices are which is
    /// only knowable by scanning trigger ActionLists (done once during TrLevel.Load, see
    /// TrLevel.cs's Trigger FloorData handling). For entries classified as sinks, the raw "Room"
    /// field is repurposed by the TombLib compiler to hold Strength (not a room index) and "Flags"
    /// to hold a pathfinding box index -- see LevelCompilerClassicTR.cs's sink-writing code, which
    /// this inverts. The containing room itself must be found by position, same as sound sources.
    /// </summary>
    private static void ExportSinks(TrLevel trLevel, Room?[] tombRooms, List<string> warnings)
    {
        foreach (int idx in trLevel.SinkFloorDataIndices)
        {
            if (idx < 0 || idx >= trLevel.Cameras.Count)
            {
                warnings.Add($"Sink index {idx} out of range of the raw Cameras[] array ({trLevel.Cameras.Count} entries)");
                continue;
            }
            var c = trLevel.Cameras[idx];
            int roomIdx = FindContainingRoom(trLevel, c.X, c.Y, c.Z);
            if (roomIdx < 0 || tombRooms[roomIdx] == null)
            {
                warnings.Add($"Sink (index {idx}, strength {c.Room}) at ({c.X},{c.Y},{c.Z}) skipped: no containing room found");
                continue;
            }
            var room = tombRooms[roomIdx]!;
            var r1 = trLevel.Rooms[roomIdx];
            var sink = new SinkInstance
            {
                Position = new Vector3(c.X - r1.X, -c.Y - room.Position.Y, c.Z - r1.Z),
                // Empirically confirmed (Francy): raw Strength is stored 1-based, TombLib's is 0-based.
                Strength = (short)(c.Room - 1),
            };
            room.AddObject(room.Level, sink);
        }
    }

    /// <summary>
    /// Static (non-flyby) cameras, found via the CameraFloorDataIndices set collected while parsing
    /// Trigger FloorData (TrigAction 0x01) during TrLevel.Load. Unlike sinks, tr_camera's Room field
    /// is a genuine room index for camera entries, so no position-based search is needed.
    /// </summary>
    private static void ExportCameras(TrLevel trLevel, Room?[] tombRooms, List<string> warnings)
    {
        foreach (int idx in trLevel.CameraFloorDataIndices)
        {
            if (idx < 0 || idx >= trLevel.Cameras.Count)
            {
                warnings.Add($"Camera index {idx} out of range of the raw Cameras[] array ({trLevel.Cameras.Count} entries)");
                continue;
            }
            var c = trLevel.Cameras[idx];
            if (c.Room < 0 || c.Room >= tombRooms.Length || tombRooms[c.Room] == null)
            {
                warnings.Add($"Camera (index {idx}) at ({c.X},{c.Y},{c.Z}) skipped: invalid room {c.Room}");
                continue;
            }
            var room = tombRooms[c.Room]!;
            var r1 = trLevel.Rooms[c.Room];
            var camera = new CameraInstance
            {
                Position = new Vector3(c.X - r1.X, -c.Y - room.Position.Y, c.Z - r1.Z),
                // Flags: 0x3 (bits 0+1 both set) = Sniper; bit0 alone = Locked; bit2 = GlideOut.
                CameraMode = (c.Flags & 0x3) == 0x3 ? CameraInstanceMode.Sniper
                    : (c.Flags & 0x1) != 0 ? CameraInstanceMode.Locked
                    : CameraInstanceMode.Default,
                GlideOut = (c.Flags & 0x4) != 0,
            };
            room.AddObject(room.Level, camera);
        }
    }

    /// <summary>
    /// Flyby cameras, from the raw tr4_flyby_camera array. Unlike static cameras, Room is a genuine
    /// direct room index here too, so no position search is needed.
    /// </summary>
    private static void ExportFlybyCameras(TrLevel trLevel, Room?[] tombRooms, List<string> warnings)
    {
        foreach (var c in trLevel.FlybyCameras)
        {
            int roomIdx = (int)c.RoomId;
            if (roomIdx < 0 || roomIdx >= tombRooms.Length || tombRooms[roomIdx] == null)
            {
                warnings.Add($"Flyby camera (seq {c.Sequence}, idx {c.Index}) at ({c.X},{c.Y},{c.Z}) skipped: invalid room {roomIdx}");
                continue;
            }
            var room = tombRooms[roomIdx]!;
            var r1 = trLevel.Rooms[roomIdx];

            var position = new Vector3(c.X - r1.X, -c.Y - room.Position.Y, c.Z - r1.Z);
            // DirX/Y/Z encode a world-space "look at" point: position + SectorSizeUnit*direction (with
            // the same Y sign convention as the position fields). Invert to recover the direction.
            float dirX = (c.DirX - c.X) / (float)Level.SectorSizeUnit;
            float dirY = (c.Y - c.DirY) / (float)Level.SectorSizeUnit;
            float dirZ = (c.DirZ - c.Z) / (float)Level.SectorSizeUnit;

            var flyby = new FlybyCameraInstance
            {
                Position = position,
                Sequence = c.Sequence,
                Number = c.Index,
                Timer = (short)c.Timer,
                Flags = c.Flags,
                Fov = c.Fov * (360.0f / 65536.0f),
                Speed = c.Speed / 655.0f,
            };
            // Roll: encoded as rollTo65536 = (65536 - round(Roll*65536/360)) mod 65536, stored as a
            // reinterpreted (unchecked) int16. Invert by reading it back as unsigned first.
            ushort rollRaw = unchecked((ushort)c.Roll);
            int rollX = (65536 - rollRaw) % 65536;
            flyby.Roll = rollX * (360.0f / 65536.0f);
            ApplyDirection(flyby, dirX, dirY, dirZ);

            room.AddObject(room.Level, flyby);
        }
    }

    /// <summary>
    /// Assigns face textures for one room's sectors, reading our own classic-PRJ-model
    /// Block.Textures[] (already populated by TrLevel.ApplyRoomMeshTextures/ApplyWallFace) and
    /// writing via TombLib's modern Sector.SetFaceTexture. The slot -> SectorFace resolution
    /// (which of two adjacent sectors a wall texture "belongs" to) is ported from TombLib's own
    /// PrjLoader.cs (its "Texturize faces" loop in LoadFromPrj), which performs the identical
    /// classic-PRJ-slot-to-modern-SectorFace mapping for the same on-disk format.
    ///
    /// SIMPLIFIED relative to PrjLoader: PrjLoader also handles slots 10-13 (Floor2/Ceiling2 --
    /// the classic format's second floor/ceiling split tier, from NGLE's stacked-wall feature) and
    /// the IsUndefinedButHasArea disambiguation that goes with them. Our Pass-1 sector setup
    /// (Prj2Exporter.Export) never calls Sector.SetHeight(Floor2/Ceiling2, ...) for any sector --
    /// verified true for TR4 raw FloorData, which only ever encodes one floor split and one ceiling
    /// split per sector (see TrLevel.ApplyFloorData) -- so IsFaceDefined for every Floor2/Ceiling2
    /// SectorFace is always false here, and PrjLoader's corresponding branches collapse to their
    /// single non-Floor2/Ceiling2 case unconditionally. Slots 10-13 are therefore never read.
    /// </summary>
    private static void ApplyRoomFaceTextures(Room room, PrjRoom pr, LevelTexture levelTexture, TexInfo[] textures)
    {
        for (int x = 0; x < pr.XSize; x++)
        for (int z = 0; z < pr.ZSize; z++)
        {
            int b = x * pr.ZSize + z;
            if (b < 0 || b >= pr.Blocks.Length) continue;
            var block = pr.Blocks[b];

            LoadTextureArea(room, x, z, SectorFace.Floor, levelTexture, textures, block.Textures[0]);
            LoadTextureArea(room, x, z, SectorFace.Ceiling, levelTexture, textures, block.Textures[1]);
            LoadTextureArea(room, x, z, SectorFace.Floor_Triangle2, levelTexture, textures, block.Textures[8]);
            LoadTextureArea(room, x, z, SectorFace.Ceiling_Triangle2, levelTexture, textures, block.Textures[9]);

            // Slots 2-4 (-X QA/WS/Middle) and 5-7 (-Z QA/WS/Middle). See PlaceWallSeam.
            PlaceWallSeam(room, x, z, true, levelTexture, textures, block.Textures[2], block.Textures[3], block.Textures[4]);
            PlaceWallSeam(room, x, z, false, levelTexture, textures, block.Textures[5], block.Textures[6], block.Textures[7]);
        }
    }

    /// <summary>
    /// Places the QA/WS/Middle textures of one seam (own sector's -X or -Z side) on the faces that OUR
    /// exported geometry actually defines. Tier labels are inferred from the compiled TR4 mesh and can
    /// disagree with the tier our flattened Wall/BorderWall sector heights make TombLib define (e.g. a
    /// full-height wall is a single Middle face here, but WS in a hand-authored reference). A texture whose
    /// own tier face is not defined is moved to the nearest defined tier that has no texture of its own,
    /// instead of being dropped (DOCUMENTATION.md 2.22). Tier order bottom to top: QA, Middle, WS.
    /// Same-tier placement keeps PrjLoader's rule: own sector's Negative face if defined, else the
    /// neighbour's Positive face.
    /// </summary>
    private static void PlaceWallSeam(Room room, int x, int z, bool isX, LevelTexture levelTexture, TexInfo[] textures,
        BlockTex qaTex, BlockTex wsTex, BlockTex midTex)
    {
        int nx = isX ? x - 1 : x, nz = isX ? z : z - 1;
        bool hasNeighbor = nx >= 0 && nz >= 0;
        var neg = isX
            ? new[] { SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_NegativeX_WS }
            : new[] { SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_NegativeZ_WS };
        var pos = isX
            ? new[] { SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_PositiveX_WS }
            : new[] { SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_PositiveZ_Middle, SectorFace.Wall_PositiveZ_WS };
        var tex = new[] { qaTex, midTex, wsTex };

        var ownDefined = new bool[3];
        var defined = new bool[3];
        for (int t = 0; t < 3; t++)
        {
            ownDefined[t] = room.IsFaceDefined(x, z, neg[t]);
            defined[t] = ownDefined[t] || (hasNeighbor && room.IsFaceDefined(nx, nz, pos[t]));
        }

        void Put(int faceTier, BlockTex source)
        {
            if (ownDefined[faceTier]) LoadTextureArea(room, x, z, neg[faceTier], levelTexture, textures, source);
            else if (hasNeighbor) LoadTextureArea(room, nx, nz, pos[faceTier], levelTexture, textures, source);
        }

        var taken = new bool[3];
        for (int t = 0; t < 3; t++)
        {
            if (tex[t].Tipo != 0x0007 || !defined[t]) continue;
            Put(t, tex[t]);
            taken[t] = true;
        }
        for (int t = 0; t < 3; t++)
        {
            if (tex[t].Tipo != 0x0007 || defined[t]) continue;
            int best = -1;
            for (int u = 0; u < 3; u++)
            {
                if (!defined[u] || taken[u] || tex[u].Tipo == 0x0007) continue;
                if (best < 0 || Math.Abs(u - t) < Math.Abs(best - t)) best = u;
            }
            if (best >= 0) { Put(best, tex[t]); taken[best] = true; }
            else Put(t, tex[t]); // nothing to move it to: previous behaviour
        }
    }
    /// <summary>
    /// Direct TexCoord derivation for a single-sector floor/ceiling QUAD (DOCUMENTATION.md 2.31), replacing the
    /// rotation/mirror arithmetic. RoomGeometry.AddQuad gives the face's TexCoord((j + 1) mod 4) to corner pj, and stores the
    /// six vertices as p1, p2, p0, p3, p0, p2 (floor); for ceilings it then swaps the first and last vertex of each triangle,
    /// leaving p0, p2, p1, p2, p0, p3. The corner positions are read from there, each is matched by (X, Z) to the compiled TR4
    /// quad's vertex, and its raw UV (>> 8) is classified onto a texture-box corner (0=TL, 1=TR, 2=BR, 3=BL). Returns false
    /// (callers keep the old arithmetic) for a missing source, a quad that does not span exactly this sector, an unmatched
    /// corner or UVs that are not on four distinct box corners.
    /// </summary>
    private static bool TryComputeFloorCeilingQuadTexCoords(Room room, int x, int z, SectorFace face, BlockTex blockTex, Vector2[] box, out Vector2[] texCoords)
    {
        texCoords = new Vector2[4];
        var source = blockTex.SourceFace;
        var texture = blockTex.SourceTexture;
        var owner = source?.Owner;
        if (source == null || texture == null || owner == null || source.IsTriangle || source.Vertices.Length != 4) return false;
        if (source.Vertices.Any(v => v >= owner.Vertices.Length)) return false;
        if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, face), out var range) || range.Count != 6) return false;

        var raw = source.Vertices.Select(v => owner.Vertices[v]).ToArray();
        if (raw.Max(v => (int)v.X) - raw.Min(v => (int)v.X) != 1024 || raw.Max(v => (int)v.Z) - raw.Min(v => (int)v.Z) != 1024) return false;

        int[] offsets = face == SectorFace.Ceiling ? new[] { 0, 2, 1, 5 } : new[] { 2, 0, 1, 3 }; // vertex slots of p0, p1, p2, p3
        var uvs = new (int U, int V)[4];
        var used = new HashSet<int>();
        for (int j = 0; j < 4; j++)
        {
            var p = room.RoomGeometry.VertexPositions[range.Start + offsets[j]];
            int match = -1;
            for (int i = 0; i < 4; i++)
            {
                if (Math.Abs(raw[i].X - p.X) > 8 || Math.Abs(raw[i].Z - p.Z) > 8) continue;
                if (match >= 0) return false;
                match = i;
            }
            if (match < 0 || !used.Add(match)) return false;
            uvs[j] = (texture.Vertices[match].X >> 8, texture.Vertices[match].Y >> 8);
        }

        int minU = uvs.Min(c => c.U), maxU = uvs.Max(c => c.U), minV = uvs.Min(c => c.V), maxV = uvs.Max(c => c.V);
        if (minU == maxU || minV == maxV) return false;
        var corner = new int[4];
        for (int j = 0; j < 4; j++)
        {
            bool atMaxU = Math.Abs(uvs[j].U - maxU) < Math.Abs(uvs[j].U - minU);
            bool atMaxV = Math.Abs(uvs[j].V - maxV) < Math.Abs(uvs[j].V - minV);
            corner[j] = (atMaxU, atMaxV) switch { (false, false) => 0, (true, false) => 1, (true, true) => 2, (false, true) => 3 };
        }
        if (corner.Distinct().Count() != 4) return false;

        for (int k = 0; k < 4; k++) texCoords[k] = box[corner[(k + 3) % 4]]; // TexCoordK belongs to corner p((K + 3) mod 4)
        return true;
    }
    /// <summary>
    /// Direct TexCoord derivation for a triangular floor/ceiling face (DOCUMENTATION.md 2.27), replacing the
    /// Triangle index / split-direction / rotation arithmetic of the decode below. TombLib's AddTriangle gives
    /// vertex Pj the face's TexCoordJ, and the three vertices come from RoomGeometry in order. Each one is matched
    /// by its (X, Z) corner to the compiled TR4 triangle's vertex, whose raw UV (>> 8) lands on one of the texture
    /// box's corners (0=TL, 1=TR, 2=BR, 3=BL); TexCoordJ is that box corner. Returns false (callers keep the old
    /// decode) for a missing source, a source that is not a triangle, an unmatched vertex or UVs that do not sit on
    /// three distinct box corners.
    /// </summary>
    private static bool TryComputeTriangleTexCoords(Room room, int x, int z, SectorFace face, BlockTex blockTex, Vector2[] box, out Vector2[] texCoords)
    {
        texCoords = new Vector2[3];
        var source = blockTex.SourceFace;
        var texture = blockTex.SourceTexture;
        var owner = source?.Owner;
        if (source == null || texture == null || owner == null || !source.IsTriangle || source.Vertices.Length != 3) return false;
        if (source.Vertices.Any(v => v >= owner.Vertices.Length)) return false;
        if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, face), out var range) || range.Count != 3) return false;

        var raw = source.Vertices.Select(v => owner.Vertices[v]).ToArray();
        var uvs = new (int U, int V)[3];
        var used = new HashSet<int>();
        for (int j = 0; j < 3; j++)
        {
            var p = room.RoomGeometry.VertexPositions[range.Start + j];
            // Floor/ceiling corners are told apart by (X, Z); a wall triangle has vertices that share (X, Z) and differ in Y, so those
            // are matched in 3D (TR4 Y grows downward, TombLib's room-relative Y grows upward).
            int match = -1;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(raw[i].X - p.X) > 8 || Math.Abs(raw[i].Z - p.Z) > 8) continue;
                bool sameXZAsAnother = Enumerable.Range(0, 3).Any(o => o != i && Math.Abs(raw[o].X - raw[i].X) <= 8 && Math.Abs(raw[o].Z - raw[i].Z) <= 8);
                if (sameXZAsAnother && Math.Abs(-raw[i].Y - (p.Y + room.Position.Y)) > 16) continue;
                if (match >= 0) return false; // ambiguous
                match = i;
            }
            if (match < 0 || !used.Add(match)) return false;
            uvs[j] = (texture.Vertices[match].X >> 8, texture.Vertices[match].Y >> 8);
        }

        int minU = uvs.Min(c => c.U), maxU = uvs.Max(c => c.U), minV = uvs.Min(c => c.V), maxV = uvs.Max(c => c.V);
        if (minU == maxU || minV == maxV) return false;

        var corner = new int[3];
        for (int j = 0; j < 3; j++)
        {
            bool atMaxU = Math.Abs(uvs[j].U - maxU) < Math.Abs(uvs[j].U - minU);
            bool atMaxV = Math.Abs(uvs[j].V - maxV) < Math.Abs(uvs[j].V - minV);
            corner[j] = (atMaxU, atMaxV) switch { (false, false) => 0, (true, false) => 1, (true, true) => 2, (false, true) => 3 };
        }
        if (corner.Distinct().Count() != 3) return false;

        // Ceilings: RoomGeometry reverses the vertex order (and TexCoord0<->2) after building, and the compiler swaps it back
        // with Mirror(true), so the stored TexCoordJ belongs to the geometry vertex 2 - J.
        bool reversed = face == SectorFace.Ceiling || face == SectorFace.Ceiling_Triangle2;
        for (int j = 0; j < 3; j++) texCoords[j] = box[corner[reversed ? 2 - j : j]];
        return true;
    }
    /// <summary>
    /// A wall face that TombLib builds as a QUAD but the TR4 holds as a TRIANGLE (two coincident corners because one end of the wall has zero
    /// height, or one half of a non-planar quad) had no four-corner source for the quad orientation arithmetic, which left rotation 0 (DOCUMENTATION.md 2.34). The four
    /// corner positions come from RoomGeometry (p1, p2, p0, p3, p0, p2), each is matched in 3D to a vertex of the compiled triangle (two
    /// coincident corners match the same vertex), its raw UV (>> 8) is classified onto a box corner of the triangle's right-angle UV box, and
    /// TexCoordK takes the box corner of corner p((K + 3) mod 4), as for floor/ceiling quads. Returns false for anything that does not fit.
    /// </summary>
    private static bool TryComputeCollapsedWallQuadTexCoords(Room room, int x, int z, SectorFace face, BlockTex blockTex, Vector2[] box, out Vector2[] texCoords)
    {
        texCoords = new Vector2[4];
        var source = blockTex.SourceFace;
        var texture = blockTex.SourceTexture;
        var owner = source?.Owner;
        if (source == null || texture == null || owner == null || !source.IsTriangle || source.Vertices.Length != 3) return false;
        if (source.Vertices.Any(v => v >= owner.Vertices.Length)) return false;
        if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, face), out var range) || range.Count != 6) return false;

        var raw = source.Vertices.Select(v => owner.Vertices[v]).ToArray();
        int[] slots = { 2, 0, 1, 3 }; // vertex slots of p0, p1, p2, p3
        var match = new int[4];
        for (int j = 0; j < 4; j++)
        {
            var p = room.RoomGeometry.VertexPositions[range.Start + slots[j]];
            int found = -1;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(raw[i].X - p.X) > 8 || Math.Abs(raw[i].Z - p.Z) > 8) continue;
                if (Math.Abs(-raw[i].Y - (p.Y + room.Position.Y)) > 16) continue;
                if (found >= 0) return false;
                found = i;
            }
            match[j] = found; // -1: corner is not a vertex of this triangle
        }
        // Two shapes fit: a COLLAPSED quad (every corner matched, one triangle vertex used twice), or one half of a non-planar wall quad (three corners
        // matched, the fourth takes the unused corner of the texture box, as in FillMissingSplitTriangles).
        int unmatched = match.Count(m => m < 0);
        if (match.Where(m => m >= 0).Distinct().Count() != 3 || unmatched > 1) return false;

        var uvs = Enumerable.Range(0, 3).Select(i => (U: texture.Vertices[i].X >> 8, V: texture.Vertices[i].Y >> 8)).ToArray();
        int minU = uvs.Min(c => c.U), maxU = uvs.Max(c => c.U), minV = uvs.Min(c => c.V), maxV = uvs.Max(c => c.V);
        if (minU == maxU || minV == maxV) return false;
        var corner = new int[3];
        for (int i = 0; i < 3; i++)
        {
            bool atMaxU = Math.Abs(uvs[i].U - maxU) < Math.Abs(uvs[i].U - minU);
            bool atMaxV = Math.Abs(uvs[i].V - maxV) < Math.Abs(uvs[i].V - minV);
            corner[i] = (atMaxU, atMaxV) switch { (false, false) => 0, (true, false) => 1, (true, true) => 2, (false, true) => 3 };
        }
        if (corner.Distinct().Count() != 3) return false;

        int unusedCorner = Enumerable.Range(0, 4).First(c => !corner.Contains(c));
        for (int k = 0; k < 4; k++)
        {
            int m = match[(k + 3) % 4];
            texCoords[k] = box[m < 0 ? unusedCorner : corner[m]];
        }
        return true;
    }
    /// <summary>
    /// Derives the rotation and mirror a wall QUAD needs so that each of TombLib's four face corners shows the
    /// same texture corner as the compiled TR4 face does (DOCUMENTATION.md 2.26). TombLib builds a wall quad as
    /// P0 = top at the wall's start, P1 = top at its end, P2 = bottom at its end, P3 = bottom at its start, and the
    /// decode in <see cref="LoadTextureArea"/> puts texture-box corner (j - rotation) mod 4 on Pj (corner order
    /// 0=TL,1=TR,2=BR,3=BL; with a mirror the box index is XOR 1). The start/end side depends on the face's
    /// direction: PositiveX z..z+1, NegativeX z+1..z, PositiveZ x+1..x, NegativeZ x..x+1. Returns false (callers keep
    /// the old rotation 0 / no mirror) for a missing source, a non-quad face or a corner pattern that is neither a
    /// pure rotation nor a pure mirror.
    /// </summary>
    private static bool TryComputeWallQuadOrientation(BlockTex blockTex, SectorFace face, out byte rotation, out bool flip)
    {
        rotation = 0;
        flip = false;
        var source = blockTex.SourceFace;
        var texture = blockTex.SourceTexture;
        var owner = source?.Owner;
        if (source == null || texture == null || owner == null || source.IsTriangle || source.Vertices.Length != 4) return false;
        if (source.Vertices.Any(v => v >= owner.Vertices.Length)) return false;

        string name = face.ToString();
        bool isXWall = name.Contains("X_");
        bool startIsMin = name.StartsWith("Wall_PositiveX") || name.StartsWith("Wall_NegativeZ");

        var vs = source.Vertices.Select(v => owner.Vertices[v]).ToArray();
        int Along(RoomVertex v) => isXWall ? v.Z : v.X;
        int minAlong = vs.Min(Along), maxAlong = vs.Max(Along);
        if (minAlong == maxAlong) return false;
        int startAlong = startIsMin ? minAlong : maxAlong, endAlong = startIsMin ? maxAlong : minAlong;

        // Corner index (into source.Vertices) of the top / bottom vertex at one end of the wall. TR Y grows downward.
        int[]? EndCorners(int along)
        {
            var idx = Enumerable.Range(0, 4).Where(i => Math.Abs(Along(vs[i]) - along) <= 8).ToArray();
            if (idx.Length != 2 || vs[idx[0]].Y == vs[idx[1]].Y) return null;
            return vs[idx[0]].Y < vs[idx[1]].Y ? new[] { idx[0], idx[1] } : new[] { idx[1], idx[0] }; // [top, bottom]
        }
        var start = EndCorners(startAlong);
        var end = EndCorners(endAlong);
        if (start == null || end == null) return false;

        int[] corner = { start[0], end[0], end[1], start[1] }; // P0..P3
        if (corner.Distinct().Count() != 4) return false;

        var uvs = corner.Select(i => ((int)(texture.Vertices[i].X >> 8), (int)(texture.Vertices[i].Y >> 8))).ToArray();
        int minU = uvs.Min(c => c.Item1), maxU = uvs.Max(c => c.Item1), minV = uvs.Min(c => c.Item2), maxV = uvs.Max(c => c.Item2);
        if (minU == maxU || minV == maxV) return false;

        int Box((int u, int v) c)
        {
            bool atMaxU = Math.Abs(c.u - maxU) < Math.Abs(c.u - minU);
            bool atMaxV = Math.Abs(c.v - maxV) < Math.Abs(c.v - minV);
            return (atMaxU, atMaxV) switch { (false, false) => 0, (true, false) => 1, (true, true) => 2, (false, true) => 3 };
        }
        var b = uvs.Select(Box).ToArray();

        int r = (4 - b[0]) % 4; // unmirrored: b[j] == (j - r) mod 4
        if (Enumerable.Range(0, 4).All(j => b[j] == ((j - r) % 4 + 4) % 4))
        {
            rotation = (byte)r;
            flip = false;
            return true;
        }

        r = (4 - (b[0] ^ 1)) % 4; // mirrored: b[j] == ((j - r) mod 4) ^ 1
        if (Enumerable.Range(0, 4).All(j => b[j] == ((((j - r) % 4 + 4) % 4) ^ 1)))
        {
            rotation = (byte)r;
            flip = true;
            return true;
        }

        return false;
    }
    /// <summary>
    /// Builds a TextureArea from one classic-PRJ BlockTex slot and writes it via
    /// Sector.SetFaceTexture. UV construction, rotation handling, triangle-corner selection and
    /// flip/blend-mode flags are ported verbatim from TombLib's PrjLoader.LoadTextureArea (its
    /// TYPE_TEXTURE_TILE case) -- BlockTex's fields (Tipo/Index/Flags1/Rotation/Triangle) are a
    /// direct 1:1 match to PrjLoader's own on-disk PrjFace fields
    /// (_txtType/_txtIndex/_txtFlags/_txtRotation/_txtTriangle), and our TexInfo (X/Y/Right/Bottom)
    /// matches its PrjTexInfo (_x/_y/_width/_height) the same way -- both are the same classic-PRJ
    /// on-disk texture-table record, just already parsed into our own model by TrLevel/TrProject.
    /// texStartCoord is fixed at 0 here (PrjLoader's adjustUV=false path): we always want
    /// uncropped/exact UVs, matching a plain re-import with half-pixel correction turned off.
    /// </summary>
    private static void LoadTextureArea(Room room, int x, int z, SectorFace face, LevelTexture levelTexture,
        TexInfo[] textures, BlockTex blockTex)
    {
        const float texStartCoord = 0.0f;
        Sector sector = room.Sectors[x, z];

        if (blockTex.Tipo != 0x0007) return; // not TYPE_TEXTURE_TILE: nothing was assigned here, leave undefined

        int texIndex = blockTex.Index; // BlockTex.Index is now a full int (no 10-bit Flags1 packing).
        if (texIndex < 0 || texIndex >= textures.Length) return;

        TexInfo texInfo = textures[texIndex];

        var uv = new[]
        {
            new Vector2(texInfo.X + texStartCoord, texInfo.Y + texStartCoord),
            new Vector2(texInfo.X + texInfo.Right + (1.0f - texStartCoord), texInfo.Y + texStartCoord),
            new Vector2(texInfo.X + texInfo.Right + (1.0f - texStartCoord), texInfo.Y + texInfo.Bottom + (1.0f - texStartCoord)),
            new Vector2(texInfo.X + texStartCoord, texInfo.Y + texInfo.Bottom + (1.0f - texStartCoord)),
        };

        var boxUv = (Vector2[])uv.Clone(); // texture-box corners TL, TR, BR, BL before any flip/rotation

        var texture = new TextureArea
        {
            Texture = levelTexture,
            DoubleSided = (blockTex.Flags1 & 0x04) != 0,
            BlendMode = (blockTex.Flags1 & 0x08) != 0 ? BlendMode.Additive : BlendMode.Normal,
        };

        bool flipUv = (blockTex.Flags1 & 0x80) != 0;
        ushort rotation = blockTex.Rotation;
        if (face.ToString().StartsWith("Wall_") && room.GetFaceShape(x, z, face) != FaceShape.Triangle
            && TryComputeWallQuadOrientation(blockTex, face, out byte wallRotation, out bool wallFlip))
        {
            rotation = wallRotation;
            flipUv = wallFlip;
        }

        // Apply flipping.
        if (flipUv)
        {
            (uv[0], uv[1]) = (uv[1], uv[0]);
            (uv[2], uv[3]) = (uv[3], uv[2]);
        }

        if (face.ToString().StartsWith("Wall_") && room.GetFaceShape(x, z, face) != FaceShape.Triangle && blockTex.SourceFace is { IsTriangle: true }
            && TryComputeCollapsedWallQuadTexCoords(room, x, z, face, blockTex, boxUv, out var collapsedUv))
        {
            texture.TexCoord0 = collapsedUv[0];
            texture.TexCoord1 = collapsedUv[1];
            texture.TexCoord2 = collapsedUv[2];
            texture.TexCoord3 = collapsedUv[3];
            sector.SetFaceTexture(face, texture);
            return;
        }

        if ((face == SectorFace.Floor || face == SectorFace.Ceiling) && room.GetFaceShape(x, z, face) != FaceShape.Triangle
            && TryComputeFloorCeilingQuadTexCoords(room, x, z, face, blockTex, boxUv, out var quadUv))
        {
            texture.TexCoord0 = quadUv[0];
            texture.TexCoord1 = quadUv[1];
            texture.TexCoord2 = quadUv[2];
            texture.TexCoord3 = quadUv[3];
            sector.SetFaceTexture(face, texture);
            return;
        }

        if (room.GetFaceShape(x, z, face) == FaceShape.Triangle)
        {
            if (TryComputeTriangleTexCoords(room, x, z, face, blockTex, boxUv, out var triUv))
            {
                texture.TexCoord0 = triUv[0];
                texture.TexCoord1 = triUv[1];
                texture.TexCoord2 = triUv[2];
                texture.TexCoord3 = triUv[2];
                sector.SetFaceTexture(face, texture);
                return;
            }

            switch (blockTex.Triangle)
            {
                case 0: texture.TexCoord0 = uv[0]; texture.TexCoord1 = uv[1]; texture.TexCoord2 = uv[3]; break;
                case 1: texture.TexCoord0 = uv[1]; texture.TexCoord1 = uv[2]; texture.TexCoord2 = uv[0]; break;
                case 2: texture.TexCoord0 = uv[2]; texture.TexCoord1 = uv[3]; texture.TexCoord2 = uv[1]; break;
                case 3: texture.TexCoord0 = uv[3]; texture.TexCoord1 = uv[0]; texture.TexCoord2 = uv[2]; break;
                default:
                    sector.SetFaceTexture(face, new TextureArea());
                    return;
            }

            if (face == SectorFace.Floor)
            {
                rotation += sector.Floor.SplitDirectionIsXEqualsZ ? (byte)1 : (byte)2;
            }
            else if (face == SectorFace.Ceiling)
            {
                (texture.TexCoord0, texture.TexCoord2) = (texture.TexCoord2, texture.TexCoord0);
                rotation += sector.Ceiling.SplitDirectionIsXEqualsZ ? (byte)2 : (byte)1;
                rotation = (ushort)(3000 - rotation);
            }
            else if (face == SectorFace.Ceiling_Triangle2)
            {
                (texture.TexCoord0, texture.TexCoord2) = (texture.TexCoord2, texture.TexCoord0);
                rotation = (ushort)(3000 - rotation);
            }

            rotation %= 3;
            for (int rot = 0; rot < rotation; rot++)
                (texture.TexCoord2, texture.TexCoord1, texture.TexCoord0) = (texture.TexCoord1, texture.TexCoord0, texture.TexCoord2);

            texture.TexCoord3 = texture.TexCoord2;
        }
        else
        {
            if (face == SectorFace.Floor || face == SectorFace.Floor_Triangle2)
                rotation += 2;

            rotation %= 4;
            for (int rot = 0; rot < rotation; rot++)
                (uv[3], uv[2], uv[1], uv[0]) = (uv[2], uv[1], uv[0], uv[3]);

            if (face == SectorFace.Ceiling || face == SectorFace.Ceiling_Triangle2)
            {
                texture.TexCoord0 = uv[2]; texture.TexCoord1 = uv[1]; texture.TexCoord2 = uv[0]; texture.TexCoord3 = uv[3];
            }
            else
            {
                texture.TexCoord0 = uv[3]; texture.TexCoord1 = uv[0]; texture.TexCoord2 = uv[1]; texture.TexCoord3 = uv[2];
            }
        }

        sector.SetFaceTexture(face, texture);
    }
}
