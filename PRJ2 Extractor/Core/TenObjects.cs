using System.Numerics;
using TombLib.LevelData;
using TombLib.Wad;

namespace PRJ2_Extractor.Core;

/// <summary>
/// Moveables, AI objects, statics, lights, cameras, flybys, sinks and sound sources of a .ten level as TombLib instances (inverse of
/// TombLib's Compilers/TombEngine/LevelCompilerTombEngine.cs and Rooms.cs). The WAD is not extracted: instances only carry their slot ID.
/// </summary>
internal static class TenObjects
{
    // The compiled file stores half of the editor's colour values (ambient, statics, moveables, lights).
    private static Vector3 Color(Vector4 c) => new(c.X * 2, c.Y * 2, c.Z * 2);
    private static float Degrees(ushort angle) => angle * (360.0f / 65536.0f);
    private static float Degrees(short angle) => Degrees(unchecked((ushort)angle));

    private static Room? RoomOf(Room[] rooms, int index, string what, List<string> warnings)
    {
        if (index < 0 || index >= rooms.Length) { warnings.Add($"{what} skipped: invalid room {index}"); return null; }
        return rooms[index];
    }

    /// <summary>Local position inside the room: world X/Z minus the room origin, engine Y (down) negated minus the room's Position.Y.</summary>
    private static Vector3 Local(Room room, int x, int y, int z)
        => new(x - room.Position.X * 1024, -y - room.Position.Y, z - room.Position.Z * 1024);

