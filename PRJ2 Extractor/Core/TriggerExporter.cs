using System.IO;
using PRJ2_Extractor.Models;
using TombLib;
using TombLib.LevelData;
using TombLib.NG;
using TombLib.Utils;

namespace PRJ2_Extractor.Core;

/// <summary>Exported objects that a trigger action can point to, keyed by the index the FloorData uses.</summary>
internal sealed class ObjectRegistry
{
    public readonly Dictionary<int, MoveableInstance> Items = [];
    public readonly Dictionary<int, CameraInstance> Cameras = [];       // key = parameter & 0x7F, as in CameraFloorDataIndices
    public readonly Dictionary<int, SinkInstance> Sinks = [];
    public readonly Dictionary<int, FlybyCameraInstance> FlybyBySequence = [];
}

/// <summary>
/// Turns the raw FloorData triggers into TombLib TriggerInstance objects (inverse of the trigger part of
/// TombLib's Compilers/FloorData.cs). One compiled sector holds a single trigger function whose setup word
/// (type, timer, one-shot, code bits) applies to every action; TombLib merges all TriggerInstances overlapping a
/// sector into that one list, so each action becomes one TriggerInstance and sectors that share the same trigger are
/// merged back into rectangles.
/// </summary>
internal static class TriggerExporter
{
    private sealed class Spec
    {
        public TriggerType Type;
        public TriggerTargetType TargetType;
        public object? TargetObject;       // an ObjectInstance, or null when the target is a plain number
        public ushort TargetKey;
        public ushort Timer;
        public ushort? Extra;
        public bool OneShot;
        public byte CodeBits = 0x1F;
        public string Signature = "";
    }

    private static bool _catalogTried;

