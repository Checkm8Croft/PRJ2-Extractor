using PRJ2_Extractor.Core;
using System.IO;
using System.Threading;
using TombLib.LevelData.IO;
using TombLib.Utils;

string tr4Path = @"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4";
string prj2Path = @"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2";

using var level = new TrLevel();
byte loadResult = level.Load(tr4Path, new Progress<int>(v => { }));
if (loadResult != 0) { Console.WriteLine($"Load failed: {loadResult}"); return 1; }

var warnings = Prj2Exporter.Export(level, prj2Path);
Console.WriteLine($"Export OK -> {prj2Path}, portal warnings: {warnings.Count}");
foreach (var w in warnings.Take(20)) Console.WriteLine("  " + w);

var reporter = new ProgressReporterSimple();
var loadSettings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var tombLevel = Prj2Loader.LoadFromPrj2(prj2Path, reporter, CancellationToken.None, loadSettings);

int total = 0, illegal = 0;
foreach (var room in tombLevel.Rooms)
{
    if (room == null) continue;
    for (int x = 0; x < room.NumXSectors; x++)
    for (int z = 0; z < room.NumZSectors; z++)
    {
        if (room.Sectors[x, z].IsAnyWall) continue;
        total++;
        if (room.IsIllegalSlope(x, z)) illegal++;
    }
}
Console.WriteLine($"Total non-wall sectors: {total}, IsIllegalSlope: {illegal} ({(total>0?100.0*illegal/total:0):F1}%)");
return 0;
