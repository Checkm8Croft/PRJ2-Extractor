using System.Reflection;
using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// PrjProbe (lone-quad vs border/solid): on seams covered by exactly ONE compiled quad where exactly one side is
// "border or solid" (unreliable heights) and the other side is a real sector, compare the quad's Y range with the
// real sector's floor/ceiling at the shared corners and cross-tab against the reference's tier set.

using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var prj = level.ConvertToPrj(@"C:\Users\Checkm8ra1n\Documents\alexhub2_probe.prj2", saveTga: false);
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var refLevel = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings);
var refRooms = refLevel.Rooms.Where(r => r != null).ToList();

bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 &&
    Math.Abs(rr.Position.Y + r1.YBottom) < 300;

var flags = BindingFlags.NonPublic | BindingFlags.Static;
var mFloor = typeof(TrLevel).GetMethod("GetCornerFloorY", flags)!;
var mCeil = typeof(TrLevel).GetMethod("GetCornerCeilY", flags)!;
var mBorder = typeof(TrLevel).GetMethod("IsBorderOrSolid", flags)!;
int CF(object b, bool xp, bool zp) => (int)mFloor.Invoke(null, new object[] { b, xp, zp })!;
int CC(object b, bool xp, bool zp) => (int)mCeil.Invoke(null, new object[] { b, xp, zp })!;
bool Border(LevelRoom r, int x, int z) => (bool)mBorder.Invoke(null, new object[] { r, x, z })!;

var tab = new Dictionary<string, int>();
void Count(string k) { tab[k] = tab.GetValueOrDefault(k) + 1; }
var samples = new Dictionary<string, List<string>>();

for (int i = 0; i < level.Rooms.Length; i++)
{
    var r1 = level.Rooms[i];
    var pr = prj.Rooms[i];
    var refRoom = refRooms.FirstOrDefault(rr => RoomMatches(rr, r1));
    if (refRoom == null) continue;

    var seams = new Dictionary<(int, int, bool), List<(int minY, int maxY)>>();
    foreach (var face in r1.Rectangles.Concat(r1.Triangles))
    {
        var vs = face.Vertices.Where(v => v < r1.Vertices.Length).Select(v => r1.Vertices[v]).ToArray();
        if (vs.Length != face.Vertices.Length || vs.Length == 0) continue;
        int minX = vs.Min(v => (int)v.X), maxX = vs.Max(v => (int)v.X);
        int minY = vs.Min(v => (int)v.Y), maxY = vs.Max(v => (int)v.Y);
        int minZ = vs.Min(v => (int)v.Z), maxZ = vs.Max(v => (int)v.Z);
        double avgX = vs.Average(v => (double)v.X), avgZ = vs.Average(v => (double)v.Z);
        bool xWall = Math.Abs(maxX - minX) <= 8, zWall = Math.Abs(maxZ - minZ) <= 8;
        void Add((int, int, bool) k) { if (!seams.TryGetValue(k, out var l)) seams[k] = l = new(); l.Add((minY, maxY)); }
        if (xWall)
        {
            int sx = (int)Math.Round(avgX / 1024.0);
            if (sx <= 0 || sx >= pr.XSize) continue;
            for (int z = Math.Clamp(minZ / 1024, 0, pr.ZSize - 1); z <= Math.Clamp((maxZ - 1) / 1024, 0, pr.ZSize - 1); z++) Add((sx, z, true));
        }
        else if (zWall)
        {
            int sz = (int)Math.Round(avgZ / 1024.0);
            if (sz <= 0 || sz >= pr.ZSize) continue;
            for (int x = Math.Clamp(minX / 1024, 0, pr.XSize - 1); x <= Math.Clamp((maxX - 1) / 1024, 0, pr.XSize - 1); x++) Add((x, sz, false));
        }
    }

    foreach (var ((ox, oz, isX), quads) in seams)
    {
        if (quads.Count != 1) continue;
        if (ox >= refRoom.NumXSectors || oz >= refRoom.NumZSectors) continue;
        int nx = isX ? ox - 1 : ox, nz = isX ? oz : oz - 1;
        bool ownB = Border(r1, ox, oz), neighB = Border(r1, nx, nz);
        if (ownB == neighB) continue;                 // need exactly one reliable side
        int rx = ownB ? nx : ox, rz = ownB ? nz : oz; // reliable sector
        int rt = rx * pr.ZSize + rz;
        if (rt < 0 || rt >= pr.Blocks.Length) continue;
        var rb = pr.Blocks[rt];
        // reliable sector's corners on the shared seam
        bool relIsOwn = !ownB;
        bool xp0, zp0, xp1, zp1;
        if (isX) { xp0 = xp1 = !relIsOwn; zp0 = false; zp1 = true; }     // own's -X side / neighbor's +X side
        else     { zp0 = zp1 = !relIsOwn; xp0 = false; xp1 = true; }
        int f0 = CF(rb, xp0, zp0), f1 = CF(rb, xp1, zp1), c0 = CC(rb, xp0, zp0), c1 = CC(rb, xp1, zp1);
        int floorLo = Math.Min(f0, f1), floorHi = Math.Max(f0, f1), ceilLo = Math.Min(c0, c1), ceilHi = Math.Max(c0, c1);

        var q = quads[0];
        // Y positive-down: floor Y is the larger value, ceiling Y the smaller one.
        bool touchesFloor = q.maxY >= floorLo - 8 && q.maxY <= floorHi + 8;
        bool touchesCeil = q.minY >= ceilLo - 8 && q.minY <= ceilHi + 8;
        string geom = touchesFloor && touchesCeil ? "spans floor+ceil" : touchesFloor ? "touches floor only" : touchesCeil ? "touches ceil only" : "floats";

        var so = refRoom.Sectors[ox, oz].GetFaceTextures(); var sn = refRoom.Sectors[nx, nz].GetFaceTextures();
        bool R(SectorFace a, SectorFace b) => so.ContainsKey(a) || sn.ContainsKey(b);
        bool qa = isX ? R(SectorFace.Wall_NegativeX_QA, SectorFace.Wall_PositiveX_QA) : R(SectorFace.Wall_NegativeZ_QA, SectorFace.Wall_PositiveZ_QA);
        bool ws = isX ? R(SectorFace.Wall_NegativeX_WS, SectorFace.Wall_PositiveX_WS) : R(SectorFace.Wall_NegativeZ_WS, SectorFace.Wall_PositiveZ_WS);
        bool md = isX ? R(SectorFace.Wall_NegativeX_Middle, SectorFace.Wall_PositiveX_Middle) : R(SectorFace.Wall_NegativeZ_Middle, SectorFace.Wall_PositiveZ_Middle);
        string set = $"{(qa ? "QA" : "")}{(ws ? "WS" : "")}{(md ? "Mid" : "")}"; if (set == "") set = "none";
        string key = $"{geom,-20} ref={set}";
        Count(key);
        if (!samples.TryGetValue(key, out var l)) samples[key] = l = new();
        if (l.Count < 3) l.Add($"R{i} ({ox},{oz}){(isX ? "X" : "Z")} quad {q.minY}..{q.maxY}  relFloor {floorLo}..{floorHi} relCeil {ceilLo}..{ceilHi}");
    }
}

foreach (var kv in tab.OrderBy(k => k.Key.Split("ref=")[0]).ThenByDescending(k => k.Value))
{
    Console.WriteLine($"{kv.Value,5}  {kv.Key}");
}
Console.WriteLine("--- samples ---");
foreach (var kv in samples.OrderBy(k => k.Key)) foreach (var s in kv.Value) Console.WriteLine($"{kv.Key.Trim(),-34} {s}");
return 0;
