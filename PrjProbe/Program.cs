using System.Threading;
using System.Numerics;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.Utils;

// Wall texture UV check: for every wall face key that BOTH our exported prj2 and the reference have textured at the same
// sector, compare TexCoord0-3: exact / same UV set but different corner order (rotation or mirror) / different region.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = false, IgnoreTextures = false, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.ToList();
foreach (var rr0 in ours.Where(q => q != null)) rr0.BuildGeometry(useLegacyCode: false);
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;

var paths = System.IO.File.ReadAllLines(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "direct_paths.txt")).Select(l => l.Split('|')).ToDictionary(p => $"{p[0]}|{p[1]}|{p[2]}|{p[3]}", p => p[4]);
var tab = new Dictionary<string, int>(); var samples = new List<string>();
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
bool Close(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) <= 0.6f && Math.Abs(a.Y - b.Y) <= 0.6f;
string Fmt(TextureArea t) => $"[{t.TexCoord0.X:F0},{t.TexCoord0.Y:F0} {t.TexCoord1.X:F0},{t.TexCoord1.Y:F0} {t.TexCoord2.X:F0},{t.TexCoord2.Y:F0} {t.TexCoord3.X:F0},{t.TexCoord3.Y:F0}]";

var faces = new[] {
  SectorFace.Wall_NegativeX_QA, SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_NegativeX_WS,
  SectorFace.Wall_PositiveX_QA, SectorFace.Wall_PositiveX_Middle, SectorFace.Wall_PositiveX_WS,
  SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_NegativeZ_WS,
  SectorFace.Wall_PositiveZ_QA, SectorFace.Wall_PositiveZ_Middle, SectorFace.Wall_PositiveZ_WS };

