using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Placement census: for every interior seam covered by compiled quads, how many wall faces (QA/Middle/WS) does OUR exported
// geometry define, and how many of them carry a texture? Compares against the number of compiled quads on the seam.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;

var tab = new Dictionary<string, int>(); long deficit = 0, totalQuads = 0, totalDefined = 0, totalTextured = 0; int unmatched = 0;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var room = ours.FirstOrDefault(rr => RoomMatches(rr, r1));
    if (room == null) { unmatched++; continue; }
    int xs = room.NumXSectors, zs = room.NumZSectors;
    var seams = new Dictionary<(int, int, bool), int>();
    foreach (var face in r1.Rectangles.Concat(r1.Triangles))
    {
        var vs = face.Vertices.Where(v => v < r1.Vertices.Length).Select(v => r1.Vertices[v]).ToArray();
        if (vs.Length != face.Vertices.Length || vs.Length == 0) continue;
        int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X), minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
        double avgX = vs.Average(v => (double)v.X), avgZ = vs.Average(v => (double)v.Z);
        bool xWall = Math.Abs(maxX - minX) <= 8, zWall = Math.Abs(maxZ - minZ) <= 8;
        if (xWall)
        {
            int sx = (int)Math.Round(avgX / 1024.0); if (sx <= 0 || sx >= xs) continue;
            for (int z = Math.Clamp(minZ / 1024, 0, zs - 1); z <= Math.Clamp((maxZ - 1) / 1024, 0, zs - 1); z++) seams[(sx, z, true)] = seams.GetValueOrDefault((sx, z, true)) + 1;
        }
        else if (zWall)
        {
            int sz = (int)Math.Round(avgZ / 1024.0); if (sz <= 0 || sz >= zs) continue;
            for (int x = Math.Clamp(minX / 1024, 0, xs - 1); x <= Math.Clamp((maxX - 1) / 1024, 0, xs - 1); x++) seams[(x, sz, false)] = seams.GetValueOrDefault((x, sz, false)) + 1;
        }
    }
    foreach (var ((ox, oz, isX), qn) in seams)
    {
        int nx = isX ? ox - 1 : ox, nz = isX ? oz : oz - 1;
        var neg = isX ? new[] { SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_NegativeX_WS } : new[] { SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_NegativeZ_WS };
        var pos = isX ? new[] { SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_PositiveX_WS } : new[] { SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_PositiveZ_Middle, SectorFace.Wall_PositiveZ_WS };
        int d = 0, t = 0;
        var so = room.Sectors[ox, oz].GetFaceTextures(); var sn = room.Sectors[nx, nz].GetFaceTextures();
        for (int k = 0; k < 3; k++)
        {
            if (room.IsFaceDefined(ox, oz, neg[k]) || room.IsFaceDefined(nx, nz, pos[k])) d++;
            if (so.ContainsKey(neg[k]) || sn.ContainsKey(pos[k])) t++;
        }
        int need = Math.Min(qn, 3);
        totalQuads += need; totalDefined += Math.Min(d, need); totalTextured += t;
        deficit += Math.Max(0, need - d);
        string key = $"compiled quads={(qn >= 4 ? "4+" : qn.ToString())}  defined faces={d}  textured={t}";
        tab[key] = tab.GetValueOrDefault(key) + 1;
    }
}
Console.WriteLine($"unmatched rooms: {unmatched}; quads needing a face (capped 3/seam): {totalQuads}; of which our geometry defines a face: {totalDefined}; textured faces: {totalTextured}; deficit (quads without a defined face): {deficit}");
foreach (var kv in tab.OrderByDescending(k => k.Value).Take(14)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;