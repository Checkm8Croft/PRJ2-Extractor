using System.IO;
using System.Numerics;
using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;

// Is OUR floor/ceiling QUAD texture physically what the compiled TR4 shows? Compare, per sector corner (X, Z), the position of the UV
// inside the texture box (0/1 in u and v) between the TR4 raw face and what TombLib draws for our face. Also report the reference's answer
// so a disagreement can be attributed (ours vs TR4 vs reference).
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = false, IgnoreTextures = false, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
foreach (var r in ours.Concat(refs)) r.BuildGeometry(useLegacyCode: false);
bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;

// normalized (u, v) in {0,1} per (X sector corner, Z sector corner), or null
Dictionary<(int, int), (int, int)>? Norm(IEnumerable<((int, int) key, Vector2 uv)> pts)
{
    var l = pts.ToList(); if (l.Count == 0) return null;
    float minU = l.Min(p => p.uv.X), maxU = l.Max(p => p.uv.X), minV = l.Min(p => p.uv.Y), maxV = l.Max(p => p.uv.Y);
    if (maxU - minU < 1 || maxV - minV < 1) return null;
    var d = new Dictionary<(int, int), (int, int)>();
    foreach (var (k, uv) in l) d[k] = (uv.X - minU > maxU - uv.X ? 1 : 0, uv.Y - minV > maxV - uv.Y ? 1 : 0);
    return d;
}
Dictionary<(int, int), (int, int)>? Rendered(TombLib.LevelData.Room room, int x, int z, SectorFace f)
{
    if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, f), out var range)) return null;
    var pts = new List<((int, int), Vector2)>();
    for (int i = range.Start; i < range.Start + range.Count; i++)
    {
        var p = room.RoomGeometry.VertexPositions[i]; var t = room.RoomGeometry.TriangleTextureAreas[i / 3];
        pts.Add((((int)Math.Round(p.X / 1024.0), (int)Math.Round(p.Z / 1024.0)), (i % 3) switch { 0 => t.TexCoord0, 1 => t.TexCoord1, _ => t.TexCoord2 }));
    }
    return Norm(pts);
}
bool Same(Dictionary<(int, int), (int, int)> a, Dictionary<(int, int), (int, int)> b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

var tab = new Dictionary<string, int>();
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i]; var o = ours.FirstOrDefault(rr => RoomMatches(rr, r1)); var r = refs.FirstOrDefault(rr => RoomMatches(rr, r1)); if (o == null || r == null) continue;
    float py = o.Position.Y;
    foreach (var q in r1.Rectangles)
    {
        if (q.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
        var vs = q.Vertices.Select(v => r1.Vertices[v]).ToArray();
        if (vs.Max(v => (int)v.X) - vs.Min(v => (int)v.X) != 1024 || vs.Max(v => (int)v.Z) - vs.Min(v => (int)v.Z) != 1024) continue; // exactly one sector
        int sx = vs.Min(v => (int)v.X) / 1024, sz = vs.Min(v => (int)v.Z) / 1024;
        if (sx < 0 || sz < 0 || sx >= o.NumXSectors || sz >= o.NumZSectors || sx >= r.NumXSectors || sz >= r.NumZSectors) continue;
        var s = o.Sectors[sx, sz]; float absY = vs.Average(v => -(float)v.Y);
        float fl = (float)new[] { s.Floor.XnZn, s.Floor.XpZn, s.Floor.XnZp, s.Floor.XpZp }.Average() + py, ce = (float)new[] { s.Ceiling.XnZn, s.Ceiling.XpZn, s.Ceiling.XnZp, s.Ceiling.XpZp }.Average() + py;
        bool isFloor = Math.Abs(absY - fl) <= Math.Abs(absY - ce);
        var face = isFloor ? SectorFace.Floor : SectorFace.Ceiling;
        var ot = level.ObjectTextures[q.Texture & 0x7FFF];
        var raw = Norm(Enumerable.Range(0, 4).Select(k => (((int)Math.Round(vs[k].X / 1024.0), (int)Math.Round(vs[k].Z / 1024.0)), new Vector2(ot.Vertices[k].X >> 8, ot.Vertices[k].Y >> 8))));
        var a = Rendered(o, sx, sz, face); var b = Rendered(r, sx, sz, face);
        if (raw == null || a == null || b == null) continue;
        if (o.Sectors[sx, sz].GetFaceTexture(face).TextureIsUnavailable || r.Sectors[sx, sz].GetFaceTexture(face).TextureIsUnavailable) continue;
        string who = $"{(isFloor ? "floor" : "ceiling")} quad: ours==TR4 {(Same(a, raw) ? "yes" : "NO ")}, reference==TR4 {(Same(b, raw) ? "yes" : "NO ")}";
        Count(who);
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;