    /// <summary>
    /// NG flipeffect / action triggers store timer and extra in one word; telling them apart needs TombLib's NG catalog
    /// (NgCatalog.xml). Look next to the app and next to TombLib.dll; without it the split falls back to low byte / high byte.
    /// </summary>
    private static bool EnsureNgCatalog()
    {
        if (NgCatalog.Instance != null) return true;
        if (_catalogTried) return false;
        _catalogTried = true;
        var dirs = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(NgCatalog).Assembly.Location) ?? "" };
        foreach (var dir in dirs)
        {
            string path = Path.Combine(dir, "Catalogs", "NgCatalog.xml");
            if (!File.Exists(path)) continue;
            try { NgCatalog.LoadCatalog(path); return true; }
            catch (Exception) { NgCatalog.Instance = null!; }
        }
        return false;
    }

    public static void Export(TrLevel trLevel, Room?[] tombRooms, ObjectRegistry registry, List<string> warnings)
    {
        bool ng = trLevel.IsNg;
        bool catalog = ng && EnsureNgCatalog();
        bool warnedCatalog = false, warnedUnknown = false;
        var ids = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);

        for (int roomIdx = 0; roomIdx < trLevel.Rooms.Length && roomIdx < tombRooms.Length; roomIdx++)
        {
            var room = tombRooms[roomIdx];
            var r1 = trLevel.Rooms[roomIdx];
            if (room == null || r1.NumX == 0 || r1.NumZ == 0) continue;

            var specs = new List<Spec>?[r1.NumX, r1.NumZ];
            var signatures = new string?[r1.NumX, r1.NumZ];
            for (int x = 0; x < r1.NumX; x++)
            for (int z = 0; z < r1.NumZ; z++)
            {
                var trigger = r1.Sectors[x * r1.NumZ + z].FloorInfo?.FirstOrDefault(f => f.Trigger != null)?.Trigger;
                if (trigger == null) continue;
                var list = Decode(trigger, ng, catalog, registry, ids, warnings, roomIdx, x, z, ref warnedCatalog, ref warnedUnknown);
                if (list.Count == 0) continue;
                specs[x, z] = list;
                signatures[x, z] = string.Join(";", list.Select(s => s.Signature));
            }

            // Greedy rectangle cover over sectors with identical trigger lists.
            var done = new bool[r1.NumX, r1.NumZ];
            for (int z = 0; z < r1.NumZ; z++)
            for (int x = 0; x < r1.NumX; x++)
            {
                string? sig = signatures[x, z];
                if (sig == null || done[x, z]) continue;
                int x1 = x;
                while (x1 + 1 < r1.NumX && !done[x1 + 1, z] && signatures[x1 + 1, z] == sig) x1++;
                int z1 = z;
                while (z1 + 1 < r1.NumZ)
                {
                    bool rowOk = true;
                    for (int xx = x; xx <= x1 && rowOk; xx++)
                        rowOk = !done[xx, z1 + 1] && signatures[xx, z1 + 1] == sig;
                    if (!rowOk) break;
                    z1++;
                }
                for (int xx = x; xx <= x1; xx++)
                for (int zz = z; zz <= z1; zz++)
                    done[xx, zz] = true;

                foreach (var s in specs[x, z]!)
                {
                    var instance = new TriggerInstance(new RectangleInt2(x, z, x1, z1))
                    {
                        TriggerType = s.Type,
                        TargetType = s.TargetType,
                        Target = s.TargetObject is ITriggerParameter p ? p : new TriggerParameterUshort(s.TargetKey),
                        Timer = new TriggerParameterUshort(s.Timer),
                        Extra = s.Extra.HasValue ? new TriggerParameterUshort(s.Extra.Value) : null,
                        OneShot = s.OneShot,
                        CodeBits = s.CodeBits,
                    };
                    try { room.AddObject(room.Level, instance); }
                    catch (Exception ex) { warnings.Add($"Room {roomIdx}: trigger at ({x},{z})-({x1},{z1}) skipped: {ex.Message}"); }
                }
            }
        }
    }

    private static List<Spec> Decode(TrTrigger t, bool ng, bool catalog, ObjectRegistry reg, Dictionary<object, int> ids,
        List<string> warnings, int roomIdx, int x, int z, ref bool warnedCatalog, ref bool warnedUnknown)
    {
        var result = new List<Spec>();
        string where = $"room {roomIdx} sector ({x},{z})";

        TriggerType type;
        switch (t.SubFunction)
        {
            case >= 0 and <= 11: type = (TriggerType)t.SubFunction; break;
            case 12: type = ng ? TriggerType.ConditionNg : TriggerType.Monkey; break;
            case 13: type = TriggerType.Skeleton; break;
            case 14: type = TriggerType.TightRope; break;
            case 15: type = TriggerType.Crawl; break;
            case 16: type = TriggerType.Climb; break;
            default:
                warnings.Add($"Trigger in {where} skipped: unknown trigger type {t.SubFunction}");
                return result;
        }

        bool oneShot = (t.Setup & 0x100) != 0;
        byte codeBits = type == TriggerType.ConditionNg ? (byte)0x1F : (byte)((t.Setup >> 9) & 0x1F);
        ushort setupTimer = (ushort)(t.Setup & 0xFF);
        ushort? setupExtra = null;
        if (type == TriggerType.ConditionNg)
        {
            int bits = (t.Setup >> 9) & 0x7F;
            setupExtra = bits != 0 ? (ushort)bits : null;
        }

        foreach (var a in t.Actions)
        {
            var s = new Spec { Type = type, OneShot = oneShot, CodeBits = codeBits, Timer = setupTimer, Extra = setupExtra };
            switch (a.Type)
            {
                case 0: // Object
                case 6: // Target (look at)
                    s.TargetType = a.Type == 0 ? TriggerTargetType.Object : TriggerTargetType.Target;
                    if (!reg.Items.TryGetValue(a.Param, out var item)) { warnings.Add($"Trigger in {where}: no moveable with item index {a.Param}, action skipped"); continue; }
                    s.TargetObject = item;
                    break;
                case 1: // Camera
                    s.TargetType = TriggerTargetType.Camera;
                    if (!reg.Cameras.TryGetValue(a.Param & 0x7F, out var camera)) { warnings.Add($"Trigger in {where}: no camera with index {a.Param}, action skipped"); continue; }
                    s.TargetObject = camera;
                    if (a.HasExtra)
                    {
                        s.Timer = (ushort)(a.Extra & 0xFF);
                        if (camera.CameraMode != CameraInstanceMode.Sniper)
                            camera.MoveTimer = (byte)((a.Extra >> 9) & 0x3F);
                    }
                    break;
                case 2: // Sink
                    s.TargetType = TriggerTargetType.Sink;
                    if (!reg.Sinks.TryGetValue(a.Param, out var sink)) { warnings.Add($"Trigger in {where}: no sink with index {a.Param}, action skipped"); continue; }
                    s.TargetObject = sink;
                    break;
                case 3: s.TargetType = TriggerTargetType.FlipMap; s.TargetKey = a.Param; break;
                case 4: s.TargetType = TriggerTargetType.FlipOn; s.TargetKey = a.Param; break;
                case 5: s.TargetType = TriggerTargetType.FlipOff; s.TargetKey = a.Param; break;
                case 7: s.TargetType = TriggerTargetType.FinishLevel; s.TargetKey = a.Param; break;
                case 8: s.TargetType = TriggerTargetType.PlayAudio; s.TargetKey = a.Param; break;
                case 10: s.TargetType = TriggerTargetType.Secret; s.TargetKey = a.Param; break;
                case 9: // FlipEffect (NG: extra word holds timer and extra)
                case 11: // ActionNg (NG only)
                    s.TargetType = a.Type == 9 ? TriggerTargetType.FlipEffect : TriggerTargetType.ActionNg;
                    s.TargetKey = a.Param;
                    if (a.HasExtra)
                    {
                        DecodeRealTimer(s.TargetType, type, a.Param, a.Extra, catalog, out ushort timer, out ushort? extra);
                        s.Timer = timer;
                        s.Extra = extra;
                        if (!catalog && !warnedCatalog)
                        {
                            warnedCatalog = true;
                            warnings.Add("NG flipeffect/action timers were split low byte / high byte because NgCatalog.xml was not found (looked in Catalogs\\ next to the app and next to TombLib.dll)");
                        }
                    }
                    break;
                case 12: // Flyby
                    s.TargetType = TriggerTargetType.FlyByCamera;
                    if (!reg.FlybyBySequence.TryGetValue(a.Param, out var flyby)) { warnings.Add($"Trigger in {where}: no flyby camera with sequence {a.Param}, action skipped"); continue; }
                    s.TargetObject = flyby;
                    break;
                case 13: s.TargetType = TriggerTargetType.ParameterNg; s.TargetKey = a.Param; break;
                case 14: s.TargetType = TriggerTargetType.FmvNg; s.TargetKey = a.Param; break;
                case 15: s.TargetType = TriggerTargetType.TimerfieldNg; s.TargetKey = a.Param; break;
                default:
                    if (!warnedUnknown)
                    {
                        warnedUnknown = true;
                        warnings.Add($"Trigger action type {a.Type} (first seen in {where}) is not known and was skipped; older NGLE levels store inline data blocks this way");
                    }
                    continue;
            }

            string target;
            if (s.TargetObject != null)
            {
                if (!ids.TryGetValue(s.TargetObject, out int id)) ids[s.TargetObject] = id = ids.Count;
                target = "o" + id;
            }
            else target = "k" + s.TargetKey;
            s.Signature = $"{(int)s.Type}|{(int)s.TargetType}|{target}|{s.Timer}|{s.Extra}|{s.OneShot}|{s.CodeBits}";
            result.Add(s);
        }
        return result;
    }

    private static void DecodeRealTimer(TriggerTargetType targetType, TriggerType type, ushort target, ushort real, bool catalog,
        out ushort timer, out ushort? extra)
    {
        if (catalog)
        {
            NgParameterInfo.DecodeNGRealTimer(targetType, type, target, real, 0, out var t, out var e);
            timer = t ?? 0;
            extra = e;
            return;
        }
        timer = (ushort)(real & 0xFF);
        extra = (real >> 8) != 0 ? (ushort)(real >> 8) : null;
    }
}
