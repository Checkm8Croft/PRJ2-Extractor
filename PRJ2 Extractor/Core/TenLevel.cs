using System.IO;
using System.Numerics;
using K4os.Compression.LZ4;

namespace PRJ2_Extractor.Core;

// ------------------------------------------------------------------------------------------
// Reader for compiled TombEngine levels (.ten). Layout follows TombEngine's Specific/level.cpp
// (LoadLevel): 16-byte header ("TEN", version[4], system hash, level hash) and three blocks
// (media, geometry, dynamic data), each [int64 uncompressed][int64 compressed][LZ4 chunks].
// Strings are LEB128-prefixed (BinaryReader.ReadString compatible).
// ------------------------------------------------------------------------------------------

public sealed class TenPolygon
{
    public int Shape;                 // 0 = quad, otherwise triangle
    public int AnimatedSequence, AnimatedFrame;
    public Vector3 Normal;
    public int[] Indices = [];
    public Vector2[] Uv = [];
}

public sealed class TenBucket
{
    public int Texture;
    public byte BlendMode;
    public int Material;
    public bool Animated;
    public List<TenPolygon> Polygons = [];
}

public sealed class TenPortal
{
    public short RoomNumber;
    public int NormalX, NormalY, NormalZ;
    public int[] Vertices = new int[12];   // 4 corners x (x, y, z)
}

/// <summary>Sector data as stored by the compiler. Planes are "fake planes": x, y = X / Z tilt per block, z = height at the sector centre.</summary>
public sealed class TenSector
{
    public int TriggerIndex, PathfindingBoxId, Material;
    public bool Stopper;
    public float FloorSplitAngle, CeilingSplitAngle;
    public int[] FloorPortal = new int[2], CeilingPortal = new int[2];
    public Vector3[] FloorPlane = new Vector3[2], CeilingPlane = new Vector3[2];
    public int SidePortal;
    public bool Death, Monkeyswing, ClimbNorth, ClimbSouth, ClimbEast, ClimbWest, MarkTriggerer, MarkBeetle;
}

public sealed class TenLight
{
    public int X, Y, Z;
    public float Dx, Dy, Dz, R, G, B, Intensity, In, Out, Length, Cutoff;
    public byte Type;
    public bool CastShadows;
}

public sealed class TenStatic
{
    public int X, Y, Z;
    public ushort RotY, RotX, RotZ;
    public float Scale;
    public ushort Flags;
    public Vector4 Color;
    public ushort Slot;
    public short HitPoints;
    public string Name = "";
}

public sealed class TenVolume
{
    public int Type;
    public Vector3 Position, Scale;
    public Vector4 Orientation;
    public bool Enabled, DetectInAdjacentRooms;
    public string Name = "";
    public int EventSetIndex;
}

public sealed class TenRoom
{
    // geometry block
    public int X, Z, BottomHeight, TopHeight;
    public Vector3[] Positions = [], Colors = [], Effects = [];
    public List<TenBucket> Buckets = [];
    public List<TenPortal> Portals = [];
    public int XSize, ZSize;
    public TenSector[] Sectors = [];          // index = x * ZSize + z
    public List<TenLight> Lights = [];
    // dynamic block
    public string Name = "";
    public List<string> Tags = [];
    public Vector3 Ambient;
    public int FlippedRoom = -1, Flags, MeshEffect, ReverbType, FlipNumber;
    public List<TenStatic> Statics = [];
    public List<TenVolume> Volumes = [];
}

public sealed class TenItem
{
    public short ObjectNumber, Room;
    public int X, Y, Z;
    public short RotY, RotX, RotZ;
    public Vector4 Color;
    public short TriggerFlags, Flags;
    public string Name = "";
}

public sealed class TenAiObject
{
    public short ObjectNumber, Room;
    public int X, Y, Z;
    public short RotY, RotX, RotZ, TriggerFlags, Flags;
    public int BoxNumber;
    public string Name = "";
}

public sealed class TenCamera
{
    public int X, Y, Z, Room, Flags, Speed;
    public string Name = "";
}

