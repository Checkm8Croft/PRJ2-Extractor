using System.Threading;
using System.Numerics;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;
using TombLib.LevelData.SectorStructs;
using TombLib.Utils;

// Render-equivalence check for floor/ceiling faces: TexCoord ORDER can differ between two rooms whose faces are built
// with a different vertex order yet render identically. Compare what TombLib actually draws: for each face, the UV that
// ends up on each (X, Z) vertex of its RoomGeometry triangles, ours vs the reference.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = false, IgnoreTextures = false, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
foreach (var r in ours.Concat(refs)) r.BuildGeometry(useLegacyCode: false);

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
bool Close(Vector2 a, Vector2 b) => Math.Abs(a.X - b.X) <= 0.6f && Math.Abs(a.Y - b.Y) <= 0.6f;

Dictionary<(int, int), Vector2>? Rendered(TombLib.LevelData.Room room, int x, int z, SectorFace face)
{
    if (!room.RoomGeometry.VertexRangeLookup.TryGetValue(new SectorFaceIdentity(x, z, face), out var range)) return null;
    var d = new Dictionary<(int, int), Vector2>();
    for (int i = range.Start; i < range.Start + range.Count; i++)
    {
        var p = room.RoomGeometry.VertexPositions[i]; var t = room.RoomGeometry.TriangleTextureAreas[i / 3];
        var uv = (i % 3) switch { 0 => t.TexCoord0, 1 => t.TexCoord1, _ => t.TexCoord2 };
        d[((int)Math.Round(p.X), (int)Math.Round(p.Z))] = uv;
    }
    return d;
}

var tab = new Dictionary<string, int>(); var samples = new List<string>();
void Count(string k) => tab[k] = tab.GetValueOrDefault(k) + 1;
var faces = new[] { SectorFace.Floor, SectorFace.Floor_Triangle2, SectorFace.Ceiling, SectorFace.Ceiling_Triangle2 };
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var o = ours.FirstOrDefault(rr => RoomMatches(rr, r1)); var r = refs.FirstOrDefault(rr => RoomMatches(rr, r1));
    if (o == null || r == null) continue;
    for (int x = 0; x < Math.Min(o.NumXSectors, r.NumXSectors); x++)
    for (int z = 0; z < Math.Min(o.NumZSectors, r.NumZSectors); z++)
    foreach (var f in faces)
    {
        var a = o.Sectors[x, z].GetFaceTexture(f); var b = r.Sectors[x, z].GetFaceTexture(f);
        if (a.TextureIsUnavailable || b.TextureIsUnavailable) continue;
        var ra = Rendered(o, x, z, f); var rb = Rendered(r, x, z, f);
        if (ra == null || rb == null) { Count($"{f}: face missing in geometry ({(ra == null ? "ours" : "ref")})"); continue; }
        bool sameShape = ra.Keys.OrderBy(k => k).SequenceEqual(rb.Keys.OrderBy(k => k));
        bool sameUv = sameShape && ra.All(kv => Close(kv.Value, rb[kv.Key]));
        bool sameOrder = Enumerable.Range(0, 4).All(k => Close(new[] { a.TexCoord0, a.TexCoord1, a.TexCoord2, a.TexCoord3 }[k], new[] { b.TexCoord0, b.TexCoord1, b.TexCoord2, b.TexCoord3 }[k]));
        string kind = a.TexCoord2 == a.TexCoord3 ? "tri" : "quad";
        string diff = "";
        if (sameShape && !ra.All(kv => Close(kv.Value, rb[kv.Key])))
        {
            float minU = ra.Values.Min(v => v.X), maxU = ra.Values.Max(v => v.X), minV = ra.Values.Min(v => v.Y), maxV = ra.Values.Max(v => v.Y);
            bool flipU = ra.All(kv => Close(new Vector2(minU + maxU - kv.Value.X, kv.Value.Y), rb[kv.Key]));
            bool flipV = ra.All(kv => Close(new Vector2(kv.Value.X, minV + maxV - kv.Value.Y), rb[kv.Key]));
            bool flipBoth = ra.All(kv => Close(new Vector2(minU + maxU - kv.Value.X, minV + maxV - kv.Value.Y), rb[kv.Key]));
            bool swapUv = ra.All(kv => Close(new Vector2(minU + (kv.Value.Y - minV) * (maxU - minU) / Math.Max(1, maxV - minV), minV + (kv.Value.X - minU) * (maxV - minV) / Math.Max(1, maxU - minU)), rb[kv.Key]));
            var s = r.Sectors[x, z]; bool floorFace = f == SectorFace.Floor || f == SectorFace.Floor_Triangle2;
            string split = floorFace ? $"floorSplit={s.Floor.DiagonalSplit}/xEqZ={s.Floor.SplitDirectionIsXEqualsZ}" : $"ceilSplit={s.Ceiling.DiagonalSplit}/xEqZ={s.Ceiling.SplitDirectionIsXEqualsZ}";
            diff = (flipU ? " [flipU]" : flipV ? " [flipV]" : flipBoth ? " [flipU+V = rot180]" : swapUv ? " [transpose]" : " [other]") + " " + split;
        }
        string hand = "";
        if (kind == "tri" && sameShape)
        {
            var ks = ra.Keys.OrderBy(k => k).ToArray();
            float W(IEnumerable<(int, int)> q) { var p = q.ToArray(); return (p[1].Item1 - p[0].Item1) * (p[2].Item2 - p[0].Item2) - (p[1].Item2 - p[0].Item2) * (p[2].Item1 - p[0].Item1); }
            var pts = ks.Select(k => (k.Item1, k.Item2)).ToArray();
            float world = W(pts);
            var ua = ks.Select(k => ra[k]).ToArray(); var ub = ks.Select(k => rb[k]).ToArray();
            float Cu(Vector2[] u) => (u[1].X - u[0].X) * (u[2].Y - u[0].Y) - (u[1].Y - u[0].Y) * (u[2].X - u[0].X);
            float sa = Math.Sign(world) * Math.Sign(Cu(ua)), sb = Math.Sign(world) * Math.Sign(Cu(ub));
            hand = $" handedness ours={sa:+0;-0} ref={sb:+0;-0}";
        }
        Count($"{f} [{kind}]{hand}: " + (sameUv ? (sameOrder ? "renders same, TexCoords same" : "renders same, TexCoord order differs (harmless)") : sameShape ? "RENDERS DIFFERENTLY" + diff : "different vertex set"));
        if (samples.Count < 6 && sameShape && !sameUv)
            samples.Add($"R{i} ({x},{z}) {f}  ours {string.Join(" ", ra.OrderBy(k => k.Key).Select(k => $"({k.Key.Item1},{k.Key.Item2})->[{k.Value.X:F0},{k.Value.Y:F0}]"))}  ref {string.Join(" ", rb.OrderBy(k => k.Key).Select(k => $"({k.Key.Item1},{k.Key.Item2})->[{k.Value.X:F0},{k.Value.Y:F0}]"))}");
    }
}
foreach (var kv in tab.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
foreach (var s in samples) Console.WriteLine(s);
return 0;