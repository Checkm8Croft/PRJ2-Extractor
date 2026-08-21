using System.Numerics;
using System.IO;
using PRJ2_Extractor.Models;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
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
                    // nel formato PRJ classico l'ordine degli angoli del soffitto è invertito
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
                // TombLib's Room.AddObject auto-creates the mirrored portal in the adjoining room, so
                // adding it again from that room's own (independent, TR4-sourced) door list would
                // always conflict with the auto-created copy. Process each room pair once, from the
                // lower-indexed room only; this was the dominant cause of "Portal overlaps another".
                if (key.Target < i) continue;

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
            }
        }

        Prj2Writer.SaveToPrj2(prj2FilePath, level);
        return warnings;
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

            // Slot 2 (North/-X QA): own -X side if it has real geometry there, else the
            // neighbour's +X side (PrjLoader: "case 10/2" collapsed -- Floor2 branch never taken).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeX_QA))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeX_QA, levelTexture, textures, block.Textures[2]);
            else if (x > 0)
                LoadTextureArea(room, x - 1, z, SectorFace.Wall_PositiveX_QA, levelTexture, textures, block.Textures[2]);

            // Slot 3 (North/-X WS).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeX_WS))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeX_WS, levelTexture, textures, block.Textures[3]);
            else if (x > 0)
                LoadTextureArea(room, x - 1, z, SectorFace.Wall_PositiveX_WS, levelTexture, textures, block.Textures[3]);

            // Slot 4 (North/-X Middle).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeX_Middle))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeX_Middle, levelTexture, textures, block.Textures[4]);
            else if (x > 0)
                LoadTextureArea(room, x - 1, z, SectorFace.Wall_PositiveX_Middle, levelTexture, textures, block.Textures[4]);

            // Slot 5 (West/-Z QA).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeZ_QA))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeZ_QA, levelTexture, textures, block.Textures[5]);
            else if (z > 0)
                LoadTextureArea(room, x, z - 1, SectorFace.Wall_PositiveZ_QA, levelTexture, textures, block.Textures[5]);

            // Slot 6 (West/-Z WS).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeZ_WS))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeZ_WS, levelTexture, textures, block.Textures[6]);
            else if (z > 0)
                LoadTextureArea(room, x, z - 1, SectorFace.Wall_PositiveZ_WS, levelTexture, textures, block.Textures[6]);

            // Slot 7 (West/-Z Middle).
            if (room.IsFaceDefined(x, z, SectorFace.Wall_NegativeZ_Middle))
                LoadTextureArea(room, x, z, SectorFace.Wall_NegativeZ_Middle, levelTexture, textures, block.Textures[7]);
            else if (z > 0)
                LoadTextureArea(room, x, z - 1, SectorFace.Wall_PositiveZ_Middle, levelTexture, textures, block.Textures[7]);
        }
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

        int texIndex = ((blockTex.Flags1 & 0x03) << 8) | blockTex.Index;
        if (texIndex < 0 || texIndex >= textures.Length) return;

        TexInfo texInfo = textures[texIndex];

        var uv = new[]
        {
            new Vector2(texInfo.X + texStartCoord, texInfo.Y + texStartCoord),
            new Vector2(texInfo.X + texInfo.Right + (1.0f - texStartCoord), texInfo.Y + texStartCoord),
            new Vector2(texInfo.X + texInfo.Right + (1.0f - texStartCoord), texInfo.Y + texInfo.Bottom + (1.0f - texStartCoord)),
            new Vector2(texInfo.X + texStartCoord, texInfo.Y + texInfo.Bottom + (1.0f - texStartCoord)),
        };

        var texture = new TextureArea
        {
            Texture = levelTexture,
            DoubleSided = (blockTex.Flags1 & 0x04) != 0,
            BlendMode = (blockTex.Flags1 & 0x08) != 0 ? BlendMode.Additive : BlendMode.Normal,
        };

        // Apply flipping.
        if ((blockTex.Flags1 & 0x80) != 0)
        {
            (uv[0], uv[1]) = (uv[1], uv[0]);
            (uv[2], uv[3]) = (uv[3], uv[2]);
        }

        ushort rotation = blockTex.Rotation;
        if (room.GetFaceShape(x, z, face) == FaceShape.Triangle)
        {
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
