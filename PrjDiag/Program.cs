using PRJ2_Extractor.Core;
using System.IO;
using System.Linq;
using System.Threading;
using TombLib.LevelData;
using TombLib.LevelData.IO;
using TombLib.Utils;

using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
Console.WriteLine($"Raw FlybyCameras[]: {level.FlybyCameras.Count}");

var warnings = Prj2Exporter.Export(level, @"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2");
Console.WriteLine($"Export warnings: {warnings.Count}");
foreach (var w in warnings) Console.WriteLine("  " + w);

var reporter = new ProgressReporterSimple();
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2.prj2", reporter, CancellationToken.None, settings);

int fbTotal = ours.Rooms.Where(r => r != null).Sum(r => r.Objects.OfType<FlybyCameraInstance>().Count());
Console.WriteLine($"Total flyby cameras: {fbTotal}");
foreach (var room in ours.Rooms.Where(r => r != null))
foreach (var fb in room.Objects.OfType<FlybyCameraInstance>())
    Console.WriteLine($"  {room.Name}: Seq={fb.Sequence} Num={fb.Number} Pos=({fb.Position.X:F1},{fb.Position.Y:F1},{fb.Position.Z:F1}) Rot=({fb.RotationX:F1},{fb.RotationY:F1}) Roll={fb.Roll:F1} FOV={fb.Fov:F1} Speed={fb.Speed:F2}");

var orig = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", reporter, CancellationToken.None, settings);
int origCam = orig.Rooms.Where(r => r != null).Sum(r => r.Objects.OfType<CameraInstance>().Count());
Console.WriteLine($"Reference generic 'cameras' (likely mis-imported flybys): {origCam}");
foreach (var room in orig.Rooms.Where(r => r != null))
foreach (var c in room.Objects.OfType<CameraInstance>())
{
    var wp = room.Position + c.Position;
    Console.WriteLine($"  {room.Name}: world=({wp.X:F1},{wp.Y:F1},{wp.Z:F1})");
}
foreach (var room in ours.Rooms.Where(r => r != null))
foreach (var fb in room.Objects.OfType<FlybyCameraInstance>())
{
    var wp = room.Position + fb.Position;
    Console.WriteLine($"  OURS FLYBY {room.Name}: world=({wp.X:F1},{wp.Y:F1},{wp.Z:F1})");
}

int totalRooms = 0, matchingRooms = 0, mismatchSectors = 0, comparedSectors = 0;
foreach (var rOrig in orig.Rooms)
{
    if (rOrig == null) continue;
    var rOurs = ours.Rooms.FirstOrDefault(r => r != null && r.Name == rOrig.Name);
    if (rOurs == null) continue;
    totalRooms++;
    if (rOurs.NumXSectors != rOrig.NumXSectors || rOurs.NumZSectors != rOrig.NumZSectors) continue;
    int roomMismatch = 0;
    for (int x = 0; x < rOrig.NumXSectors; x++)
    for (int z = 0; z < rOrig.NumZSectors; z++)
    {
        var so = rOurs.Sectors[x, z]; var sr = rOrig.Sectors[x, z];
        if (so.IsAnyWall && sr.IsAnyWall) continue;
        comparedSectors++;
        bool mismatch = Math.Abs(so.Floor.XpZn - sr.Floor.XpZn) > 4 || Math.Abs(so.Floor.XnZn - sr.Floor.XnZn) > 4 ||
            Math.Abs(so.Floor.XnZp - sr.Floor.XnZp) > 4 || Math.Abs(so.Floor.XpZp - sr.Floor.XpZp) > 4 ||
            Math.Abs(so.Ceiling.XpZn - sr.Ceiling.XpZn) > 4 || Math.Abs(so.Ceiling.XnZn - sr.Ceiling.XnZn) > 4 ||
            Math.Abs(so.Ceiling.XnZp - sr.Ceiling.XnZp) > 4 || Math.Abs(so.Ceiling.XpZp - sr.Ceiling.XpZp) > 4;
        if (mismatch) { mismatchSectors++; roomMismatch++; }
    }
    if (roomMismatch == 0) matchingRooms++;
}
Console.WriteLine($"Rooms: {totalRooms}, fully matching: {matchingRooms}");
Console.WriteLine($"Sectors: {comparedSectors}, mismatched: {mismatchSectors} ({(comparedSectors>0?100.0*mismatchSectors/comparedSectors:0):F1}%)");
return 0;
