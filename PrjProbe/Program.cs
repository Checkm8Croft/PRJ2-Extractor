using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Floor texture gaps with the CORRECT reference room (best floor-height fit among position matches; ours[i] is exactly TR4 room i).
// For every Floor/Floor_Triangle2 face the reference textures but we do not: is the face defined in our geometry, does the TR4 have a floor
// face on that sector, slope class of the sector.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = false, IgnoreTextures = false, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
foreach (var r in ours.Where(r => r != null)) r.BuildGeometry(useLegacyCode: false);
bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
double Score(TombLib.LevelData.Room o, TombLib.LevelData.Room rr)
{
    if (o.NumXSectors != rr.NumXSectors || o.NumZSectors != rr.NumZSectors) return 1e12;
    double d = 0; for (int x = 0; x < o.NumXSectors; x++) for (int z = 0; z < o.NumZSectors; z++) { var a = o.Sectors[x, z]; var b = rr.Sectors[x, z]; if (a.Type != SectorType.Floor) continue;
        d += Math.Min(Math.Abs((a.Floor.XnZn + a.Floor.XpZn + a.Floor.XnZp + a.Floor.XpZp) / 4.0 + o.Position.Y - (b.Floor.XnZn + b.Floor.XpZn + b.Floor.XnZp + b.Floor.XpZp) / 4.0 - rr.Position.Y), 4096); }
    return d;
}
var tab = new Dictionary<string, int>(); var samples = new List<string>(); long refFaces = 0, bothFaces = 0;
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i]; var o = ours[i]; if (o == null) continue;
    var cands = refs.Where(rr => RoomMatches(rr, r1)).ToList(); if (cands.Count == 0) continue;
    var r = cands.OrderBy(c => Score(o, c)).First();
    float py = o.Position.Y;
    var tr4 = new Dictionary<(int, int), int>();
    foreach (var f in r1.Rectangles.Concat(r1.Triangles))
    {
        if (f.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
        var vs = f.Vertices.Select(v => r1.Vertices[v]).ToArray();
        int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
        if (maxX - minX <= 8 || maxZ - minZ <= 8) continue;
        float absY = vs.Average(v => -(float)v.Y);
        for (int sx = (int)Math.Floor(minX / 1024.0 + 0.01); sx * 1024 < maxX - 8; sx++)
        for (int sz = (int)Math.Floor(minZ / 1024.0 + 0.01); sz * 1024 < maxZ - 8; sz++)
        {
            if (sx < 0 || sz < 0 || sx >= o.NumXSectors || sz >= o.NumZSectors) continue;
            var s = o.Sectors[sx, sz];
            float fl = (float)new[] { s.Floor.XnZn, s.Floor.XpZn, s.Floor.XnZp, s.Floor.XpZp }.Average() + py, ce = (float)new[] { s.Ceiling.XnZn, s.Ceiling.XpZn, s.Ceiling.XnZp, s.Ceiling.XpZp }.Average() + py;
            if (Math.Abs(absY - fl) <= Math.Abs(absY - ce)) tr4[(sx, sz)] = tr4.GetValueOrDefault((sx, sz)) + 1;
        }
    }
    for (int x = 0; x < Math.Min(o.NumXSectors, r.NumXSectors); x++)
    for (int z = 0; z < Math.Min(o.NumZSectors, r.NumZSectors); z++)
    foreach (var f in new[] { SectorFace.Floor, SectorFace.Floor_Triangle2 })
    {
        var so = o.Sectors[x, z]; var sr = r.Sectors[x, z];
        bool ha = !so.GetFaceTexture(f).TextureIsUnavailable, hb = !sr.GetFaceTexture(f).TextureIsUnavailable;
        if (hb) refFaces++; if (ha && hb) bothFaces++;
        if (!hb || ha) continue;
        var c = new[] { so.Floor.XnZn, so.Floor.XpZn, so.Floor.XnZp, so.Floor.XpZp };
        string slope = c.Distinct().Count() == 1 ? "flat" : (c[0] + c[3] == c[1] + c[2]) ? "planar slope" : "non-planar slope";
        var info = o.GetFloorRoomConnectionInfo(new TombLib.VectorInt2(x, z));
        string key = $"{f}, {slope}: our geometry {(o.IsFaceDefined(x, z, f) ? "DEFINES it" : "has no such face")}, TR4 floor faces on sector={Math.Min(tr4.GetValueOrDefault((x, z)), 2)}, our connection={info.AnyType}, ref portal={(sr.FloorPortal == null ? "none" : sr.FloorPortal.Opacity.ToString())}";
        Count(key);
    }
}
Console.WriteLine($"reference floor faces textured: {refFaces}, of which we texture too: {bothFaces} ({100.0 * bothFaces / refFaces:F1}%)");
foreach (var kv in tab.OrderByDescending(k => k.Value).Take(20)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;