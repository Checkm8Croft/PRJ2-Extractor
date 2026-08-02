using PRJ2_Extractor.Core;
using System.IO;

string tr4Path = @"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4";
string prj2Path = @"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2";

using var level = new TrLevel();
byte loadResult = level.Load(tr4Path, new Progress<int>(v => { }));
if (loadResult != 0) { Console.WriteLine($"Load failed: {loadResult}"); return 1; }

try
{
    var warnings = Prj2Exporter.Export(level, prj2Path);
    var info = new FileInfo(prj2Path);
    Console.WriteLine($"PRJ2 export OK -> {prj2Path} ({info.Length} bytes)");
    Console.WriteLine($"Portal warnings: {warnings.Count}");
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION: {ex}");
    return 1;
}
return 0;
