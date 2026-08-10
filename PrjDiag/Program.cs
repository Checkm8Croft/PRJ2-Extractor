using System.IO;
using System.Linq;
using System.Threading;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.Utils;
using PRJ2_Extractor.Core;

var reporter = new ProgressReporterSimple();
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };

using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var warnings = Prj2Exporter.Export(level, @"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2");
Console.WriteLine($"Export warnings: {warnings.Count}");

var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2", reporter, CancellationToken.None, settings);
var orig = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", reporter, CancellationToken.None, settings);

int totalRooms = 0, matchingRooms = 0;
int mismatchSectors = 0, comparedSectors = 0;
var worstRooms = new List<(string name, int mismatches, int total)>();
foreach (var rOrig in orig.Rooms)
{
    if (rOrig == null) continue;
    var rOurs = ours.Rooms.FirstOrDefault(r => r != null && r.Name == rOrig.Name);
    if (rOurs == null) continue;
    totalRooms++;
    if (rOurs.NumXSectors != rOrig.NumXSectors || rOurs.NumZSectors != rOrig.NumZSectors) continue;
    int roomMismatch = 0, roomTotal = 0;
    for (int x = 0; x < rOrig.NumXSectors; x++)
    for (int z = 0; z < rOrig.NumZSectors; z++)
    {
        var so = rOurs.Sectors[x, z];
        var sr = rOrig.Sectors[x, z];
        if (so.IsAnyWall && sr.IsAnyWall) continue;
        roomTotal++; comparedSectors++;
        bool mismatch = Math.Abs(so.Floor.XpZn - sr.Floor.XpZn) > 4 || Math.Abs(so.Floor.XnZn - sr.Floor.XnZn) > 4 ||
            Math.Abs(so.Floor.XnZp - sr.Floor.XnZp) > 4 || Math.Abs(so.Floor.XpZp - sr.Floor.XpZp) > 4 ||
            Math.Abs(so.Ceiling.XpZn - sr.Ceiling.XpZn) > 4 || Math.Abs(so.Ceiling.XnZn - sr.Ceiling.XnZn) > 4 ||
            Math.Abs(so.Ceiling.XnZp - sr.Ceiling.XnZp) > 4 || Math.Abs(so.Ceiling.XpZp - sr.Ceiling.XpZp) > 4;
        if (mismatch) { mismatchSectors++; roomMismatch++; }
    }
    if (roomMismatch == 0) matchingRooms++;
    worstRooms.Add((rOrig.Name, roomMismatch, roomTotal));
}
Console.WriteLine($"Rooms compared: {totalRooms}, fully matching (non-wall): {matchingRooms}");
Console.WriteLine($"Non-wall sectors compared: {comparedSectors}, mismatched: {mismatchSectors} ({(comparedSectors>0?100.0*mismatchSectors/comparedSectors:0):F1}%)");
Console.WriteLine("Worst rooms:");
foreach (var w in worstRooms.OrderByDescending(w => w.mismatches).Take(10))
    Console.WriteLine($"  {w.name}: {w.mismatches}/{w.total}");
return 0;
