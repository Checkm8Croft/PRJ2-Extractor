using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Wall-height oracle (re-measured with the correct reference rooms): on seams between a REFERENCE Floor sector and a REFERENCE Wall/BorderWall
// sector, compare the hidden wall floor/ceiling (WF/WC) stored in the reference with the boundaries of the compiled TR4 quad stack, and
// count how many reference faces (QA/Middle/WS defined) a seam has against its compiled quad count.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var oursAll = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
foreach (var r in refs) r.BuildGeometry(useLegacyCode: false);
bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
TombLib.LevelData.Room? BestRef(int i)
{
    var r1 = level.Rooms[i]; var o = oursAll[i]; var cands = refs.Where(rr => RoomMatches(rr, r1)).ToList(); if (cands.Count < 2 || o == null) return cands.FirstOrDefault();
    double Sc(TombLib.LevelData.Room rr) { if (o.NumXSectors != rr.NumXSectors || o.NumZSectors != rr.NumZSectors) return 1e12; double d = 0;
        for (int x = 0; x < o.NumXSectors; x++) for (int z = 0; z < o.NumZSectors; z++) { var a = o.Sectors[x, z]; var b = rr.Sectors[x, z]; if (a.Type != SectorType.Floor) continue;
            d += Math.Min(Math.Abs((a.Floor.XnZn + a.Floor.XpZn + a.Floor.XnZp + a.Floor.XpZp) / 4.0 + o.Position.Y - (b.Floor.XnZn + b.Floor.XpZn + b.Floor.XnZp + b.Floor.XpZp) / 4.0 - rr.Position.Y), 4096); } return d; }
    return cands.OrderBy(Sc).First();
}
var tab = new Dictionary<string, int>(); var faceTab = new Dictionary<string, int>();
void Count(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
bool near(int a, int b) => Math.Abs(a - b) <= 8;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i]; var room = BestRef(i); if (room == null) continue;
    int xs = room.NumXSectors, zs = room.NumZSectors;
    var seams = new Dictionary<(int, int, bool), List<(int lo, int hi)>>();
    foreach (var face in r1.Rectangles.Concat(r1.Triangles))
    {
        if (face.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
        var vs = face.Vertices.Select(v => r1.Vertices[v]).ToArray();
        int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z), minY = vs.Min(v => (int)v.Y), maxY = vs.Max(v => (int)v.Y);
        double avgX = vs.Average(v => (double)v.X), avgZ = vs.Average(v => (double)v.Z);
        bool xWall = Math.Abs(maxX - minX) <= 8, zWall = Math.Abs(maxZ - minZ) <= 8;
        var q = (lo: -maxY + r1.YBottom, hi: -minY + r1.YBottom);
        void Add((int, int, bool) k) { if (!seams.TryGetValue(k, out var l)) seams[k] = l = new(); l.Add(q); }
        if (xWall) { int sx = (int)Math.Round(avgX / 1024.0); if (sx <= 0 || sx >= xs) continue; for (int z = Math.Clamp(minZ / 1024, 0, zs - 1); z <= Math.Clamp((maxZ - 1) / 1024, 0, zs - 1); z++) Add((sx, z, true)); }
        else if (zWall) { int sz = (int)Math.Round(avgZ / 1024.0); if (sz <= 0 || sz >= zs) continue; for (int x = Math.Clamp(minX / 1024, 0, xs - 1); x <= Math.Clamp((maxX - 1) / 1024, 0, xs - 1); x++) Add((x, sz, false)); }
    }
    foreach (var ((ox, oz, isX), quads) in seams)
    {
        int nx = isX ? ox - 1 : ox, nz = isX ? oz : oz - 1;
        var own = room.Sectors[ox, oz]; var nei = room.Sectors[nx, nz];
        bool ownWall = own.Type != SectorType.Floor, neiWall = nei.Type != SectorType.Floor; if (ownWall == neiWall) continue;
        var wall = ownWall ? own : nei;
        int[] wf, wc;
        if (isX && ownWall) { wf = new[] { wall.Floor.XnZn, wall.Floor.XnZp }; wc = new[] { wall.Ceiling.XnZn, wall.Ceiling.XnZp }; }
        else if (isX) { wf = new[] { wall.Floor.XpZn, wall.Floor.XpZp }; wc = new[] { wall.Ceiling.XpZn, wall.Ceiling.XpZp }; }
        else if (ownWall) { wf = new[] { wall.Floor.XnZn, wall.Floor.XpZn }; wc = new[] { wall.Ceiling.XnZn, wall.Ceiling.XpZn }; }
        else { wf = new[] { wall.Floor.XnZp, wall.Floor.XpZp }; wc = new[] { wall.Ceiling.XnZp, wall.Ceiling.XpZp }; }
        bool flat = wf[0] == wf[1] && wc[0] == wc[1];
        var sorted = quads.OrderBy(q => q.lo).ToList(); var bounds = new List<int>(); for (int k = 0; k + 1 < sorted.Count; k++) bounds.Add(sorted[k].hi);
        string res;
        if (sorted.Count == 1) res = near(sorted[0].lo, wf[0]) || near(sorted[0].hi, wc[0]) || near(sorted[0].lo, wc[0]) || near(sorted[0].hi, wf[0]) ? "edge matches WF/WC" : "no match";
        else if (sorted.Count == 2) res = near(bounds[0], wf[0]) || near(bounds[0], wc[0]) ? "boundary == WF or WC" : "no match";
        else if (sorted.Count == 3) res = near(bounds[0], wf[0]) && near(bounds[1], wc[0]) ? "b1==WF && b2==WC" : "no / partial match";
        else res = "4+ (skipped)";
        Count(tab, $"quads={Math.Min(sorted.Count, 4)}{(flat ? " straight edge" : " sloped edge")}: {res}");
        // reference faces defined on this seam
        var neg = isX ? new[] { SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_NegativeX_WS } : new[] { SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_NegativeZ_WS };
        var pos = isX ? new[] { SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_PositiveX_WS } : new[] { SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_PositiveZ_Middle, SectorFace.Wall_PositiveZ_WS };
        int refFaces = 0; for (int k = 0; k < 3; k++) if (room.IsFaceDefined(ox, oz, neg[k]) || room.IsFaceDefined(nx, nz, pos[k])) refFaces++;
        Count(faceTab, $"{Math.Min(sorted.Count, 4)} compiled quads -> {refFaces} reference faces");
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
Console.WriteLine("--- compiled quads vs faces defined in the reference ---");
foreach (var kv in faceTab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;