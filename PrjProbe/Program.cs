using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Per REFERENCE floor/ceiling portal: its Opacity vs what the compiled TR4 shows inside its area. The reference twin of a TR4 room is chosen by
// geometry AND by which faces carry a real texture (twin/flipped rooms share geometry; the TR4 was compiled from exactly one of them).
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = false, IgnoreSoundsCatalogs = true };
var oursAll = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
var faceKinds = new[] { SectorFace.Floor, SectorFace.Ceiling, SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_PositiveX_WS, SectorFace.Wall_PositiveZ_WS };
bool texturesAware = Environment.GetEnvironmentVariable("GEOMETRY_ONLY") != "1";
TombLib.LevelData.Room? BestRef(int i)
{
    var r1 = level.Rooms[i]; var o = oursAll[i]; var cands = refs.Where(rr => RoomMatches(rr, r1)).ToList(); if (cands.Count < 2 || o == null) return cands.FirstOrDefault();
    double Sc(TombLib.LevelData.Room rr)
    {
        if (o.NumXSectors != rr.NumXSectors || o.NumZSectors != rr.NumZSectors) return 1e12; double d = 0;
        for (int x = 0; x < o.NumXSectors; x++) for (int z = 0; z < o.NumZSectors; z++)
        {
            var a = o.Sectors[x, z]; var b = rr.Sectors[x, z];
            if (a.Type == SectorType.Floor) d += Math.Min(Math.Abs((a.Floor.XnZn + a.Floor.XpZn + a.Floor.XnZp + a.Floor.XpZp) / 4.0 + o.Position.Y - (b.Floor.XnZn + b.Floor.XpZn + b.Floor.XnZp + b.Floor.XpZp) / 4.0 - rr.Position.Y), 4096);
            if (texturesAware) foreach (var ff in faceKinds) if ((a.GetFaceTexture(ff).Texture != null) != (b.GetFaceTexture(ff).Texture != null)) d += 600;
        }
        return d;
    }
    return cands.OrderBy(Sc).First();
}
int amb = 0, picked = 0, shown = 0;
var tab = new Dictionary<string, int>(); var tex = new Dictionary<string, int>(); var feat = new Dictionary<string, int>();
void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i]; var r = BestRef(i); if (r == null) continue;
    if (refs.Count(rr => RoomMatches(rr, r1)) > 1) amb++; picked++;
    float py = r.Position.Y;
    var quads = new Dictionary<(int, int, bool), int>(); var tris = new Dictionary<(int, int, bool), int>();
    foreach (var (list, dict) in new[] { (r1.Rectangles, quads), (r1.Triangles, tris) })
        foreach (var f in list)
        {
            if (f.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
            var vs = f.Vertices.Select(v => r1.Vertices[v]).ToArray();
            int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
            if (maxX - minX < 1000 || maxZ - minZ < 1000) continue;
            double absY = vs.Average(v => -(double)v.Y);
            for (int x = Math.Max(0, (int)Math.Round(minX / 1024.0)); x < Math.Min(r.NumXSectors, (int)Math.Round(maxX / 1024.0)); x++)
            for (int z = Math.Max(0, (int)Math.Round(minZ / 1024.0)); z < Math.Min(r.NumZSectors, (int)Math.Round(maxZ / 1024.0)); z++)
            {
                var s = r.Sectors[x, z];
                double fl = new[] { s.Floor.XnZn, s.Floor.XpZn, s.Floor.XnZp, s.Floor.XpZp }.Average() + py, ce = new[] { s.Ceiling.XnZn, s.Ceiling.XpZn, s.Ceiling.XnZp, s.Ceiling.XpZp }.Average() + py;
                var key = (x, z, Math.Abs(absY - fl) <= Math.Abs(absY - ce)); dict[key] = dict.GetValueOrDefault(key) + 1;
            }
        }
    foreach (var portal in r.Portals.Where(p => p.Direction == PortalDirection.Floor || p.Direction == PortalDirection.Ceiling))
    {
        bool isFloor = portal.Direction == PortalDirection.Floor;
        int drawn = 0, half = 0, hole = 0, texturedFaces = 0, sectors = 0;
        for (int x = portal.Area.X0; x <= portal.Area.X1; x++) for (int z = portal.Area.Y0; z <= portal.Area.Y1; z++)
        {
            sectors++; var key = (x, z, isFloor);
            if (quads.GetValueOrDefault(key) >= 1 || tris.GetValueOrDefault(key) >= 2) drawn++; else if (tris.GetValueOrDefault(key) == 1) half++; else hole++;
            if (r.Sectors[x, z].GetFaceTexture(isFloor ? SectorFace.Floor : SectorFace.Ceiling).Texture != null) texturedFaces++;
        }
        string cls = drawn == sectors ? "TR4 draws EVERY sector" : drawn > 0 && hole == 0 ? "TR4 draws some, rest half-drawn" : drawn > 0 ? "TR4 draws some, some are holes" : hole == sectors ? "TR4 draws NOTHING" : "TR4 draws only half-triangles";
        Count(tab, $"{(isFloor ? "floor" : "ceiling")} portal, reference Opacity={portal.Opacity,-16}: {cls}");
        {
            int two = 0, one = 0, zero = 0, secs = 0;
            for (int x = portal.Area.X0; x <= portal.Area.X1; x++) for (int z = portal.Area.Y0; z <= portal.Area.Y1; z++)
            {
                secs++; int layers = quads.GetValueOrDefault((x, z, isFloor)) + (tris.GetValueOrDefault((x, z, isFloor)) + 1) / 2;
                if (layers >= 2) two++; else if (layers == 1) one++; else zero++;
            }
            string bucket = two == secs ? "EVERY sector has 2+ horizontal face layers" : two > 0 ? "some sectors have 2+ layers" : one == secs ? "every sector has exactly 1 layer" : one > 0 ? "1 layer or less" : "no layers";
            Count(feat, $"{(isFloor ? "floor  " : "ceiling")} Opacity={portal.Opacity,-16} adjoining={portal.AdjoiningRoom.Properties.Type,-6}: {bucket}");
        }
        Count(tex, $"{(isFloor ? "floor" : "ceiling")} Opacity={portal.Opacity,-16}: reference textures on the portal surface: {(texturedFaces == 0 ? "none" : texturedFaces == sectors ? "ALL sectors" : "some sectors")}");
    }
}
Console.WriteLine($"rooms with several reference candidates: {amb}; texture-aware matching: {texturesAware}");
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
Console.WriteLine("--- layers rule: horizontal face layers per sector inside each reference portal ---"); foreach (var kv in feat.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
Console.WriteLine("--- the reference's own textures on the portal surface ---");
foreach (var kv in tex.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;