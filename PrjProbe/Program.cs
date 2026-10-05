using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;
using TombLib.Utils;

// Does OUR exported triangulation of floor/ceiling agree with the compiled TR4 triangles, sector by sector?
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
foreach (var r in ours) r.BuildGeometry(useLegacyCode: false);

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
string Sig(IEnumerable<(int, int)> pts) => string.Join(";", pts.Select(c => (c.Item1, c.Item2)).OrderBy(c => c).Select(c => $"{c.Item1},{c.Item2}"));

var tab = new Dictionary<string, int>(); var samples = new List<string>();
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i]; var o = ours.FirstOrDefault(rr => RoomMatches(rr, r1)); if (o == null) continue;
    float py = o.Position.Y;
    // TR4 horizontal triangles per sector, classified floor/ceiling by height
    var tr4 = new Dictionary<(int, int, bool), HashSet<string>>();
    foreach (var tf in r1.Triangles)
    {
        if (tf.Vertices.Any(v => v >= r1.Vertices.Length)) continue;
        var vs = tf.Vertices.Select(v => r1.Vertices[v]).ToArray();
        if (vs.Max(v => (int)v.X) - vs.Min(v => (int)v.X) < 1000 || vs.Max(v => (int)v.Z) - vs.Min(v => (int)v.Z) < 1000) continue; // not horizontal
        int sx = (int)Math.Floor(vs.Min(v => (int)v.X) / 1024.0 + 0.01), sz = (int)Math.Floor(vs.Min(v => (int)v.Z) / 1024.0 + 0.01);
        if (sx < 0 || sz < 0 || sx >= o.NumXSectors || sz >= o.NumZSectors) continue;
        var s = o.Sectors[sx, sz];
        float absY = vs.Average(v => -(float)v.Y);
        float fl = (float)new[] { s.Floor.XnZn, s.Floor.XpZn, s.Floor.XnZp, s.Floor.XpZp }.Average() + py, ce = (float)new[] { s.Ceiling.XnZn, s.Ceiling.XpZn, s.Ceiling.XnZp, s.Ceiling.XpZp }.Average() + py;
        bool isFloor = Math.Abs(absY - fl) <= Math.Abs(absY - ce);
        var key = (sx, sz, isFloor); if (!tr4.TryGetValue(key, out var set)) tr4[key] = set = new();
        set.Add(Sig(vs.Select(v => ((int)Math.Round(v.X / 1024.0), (int)Math.Round(v.Z / 1024.0)))));
    }
    foreach (var ((sx, sz, isFloor), set) in tr4)
    {
        var s = o.Sectors[sx, sz]; var surf = isFloor ? s.Floor : s.Ceiling;
        var mine = new HashSet<string>();
        foreach (var f in isFloor ? new[] { SectorFace.Floor, SectorFace.Floor_Triangle2 } : new[] { SectorFace.Ceiling, SectorFace.Ceiling_Triangle2 })
        {
            if (!o.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(sx, sz, f), out var range) || range.Count != 3) continue;
            mine.Add(Sig(Enumerable.Range(range.Start, 3).Select(k => { var p = o.RoomGeometry.VertexPositions[k]; return ((int)Math.Round(p.X / 1024.0), (int)Math.Round(p.Z / 1024.0)); })));
        }
        bool agree = mine.SetEquals(set);
        string kind = $"{(isFloor ? "floor" : "ceiling")} TR4 has {set.Count} triangle(s), ours {mine.Count}";
        var info = isFloor ? o.GetFloorRoomConnectionInfo(new VectorInt2(sx, sz)) : o.GetCeilingRoomConnectionInfo(new VectorInt2(sx, sz));
        Count($"{kind}, our connection={info.AnyType}: " + (agree ? "ours == TR4" : mine.Count == 0 ? "ours has NO triangle there" : "ours DIFFERS from TR4"));
        if (!agree && samples.Count < 14 && isFloor) samples.Add($"R{i} ({sx},{sz}) TR4 {string.Join(" | ", set)}  ours {string.Join(" | ", mine)}  sectorType={s.Type} floorSplit={s.Floor.DiagonalSplit} F[{s.Floor.XnZn},{s.Floor.XpZn},{s.Floor.XnZp},{s.Floor.XpZp}] C[{s.Ceiling.XnZn},{s.Ceiling.XpZn},{s.Ceiling.XnZp},{s.Ceiling.XpZp}] floorSplit2={s.Floor.DiagonalSplit}");
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
foreach (var s in samples) Console.WriteLine(s);
return 0;