for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var o = ours[i]; if (o == null) continue;
    var cands = refs.Where(rr => RoomMatches(rr, r1) && rr.NumXSectors == o.NumXSectors && rr.NumZSectors == o.NumZSectors).ToList(); if (cands.Count == 0) continue;
    var r = cands.OrderBy(rr => { double d = 0; for (int xx = 0; xx < o.NumXSectors; xx++) for (int zz = 0; zz < o.NumZSectors; zz++) { var a = o.Sectors[xx, zz]; var b = rr.Sectors[xx, zz]; if (a.Type != SectorType.Floor) continue; d += Math.Min(Math.Abs((a.Floor.XnZn + a.Floor.XpZn + a.Floor.XnZp + a.Floor.XpZp) / 4.0 + o.Position.Y - (b.Floor.XnZn + b.Floor.XpZn + b.Floor.XnZp + b.Floor.XpZp) / 4.0 - rr.Position.Y), 4096); } return d; }).First();
    for (int x = 0; x < Math.Min(o.NumXSectors, r.NumXSectors); x++)
    for (int z = 0; z < Math.Min(o.NumZSectors, r.NumZSectors); z++)
    {
        var so = o.Sectors[x, z].GetFaceTextures(); var sr = r.Sectors[x, z].GetFaceTextures();
        foreach (var f in faces)
        {
            if (!so.TryGetValue(f, out var a) || !sr.TryGetValue(f, out var b)) continue;
            if (a.TextureIsUnavailable || b.TextureIsUnavailable) continue;
            var A = new[] { a.TexCoord0, a.TexCoord1, a.TexCoord2, a.TexCoord3 }; var B = new[] { b.TexCoord0, b.TexCoord1, b.TexCoord2, b.TexCoord3 };
            string tier = f.ToString().EndsWith("QA") ? "QA" : f.ToString().EndsWith("WS") ? "WS" : "Middle";
            bool quadA = a.TexCoord3 != a.TexCoord2 && b.TexCoord3 != b.TexCoord2;
            bool exact = Enumerable.Range(0, 4).All(k => Close(A[k], B[k]));
            if (exact) { Count($"{tier}: exact"); continue; }
            bool sameSet = A.All(u => B.Any(v => Close(u, v))) && B.All(v => A.Any(u => Close(u, v)));
            // find the cyclic shift / mirror that maps ours to the reference
            string how = "none";
            if (sameSet)
            {
                for (int s = 1; s < 4 && how == "none"; s++) if (Enumerable.Range(0, 4).All(k => Close(A[(k + s) % 4], B[k]))) how = $"rotate {s}";
                if (how == "none") for (int s = 0; s < 4 && how == "none"; s++) if (Enumerable.Range(0, 4).All(k => Close(A[((s - k) % 4 + 4) % 4], B[k]))) how = $"mirror(+{s})";
                if (how == "none") how = "other permutation";
                Count($"{tier}: same region, {(a.TexCoord2 == a.TexCoord3 ? "TRIANGLE" : "quad")}, {how}  [face defined in our geometry: {o.IsFaceDefined(x, z, f)}] [ours made by: {(paths.TryGetValue($"{o.Name}|{x}|{z}|{f}", out var pth) ? pth : "no record")}]");
            }
            else {
                float aMinX = A.Min(u => u.X), aMaxX = A.Max(u => u.X), aMinY = A.Min(u => u.Y), aMaxY = A.Max(u => u.Y);
                float bMinX = B.Min(u => u.X), bMaxX = B.Max(u => u.X), bMinY = B.Min(u => u.Y), bMaxY = B.Max(u => u.Y);
                bool aInB = aMinX >= bMinX - 1 && aMaxX <= bMaxX + 1 && aMinY >= bMinY - 1 && aMaxY <= bMaxY + 1;
                bool bInA = bMinX >= aMinX - 1 && bMaxX <= aMaxX + 1 && bMinY >= aMinY - 1 && bMaxY <= aMaxY + 1;
                bool overlap = aMinX < bMaxX && bMinX < aMaxX && aMinY < bMaxY && bMinY < aMaxY;
                string kind = aInB ? "ours is a SUB-rect of ref" : bInA ? "ref is a sub-rect of ours" : overlap ? "partial overlap" : "unrelated (disjoint)";
                if (kind.StartsWith("unrelated"))
                {
                    // is the reference texture present on ANY wall face of this seam in OUR export? (right texture, wrong tier/side)
                    bool isX = f.ToString().Contains("X_"); bool neg = f.ToString().Contains("Negative");
                    int nx = isX ? (neg ? x - 1 : x + 1) : x, nz = isX ? z : (neg ? z - 1 : z + 1);
                    var cand = new List<TextureArea>();
                    foreach (var (sx, sz) in new[] { (x, z), (nx, nz) })
                    {
                        if (sx < 0 || sz < 0 || sx >= o.NumXSectors || sz >= o.NumZSectors) continue;
                        var d = o.Sectors[sx, sz].GetFaceTextures();
                        foreach (var g in faces) if (d.TryGetValue(g, out var c) && !c.TextureIsUnavailable) cand.Add(c);
                    }
                    bool found = cand.Any(c => { var C = new[] { c.TexCoord0, c.TexCoord1, c.TexCoord2, c.TexCoord3 }; return B.All(v => C.Any(u => Close(u, v))); });
                    kind += found ? " -> ref texture IS on another face of this seam in ours (tier/side mismatch)" : " -> ref texture absent from this seam in ours";
                }
                Count($"{tier}: different region, " + kind);
            }
            if (samples.Count < 40 && sameSet) samples.Add($"R{i} ({x},{z}) {f}  ours {Fmt(a)}  ref {Fmt(b)}  -> {how}");
        }
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
int tot = tab.Values.Sum(), ex = tab.Where(k => k.Key.EndsWith("exact")).Sum(k => k.Value);
Console.WriteLine($"compared faces: {tot}; exact: {ex} ({100.0 * ex / Math.Max(1, tot):F1}%)");
foreach (var s in samples) Console.WriteLine(s);
return 0;
