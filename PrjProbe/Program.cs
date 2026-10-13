using System.Numerics;
using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.LevelData.SectorEnums;

// Room ambient light: the reference's AmbientLight (Vector3, 1.0 = 128) against the TR4 room colour bytes (R, G, B) and against what our export holds now.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var oursAll = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
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
int exact = 0, near1 = 0, different = 0, oursEqual = 0, n = 0; var delta = new Dictionary<string, int>(); var deltaByType = new Dictionary<string, int>(); var samples = new List<string>(); var distinctRef = new HashSet<string>();
for (int i = 0; i < level.Rooms.Length; i++)
{
    var r = BestRef(i); var o = oursAll[i]; if (r == null || o == null) continue; n++;
    var lr = level.Rooms[i]; var tr4 = new Vector3(lr.Colour.R, lr.Colour.G, lr.Colour.B);
    var refAmb = r.Properties.AmbientLight * 128f; var ourAmb = o.Properties.AmbientLight * 128f;
    bool eq = Math.Abs(refAmb.X - tr4.X) < 0.51 && Math.Abs(refAmb.Y - tr4.Y) < 0.51 && Math.Abs(refAmb.Z - tr4.Z) < 0.51;
    bool nr = Math.Abs(refAmb.X - tr4.X) <= 1.51 && Math.Abs(refAmb.Y - tr4.Y) <= 1.51 && Math.Abs(refAmb.Z - tr4.Z) <= 1.51;
    { var d = $"({tr4.X - refAmb.X:F1},{tr4.Y - refAmb.Y:F1},{tr4.Z - refAmb.Z:F1})"; delta[d] = delta.GetValueOrDefault(d) + 1; var k2 = $"{r.Properties.Type} flags&0x1={((lr.Flags & 1) != 0)}: dR={tr4.X - refAmb.X:F0}"; deltaByType[k2] = deltaByType.GetValueOrDefault(k2) + 1; }
    if (eq) exact++; else if (nr) near1++; else { different++; if (samples.Count < 10) samples.Add($"R{i}: reference ambient*128 = ({refAmb.X:F1},{refAmb.Y:F1},{refAmb.Z:F1})  TR4 colour = ({tr4.X},{tr4.Y},{tr4.Z}) A={lr.Colour.A}  flags=0x{lr.Flags:X}  water={r.Properties.Type}"); }
    if (Math.Abs(ourAmb.X - refAmb.X) < 0.51 && Math.Abs(ourAmb.Y - refAmb.Y) < 0.51 && Math.Abs(ourAmb.Z - refAmb.Z) < 0.51) oursEqual++; else if (samples.Count < 14) samples.Add($"R{i}: ours*128=({ourAmb.X:F1},{ourAmb.Y:F1},{ourAmb.Z:F1}) reference*128=({refAmb.X:F1},{refAmb.Y:F1},{refAmb.Z:F1}) TR4 colour=({tr4.X},{tr4.Y},{tr4.Z}) type={r.Properties.Type} flags=0x{lr.Flags:X} name={r.Name}/{o.Name}");
    distinctRef.Add($"({refAmb.X:F0},{refAmb.Y:F0},{refAmb.Z:F0})");
}
Console.WriteLine($"rooms compared: {n}");
Console.WriteLine($"reference ambient == TR4 colour: exact {exact}, within 1 step {near1}, different {different}");
Console.WriteLine($"our export currently equals the reference ambient in {oursEqual} rooms");
Console.WriteLine($"distinct reference ambient values: {distinctRef.Count}");
foreach (var s in samples) Console.WriteLine("  " + s);
Console.WriteLine("--- TR4 colour minus reference*128, per room (R,G,B) ---"); foreach (var kv in delta.OrderByDescending(k => k.Value).Take(14)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
Console.WriteLine("--- red-channel delta by room type ---"); foreach (var kv in deltaByType.OrderBy(k => k.Key)) Console.WriteLine($"{kv.Value,5}  {kv.Key}");
return 0;