public sealed class TenFlybyCamera
{
    public int X, Y, Z, TargetX, TargetY, TargetZ, Sequence, Camera;
    public short Fov, Roll, Timer, Speed, Flags;
    public int Room, DofMode;
    public float DofDistance, DofRange, DofStrength;
}

public sealed class TenSink
{
    public int X, Y, Z, Strength, BoxIndex;
    public string Name = "";
}

public sealed class TenSoundSource
{
    public int X, Y, Z, SoundId, Flags;
    public string Name = "";
}

public sealed class TenEvent
{
    public int Type, Mode, CallCounter;
    public string Function = "", Data = "";
    public bool Enabled;
}

public sealed class TenEventSet
{
    public string Name = "";
    public int Activators;                 // volume event sets only
    public List<TenEvent> Events = [];
}

/// <summary>A room texture atlas as stored in the media block: PNG or DDS (BC1 / BC3) bytes of the colour map.</summary>
public sealed class TenAtlas
{
    public int Width, Height;
    public byte[] ColorMap = [];
}

public sealed class TenLevel
{
    public byte[] Version = new byte[4];
    public int SystemHash, LevelHash;

    public List<TenRoom> Rooms = [];
    public short[] FloorData = [];
    public List<TenItem> Items = [];
    public List<TenAiObject> AiObjects = [];
    public List<TenCamera> Cameras = [];
    public List<TenFlybyCamera> FlybyCameras = [];
    public List<TenSink> Sinks = [];
    public List<TenSoundSource> SoundSources = [];
    public List<TenEventSet> GlobalEventSets = [];
    public List<TenEventSet> VolumeEventSets = [];
    public string Properties = "";

    /// <summary>Raw (decompressed) media block: texture atlases and samples. Not parsed yet.</summary>
    public byte[] MediaBlock = [];

    /// <summary>Room texture atlases (the first group of the media block); polygon UVs of rooms are relative to these.</summary>
    public List<TenAtlas> RoomAtlases = [];
    /// <summary>Animated texture atlases; buckets flagged as animated refer to these.</summary>
    public List<TenAtlas> AnimatedAtlases = [];

    public string VersionText => $"{Version[0]}.{Version[1]}.{Version[2]}";

