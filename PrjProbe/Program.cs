using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Geometry fidelity check (no editor needed): load our exported prj2 with TombLib, ask TombLib's own room geometry for the
// Y extent of every wall face on each Wall/BorderWall <-> real sector seam, and compare with the compiled TR4 quads.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;

var tab = new Dictionary<string, int>(); var samples = new List<string>();
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
const float tol = 40f;

for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var room = ours.FirstOrDefault(rr => RoomMatches(rr, r1));
    if (room == null) continue;
    int xs = room.NumXSectors, zs = room.NumZSectors; float py = room.Position.Y;
    var seams = new Dictionary<(int, int, bool), List<(float lo, float hi)>>();
    foreach (var face in r1.Rectangles.Concat(r1.Triangles))
    {
        var vs = face.Vertices.Where(v => v < r1.Vertices.Length).Select(v => r1.Vertices[v]).ToArray();
        if (vs.Length != face.Vertices.Length || vs.Length == 0) continue;
        int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
        int minY = vs.Min(v => (int)v.Y), maxY = vs.Max(v => (int)v.Y);
        double avgX = vs.Average(v => (double)v.X), avgZ = vs.Average(v => (double)v.Z);
        bool xWall = Math.Abs(maxX - minX) <= 8, zWall = Math.Abs(maxZ - minZ) <= 8;
        var q = (lo: (float)-maxY, hi: (float)-minY); // TR4 absolute, Y up
        void Add((int, int, bool) k) { if (!seams.TryGetValue(k, out var l)) seams[k] = l = new(); l.Add(q); }
        if (xWall) { int sx = (int)Math.Round(avgX / 1024.0); if (sx <= 0 || sx >= xs) continue;
            for (int z = Math.Clamp(minZ / 1024, 0, zs - 1); z <= Math.Clamp((maxZ - 1) / 1024, 0, zs - 1); z++) Add((sx, z, true)); }
        else if (zWall) { int sz = (int)Math.Round(avgZ / 1024.0); if (sz <= 0 || sz >= zs) continue;
            for (int x = Math.Clamp(minX / 1024, 0, xs - 1); x <= Math.Clamp((maxX - 1) / 1024, 0, xs - 1); x++) Add((x, sz, false)); }
    }

    foreach (var ((ox, oz, isX), quads) in seams)
    {
        int nx = isX ? ox - 1 : ox, nz = isX ? oz : oz - 1;
        var so = room.Sectors[ox, oz]; var sn = room.Sectors[nx, nz];
        if ((so.Type != SectorType.Floor) == (sn.Type != SectorType.Floor)) continue; // wall <-> real seams only
        var neg = isX ? new[] { SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_NegativeX_WS } : new[] { SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_NegativeZ_WS };
        var pos = isX ? new[] { SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_PositiveX_WS } : new[] { SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_PositiveZ_Middle, SectorFace.Wall_PositiveZ_WS };
        var faces = new List<(float lo, float hi)>();
        for (int k = 0; k < 3; k++)
        {
            if (room.IsFaceDefined(ox, oz, neg[k])) faces.Add((room.GetFaceLowestPoint(ox, oz, neg[k]) + py, room.GetFaceHighestPoint(ox, oz, neg[k]) + py));
            else if (room.IsFaceDefined(nx, nz, pos[k])) faces.Add((room.GetFaceLowestPoint(nx, nz, pos[k]) + py, room.GetFaceHighestPoint(nx, nz, pos[k]) + py));
        }
        int n = Math.Min(quads.Count, 4); string nk = n >= 4 ? "4+" : n.ToString();
        float tLo = quads.Min(q => q.lo), tHi = quads.Max(q => q.hi);
        if (faces.Count == 0) { Count($"quads={nk}: NO face defined"); continue; }
        float fLo = faces.Min(f => f.lo), fHi = faces.Max(f => f.hi);
        bool sameExtent = Math.Abs(fLo - tLo) <= tol && Math.Abs(fHi - tHi) <= tol;
        bool outside = fLo < tLo - tol || fHi > tHi + tol;
        // hole: any TR4 quad mid-height not covered by a face
        int holes = quads.Count(q => { float m = (q.lo + q.hi) / 2; return !faces.Any(f => m >= f.lo - tol && m <= f.hi + tol); });
        // exact: each face matches some quad range
        int exact = faces.Count(f => quads.Any(q => Math.Abs(q.lo - f.lo) <= tol && Math.Abs(q.hi - f.hi) <= tol));
        string verdict = holes > 0 ? "HOLE (TR4 quad not covered)" : outside ? "face extends beyond TR4 stack" : sameExtent ? (exact == faces.Count ? "same extent, faces == quads" : "same extent, faces merge/split quads") : "covered, extent differs";
        Count($"quads={nk}: {verdict}");
        if (samples.Count < 8 && (holes > 0 || outside))
            samples.Add($"R{i} ({ox},{oz}){(isX ? "X" : "Z")} TR4 {string.Join(" | ", quads.OrderBy(q => q.lo).Select(q => $"{q.lo:F0}..{q.hi:F0}"))}  ours {string.Join(" | ", faces.OrderBy(f => f.lo).Select(f => $"{f.lo:F0}..{f.hi:F0}"))}");
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
foreach (var s in samples) Console.WriteLine(s);
return 0;