    public static void Add(TenLevel ten, Room[] rooms, List<string> warnings)
    {
        for (int i = 0; i < ten.Rooms.Count; i++)
        {
            var t = ten.Rooms[i];
            var room = rooms[i];

            foreach (var s in t.Statics)
            {
                var instance = new StaticInstance
                {
                    WadObjectId = new WadStaticId(s.Slot),
                    Position = Local(room, s.X, s.Y, s.Z),
                    RotationY = Degrees(s.RotY),
                    Scale = s.Scale,
                    Color = Color(s.Color),
                    LuaName = s.Name,
                };
                room.AddObject(room.Level, instance);
            }

            foreach (var l in t.Lights)
            {
                var light = new LightInstance(l.Type switch { 0 => LightType.Sun, 2 => LightType.Spot, 3 => LightType.Shadow, 4 => LightType.FogBulb, _ => LightType.Point })
                {
                    Position = Local(room, l.X, l.Y, l.Z),
                    Color = new Vector3(l.R, l.G, l.B) * 2,
                    Intensity = l.Intensity,
                    CastDynamicShadows = l.CastShadows,
                };
                switch (light.Type)
                {
                    case LightType.Point: case LightType.Shadow: case LightType.FogBulb:
                        light.InnerRange = l.In / 1024.0f; light.OuterRange = l.Out / 1024.0f; break;
                    case LightType.Spot:
                        light.InnerAngle = l.In; light.OuterAngle = l.Out;
                        light.InnerRange = l.Length / 1024.0f; light.OuterRange = l.Cutoff / 1024.0f;
                        SetDirection(light, new Vector3(l.Dx, -l.Dy, l.Dz)); break;
                    case LightType.Sun:
                        SetDirection(light, new Vector3(l.Dx, -l.Dy, l.Dz)); break;
                }
                room.AddObject(room.Level, light);
            }
        }

        foreach (var it in ten.Items)
        {
            var room = RoomOf(rooms, it.Room, $"Moveable (ID {it.ObjectNumber}) '{it.Name}'", warnings);
            if (room == null) continue;
            var instance = new MoveableInstance
            {
                WadObjectId = new WadMoveableId((uint)(ushort)it.ObjectNumber),
                Position = Local(room, it.X, it.Y, it.Z),
                RotationY = Degrees(it.RotY),
                RotationX = Degrees(it.RotX),
                Roll = -Degrees(it.RotZ),
                Color = Color(it.Color),
                Ocb = it.TriggerFlags,
                // Flags layout is the compiler's: code bits << 9, 0x80 clear body, 0x100 invisible.
                CodeBits = (byte)((it.Flags >> 9) & 0x1F),
                ClearBody = (it.Flags & 0x80) != 0,
                Invisible = (it.Flags & 0x100) != 0,
                LuaName = it.Name,
            };
            room.AddObject(room.Level, instance);
        }

        // AI objects are stored apart in the file but are ordinary moveables in TombLib.
        foreach (var ai in ten.AiObjects)
        {
            var room = RoomOf(rooms, ai.Room, $"AI object (ID {ai.ObjectNumber}) '{ai.Name}'", warnings);
            if (room == null) continue;
            var instance = new MoveableInstance
            {
                WadObjectId = new WadMoveableId((uint)(ushort)ai.ObjectNumber),
                Position = Local(room, ai.X, ai.Y, ai.Z),
                RotationY = Degrees(ai.RotY),
                RotationX = Degrees(ai.RotX),
                Roll = -Degrees(ai.RotZ),
                Ocb = ai.TriggerFlags,
                CodeBits = (byte)((ai.Flags >> 1) & 0x1F),
                LuaName = ai.Name,
            };
            room.AddObject(room.Level, instance);
        }

        foreach (var c in ten.Cameras)
        {
            var room = RoomOf(rooms, c.Room, $"Camera '{c.Name}'", warnings);
            if (room == null) continue;
            var camera = new CameraInstance()
            {
                Position = Local(room, c.X, c.Y, c.Z),
                MoveTimer = (byte)c.Speed,
                GlideOut = (c.Flags & 0x0002) != 0,
                LuaName = c.Name,
            };
            if ((c.Flags & 0x0001) != 0) camera.CameraMode = CameraInstanceMode.Locked;
            room.AddObject(room.Level, camera);
        }

        foreach (var f in ten.FlybyCameras)
        {
            var room = RoomOf(rooms, f.Room, $"Flyby camera (sequence {f.Sequence}, number {f.Camera})", warnings);
            if (room == null) continue;
            var flyby = new FlybyCameraInstance
            {
                Position = Local(room, f.X, f.Y, f.Z),
                Sequence = (ushort)f.Sequence,
                Number = (ushort)f.Camera,
                Timer = f.Timer,
                Flags = (ushort)f.Flags,
                Speed = (ushort)f.Speed / 655.0f,
                Fov = (ushort)f.Fov * (360.0f / 65536.0f),
                Roll = (float)((65536 - (ushort)f.Roll) * (360.0 / 65536.0) % 360.0),
            };
            // The file stores a point one sector ahead of the camera instead of its rotation.
            SetDirection(flyby, new Vector3(f.TargetX - f.X, -(f.TargetY - f.Y), f.TargetZ - f.Z) / 1024.0f);
            room.AddObject(room.Level, flyby);
        }

        foreach (var s in ten.Sinks)
        {
            var room = RoomAt(ten, rooms, s.X, s.Y, s.Z);
            if (room == null) { warnings.Add($"Sink '{s.Name}' skipped: no room contains it"); continue; }
            room.AddObject(room.Level, new SinkInstance { Position = Local(room, s.X, s.Y, s.Z), Strength = (short)s.Strength, LuaName = s.Name });
        }

        foreach (var s in ten.SoundSources)
        {
            var room = RoomAt(ten, rooms, s.X, s.Y, s.Z);
            if (room == null) { warnings.Add($"Sound source '{s.Name}' skipped: no room contains it"); continue; }
            // Flags: low bits = flip group, 0x8000 always, 0x4000 base room only, 0x2000 alternate room only. "Automatic" writes the
            // flag that fits the room's flip state, so those combinations are read back as Automatic.
            var mode = SoundSourcePlayMode.Automatic;
            if ((s.Flags & 0x8000) != 0) mode = room.Alternated ? SoundSourcePlayMode.Always : SoundSourcePlayMode.Automatic;
            else if ((s.Flags & 0x4000) != 0) mode = room.Alternated && !room.IsAlternate ? SoundSourcePlayMode.Automatic : SoundSourcePlayMode.OnlyInBaseRoom;
            else if ((s.Flags & 0x2000) != 0) mode = room.IsAlternate ? SoundSourcePlayMode.Automatic : SoundSourcePlayMode.OnlyInAlternateRoom;
            room.AddObject(room.Level, new SoundSourceInstance { Position = Local(room, s.X, s.Y, s.Z), SoundId = s.SoundId, PlayMode = mode, LuaName = s.Name });
        }
    }

    /// <summary>Sinks and sound sources carry no room index: take the room whose box contains the point (the base room first).</summary>
    private static Room? RoomAt(TenLevel ten, Room[] rooms, int x, int y, int z)
    {
        for (int i = 0; i < ten.Rooms.Count; i++)
        {
            var t = ten.Rooms[i];
            if (x < t.X || z < t.Z || x >= t.X + t.XSize * 1024 || z >= t.Z + t.ZSize * 1024) continue;
            if (-y < rooms[i].Position.Y + 0 && y > t.BottomHeight + 1) { }
            if (y <= t.BottomHeight && y >= t.TopHeight) return rooms[i];
        }
        return null;
    }

    private static void SetDirection(IRotateableYX obj, Vector3 direction)
    {
        var d = Vector3.Normalize(direction);
        float rotationX = (float)(Math.Asin(Math.Clamp(d.Y, -1f, 1f)) * 180.0 / Math.PI);
        float rotationY = (float)(Math.Atan2(d.X, d.Z) * 180.0 / Math.PI);
        obj.SetArbitaryRotationsYX(rotationY, rotationX);
    }
}