    public static bool IsTenFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var h = new byte[4];
            return fs.Read(h, 0, 4) == 4 && h[0] == (byte)'T' && h[1] == (byte)'E' && h[2] == (byte)'N';
        }
        catch (IOException) { return false; }
    }

    /// <summary>Loads a .ten level. Returns null on success or an error message.</summary>
    public string? Load(string path, IProgress<int>? progress = null)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            var header = br.ReadBytes(4);
            if (header.Length < 4 || header[0] != 'T' || header[1] != 'E' || header[2] != 'N')
                return "Not a TombEngine level (header is not TEN).";
            Version = br.ReadBytes(4);
            SystemHash = br.ReadInt32();
            LevelHash = br.ReadInt32();

            progress?.Report(5);
            MediaBlock = ReadBlock(br);
            using (var media = new BinaryReader(new MemoryStream(MediaBlock)))
                ReadAtlases(media);
            progress?.Report(30);

            using (var geometry = new BinaryReader(new MemoryStream(ReadBlock(br))))
                ReadGeometry(geometry);
            progress?.Report(70);

            using (var dynamic = new BinaryReader(new MemoryStream(ReadBlock(br))))
                ReadDynamic(dynamic);
            progress?.Report(100);
            return null;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException or ArgumentException)
        {
            return ex.Message;
        }
    }

    // [int64 uncompressed][int64 compressed][numChunks u32 { [u32 uncompressed][u32 compressed][LZ4 block] }]
    private static byte[] ReadBlock(BinaryReader br)
    {
        long uncompressed = br.ReadInt64(), compressed = br.ReadInt64();
        if (uncompressed <= 0 || compressed <= 0 || compressed > br.BaseStream.Length - br.BaseStream.Position)
            throw new InvalidDataException("Data block size is incorrect (older level version?).");
        if (uncompressed > int.MaxValue) throw new InvalidDataException("Level data block exceeds 2 GB.");

        var region = br.ReadBytes((int)compressed);
        var result = new byte[uncompressed];
        int pos = 0, dest = 0;
        uint chunks = BitConverter.ToUInt32(region, pos); pos += 4;
        for (uint i = 0; i < chunks; i++)
        {
            int chunkUncompressed = (int)BitConverter.ToUInt32(region, pos); pos += 4;
            int chunkCompressed = (int)BitConverter.ToUInt32(region, pos); pos += 4;
            int n = LZ4Codec.Decode(region, pos, chunkCompressed, result, dest, chunkUncompressed);
            if (n != chunkUncompressed) throw new InvalidDataException("LZ4 decompression failed.");
            pos += chunkCompressed;
            dest += chunkUncompressed;
        }
        if (dest != uncompressed) throw new InvalidDataException("Decompressed size mismatch.");
        return result;
    }

    private static int Count(BinaryReader r, int max = 1024 * 1024)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > max) throw new InvalidDataException("Level data block has incorrect size (older level version?).");
        return n;
    }

    private static Vector2 V2(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle());
    private static Vector3 V3(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static Vector4 V4(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

    /// <summary>Mirrors LoadRooms(): static room data (LoadStaticRoomData) followed by the floordata. The rest of the block (moveable models, sprites, boxes, ...) is not needed.</summary>
    private void ReadGeometry(BinaryReader r)
    {
        int roomCount = Count(r);
        Rooms = new List<TenRoom>(roomCount);
        for (int i = 0; i < roomCount; i++)
        {
            var room = new TenRoom { X = r.ReadInt32() };
            room.Z = r.ReadInt32();
            room.BottomHeight = r.ReadInt32();
            room.TopHeight = r.ReadInt32();

            int vertexCount = Count(r, 1 << 30);
            room.Positions = new Vector3[vertexCount];
            room.Colors = new Vector3[vertexCount];
            room.Effects = new Vector3[vertexCount];
            for (int j = 0; j < vertexCount; j++) room.Positions[j] = V3(r);
            for (int j = 0; j < vertexCount; j++) room.Colors[j] = V3(r);
            for (int j = 0; j < vertexCount; j++) room.Effects[j] = V3(r);

            int bucketCount = Count(r);
            for (int b = 0; b < bucketCount; b++)
            {
                var bucket = new TenBucket { Texture = r.ReadInt32(), BlendMode = r.ReadByte(), Material = r.ReadInt32(), Animated = r.ReadBoolean() };
                int polyCount = Count(r, 1 << 30);
                bucket.Polygons.Capacity = polyCount;
                for (int k = 0; k < polyCount; k++)
                {
                    var poly = new TenPolygon { Shape = r.ReadInt32(), AnimatedSequence = r.ReadInt32(), AnimatedFrame = r.ReadInt32(), Normal = V3(r) };
                    int n = poly.Shape == 0 ? 4 : 3;
                    poly.Indices = new int[n];
                    poly.Uv = new Vector2[n];
                    for (int l = 0; l < n; l++) poly.Indices[l] = r.ReadInt32();
                    for (int l = 0; l < n; l++) poly.Uv[l] = V2(r);
                    r.BaseStream.Seek(3L * n * 12, SeekOrigin.Current);   // normals, tangents, binormals
                    bucket.Polygons.Add(poly);
                }
                room.Buckets.Add(bucket);
            }

            int portalCount = Count(r);
            for (int j = 0; j < portalCount; j++)
            {
                var portal = new TenPortal { RoomNumber = r.ReadInt16(), NormalX = r.ReadInt32(), NormalY = r.ReadInt32(), NormalZ = r.ReadInt32() };
                for (int v = 0; v < 12; v++) portal.Vertices[v] = r.ReadInt32();
                room.Portals.Add(portal);
            }

            room.ZSize = r.ReadInt32();
            room.XSize = r.ReadInt32();
            if (room.XSize < 0 || room.ZSize < 0 || (long)room.XSize * room.ZSize > 1 << 20)
                throw new InvalidDataException("Room size is incorrect.");
            room.Sectors = new TenSector[room.XSize * room.ZSize];
            for (int s = 0; s < room.Sectors.Length; s++)
            {
                var sec = new TenSector { TriggerIndex = r.ReadInt32(), PathfindingBoxId = r.ReadInt32(), Material = r.ReadInt32(), Stopper = r.ReadInt32() != 0 };
                sec.FloorSplitAngle = r.ReadSingle();
                sec.FloorPortal[0] = r.ReadInt32(); sec.FloorPortal[1] = r.ReadInt32();
                sec.FloorPlane[0] = V3(r); sec.FloorPlane[1] = V3(r);
                sec.CeilingSplitAngle = r.ReadSingle();
                sec.CeilingPortal[0] = r.ReadInt32(); sec.CeilingPortal[1] = r.ReadInt32();
                sec.CeilingPlane[0] = V3(r); sec.CeilingPlane[1] = V3(r);
                sec.SidePortal = r.ReadInt32();
                sec.Death = r.ReadBoolean(); sec.Monkeyswing = r.ReadBoolean();
                sec.ClimbNorth = r.ReadBoolean(); sec.ClimbSouth = r.ReadBoolean(); sec.ClimbEast = r.ReadBoolean(); sec.ClimbWest = r.ReadBoolean();
                sec.MarkTriggerer = r.ReadBoolean(); sec.MarkBeetle = r.ReadBoolean();
                room.Sectors[s] = sec;
            }

            int lightCount = Count(r);
            for (int j = 0; j < lightCount; j++)
            {
                var l = new TenLight { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32() };
                l.Dx = r.ReadSingle(); l.Dy = r.ReadSingle(); l.Dz = r.ReadSingle();
                l.R = r.ReadSingle(); l.G = r.ReadSingle(); l.B = r.ReadSingle();
                l.Intensity = r.ReadSingle(); l.In = r.ReadSingle(); l.Out = r.ReadSingle(); l.Length = r.ReadSingle(); l.Cutoff = r.ReadSingle();
                l.Type = r.ReadByte(); l.CastShadows = r.ReadBoolean();
                room.Lights.Add(l);
            }
            Rooms.Add(room);
        }

        int floorDataCount = r.ReadInt32();
        if (floorDataCount < 0 || floorDataCount * 2L > r.BaseStream.Length - r.BaseStream.Position)
            throw new InvalidDataException("Floordata size is incorrect.");
        FloorData = new short[floorDataCount];
        for (int i = 0; i < floorDataCount; i++) FloorData[i] = r.ReadInt16();
    }

    /// <summary>
    /// Reads the trigger a sector points to (TriggerIndex into the floordata). Unlike TR floordata there is no function word:
    /// [trigger type << 8][setup: timer, 0x100 one-shot, code bits << 9][actions...]. An action is [target type << 10 | parameter];
    /// camera, flyby and event actions are followed by an extra word (timer / one-shot); bit 15 of the last word ends the list.
    /// </summary>
    public Models.TrTrigger? ReadTrigger(int index)
    {
        if (index < 0 || index + 2 >= FloorData.Length) return null;
        var trigger = new Models.TrTrigger { SubFunction = (ushort)FloorData[index] >> 8, Setup = (ushort)FloorData[index + 1] };
        int i = index + 2;
        ushort last;
        do
        {
            if (i >= FloorData.Length) break;
            last = (ushort)FloorData[i++];
            int type = (last >> 10) & 0x1F;
            var action = new Models.TrTriggerAction { Type = type, Param = (ushort)(last & 0x03FF) };
            if (type is 1 or 12 or 16 or 17)
            {
                if (i >= FloorData.Length) break;
                last = (ushort)FloorData[i++];
                action.HasExtra = true;
                action.Extra = last;
            }
            trigger.Actions.Add(action);
        } while ((last & 0x8000) == 0);
        if (trigger.Actions.Count > 0 && trigger.Actions[^1].HasExtra) trigger.Actions[^1].Extra &= 0x7FFF;
        return trigger.Actions.Count > 0 ? trigger : null;
    }
    /// <summary>
    /// Mirrors LoadTextures(): five groups (room, moveables, statics, animated, sprites) of [count]{ width, height, size, colour map, then
    /// flag + blob for normal, ORSH and emissive maps }, followed by the sky. Room and animated atlases are kept; polygon UVs of rooms are
    /// relative to them.
    /// </summary>
    private void ReadAtlases(BinaryReader r)
    {
        RoomAtlases = ReadAtlasGroup(r, true);
        ReadAtlasGroup(r, false);   // moveables
        ReadAtlasGroup(r, false);   // statics
        AnimatedAtlases = ReadAtlasGroup(r, true);
    }

    private static List<TenAtlas> ReadAtlasGroup(BinaryReader r, bool keep)
    {
        int count = Count(r, 4096);
        var list = new List<TenAtlas>(count);
        for (int i = 0; i < count; i++)
        {
            var atlas = new TenAtlas { Width = r.ReadInt32(), Height = r.ReadInt32() };
            int size = r.ReadInt32();
            if (size < 0 || size > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Texture size is incorrect.");
            if (keep) atlas.ColorMap = r.ReadBytes(size); else r.BaseStream.Seek(size, SeekOrigin.Current);
            for (int map = 0; map < 3; map++)           // normal, ORSH, emissive
            {
                if (!r.ReadBoolean()) continue;
                int mapSize = r.ReadInt32();
                if (mapSize < 0 || mapSize > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Texture map size is incorrect.");
                r.BaseStream.Seek(mapSize, SeekOrigin.Current);
            }
            if (keep) list.Add(atlas);
        }
        return list;
    }
    private static TenEvent ReadEvent(BinaryReader r) => new()
    {
        Type = r.ReadInt32(),
        Mode = r.ReadInt32(),
        Function = r.ReadString(),
        Data = r.ReadString(),
        CallCounter = r.ReadInt32(),
        Enabled = r.ReadBoolean(),
    };

    /// <summary>Mirrors LoadDynamicRoomData, LoadItems, LoadAIObjects, LoadCameras, LoadSoundSources, LoadMaterials, LoadEventSets and LoadProperties.</summary>
    private void ReadDynamic(BinaryReader r)
    {
        int roomCount = Count(r);
        if (roomCount != Rooms.Count) throw new InvalidDataException("Dynamic room data count is inconsistent with room count.");
        foreach (var room in Rooms)
        {
            room.Name = r.ReadString();
            int tags = Count(r);
            for (int j = 0; j < tags; j++) room.Tags.Add(r.ReadString());
            room.Ambient = V3(r);
            room.FlippedRoom = r.ReadInt32();
            room.Flags = r.ReadInt32();
            room.MeshEffect = r.ReadInt32();
            room.ReverbType = r.ReadInt32();
            room.FlipNumber = r.ReadInt32();

            int statics = Count(r);
            for (int j = 0; j < statics; j++)
            {
                var s = new TenStatic { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32() };
                s.RotY = r.ReadUInt16(); s.RotX = r.ReadUInt16(); s.RotZ = r.ReadUInt16();
                s.Scale = r.ReadSingle();
                s.Flags = r.ReadUInt16();
                s.Color = V4(r);
                s.Slot = r.ReadUInt16();
                s.HitPoints = r.ReadInt16();
                s.Name = r.ReadString();
                room.Statics.Add(s);
            }

            int volumes = Count(r);
            for (int j = 0; j < volumes; j++)
            {
                var v = new TenVolume { Type = r.ReadInt32() };
                v.Position = V3(r); v.Orientation = V4(r); v.Scale = V3(r);
                v.Enabled = r.ReadBoolean(); v.DetectInAdjacentRooms = r.ReadBoolean();
                v.Name = r.ReadString();
                v.EventSetIndex = r.ReadInt32();
                room.Volumes.Add(v);
            }
        }

        int items = Count(r);
        for (int i = 0; i < items; i++)
        {
            var it = new TenItem { ObjectNumber = r.ReadInt16(), Room = r.ReadInt16(), X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32() };
            it.RotY = r.ReadInt16(); it.RotX = r.ReadInt16(); it.RotZ = r.ReadInt16();
            it.Color = V4(r);
            it.TriggerFlags = r.ReadInt16(); it.Flags = r.ReadInt16();
            it.Name = r.ReadString();
            Items.Add(it);
        }

        int ai = Count(r);
        for (int i = 0; i < ai; i++)
        {
            var o = new TenAiObject { ObjectNumber = r.ReadInt16(), Room = r.ReadInt16(), X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32() };
            o.RotY = r.ReadInt16(); o.RotX = r.ReadInt16(); o.RotZ = r.ReadInt16();
            o.TriggerFlags = r.ReadInt16(); o.Flags = r.ReadInt16();
            o.BoxNumber = r.ReadInt32();
            o.Name = r.ReadString();
            AiObjects.Add(o);
        }

        int cameras = Count(r);
        for (int i = 0; i < cameras; i++)
            Cameras.Add(new TenCamera { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), Room = r.ReadInt32(), Flags = r.ReadInt32(), Speed = r.ReadInt32(), Name = r.ReadString() });

        int flybys = Count(r);
        for (int i = 0; i < flybys; i++)
        {
            var f = new TenFlybyCamera { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), TargetX = r.ReadInt32(), TargetY = r.ReadInt32(), TargetZ = r.ReadInt32() };
            f.Sequence = r.ReadInt32(); f.Camera = r.ReadInt32();
            f.Fov = r.ReadInt16(); f.Roll = r.ReadInt16(); f.Timer = r.ReadInt16(); f.Speed = r.ReadInt16(); f.Flags = r.ReadInt16();
            f.Room = r.ReadInt32();
            f.DofMode = r.ReadInt32(); f.DofDistance = r.ReadSingle(); f.DofRange = r.ReadSingle(); f.DofStrength = r.ReadSingle();
            FlybyCameras.Add(f);
        }

        int sinks = Count(r);
        for (int i = 0; i < sinks; i++)
            Sinks.Add(new TenSink { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), Strength = r.ReadInt32(), BoxIndex = r.ReadInt32(), Name = r.ReadString() });

        int sounds = Count(r);
        for (int i = 0; i < sounds; i++)
            SoundSources.Add(new TenSoundSource { X = r.ReadInt32(), Y = r.ReadInt32(), Z = r.ReadInt32(), SoundId = r.ReadInt32(), Flags = r.ReadInt32(), Name = r.ReadString() });

        int materials = Count(r);
        for (int i = 0; i < materials; i++)
        {
            r.ReadString();            // name
            r.ReadInt32();             // type
            r.BaseStream.Seek(6, SeekOrigin.Current);   // normal, height, AO, roughness, specular, emissive map flags
        }

        int eventSets = Count(r);
        if (eventSets != 0)
        {
            int globals = Count(r);
            for (int i = 0; i < globals; i++)
            {
                var set = new TenEventSet { Name = r.ReadString() };
                int n = Count(r);
                for (int j = 0; j < n; j++) set.Events.Add(ReadEvent(r));
                GlobalEventSets.Add(set);
            }
            int volumeSets = Count(r);
            for (int i = 0; i < volumeSets; i++)
            {
                var set = new TenEventSet { Name = r.ReadString(), Activators = r.ReadInt32() };
                int n = Count(r);
                for (int j = 0; j < n; j++) set.Events.Add(ReadEvent(r));
                VolumeEventSets.Add(set);
            }
        }

        int props = Count(r);
        Properties = props > 0 ? r.ReadString() : "";

        if (r.BaseStream.Position != r.BaseStream.Length)
            throw new InvalidDataException($"Dynamic data block was not fully consumed ({r.BaseStream.Length - r.BaseStream.Position} bytes left).");
    }
}
