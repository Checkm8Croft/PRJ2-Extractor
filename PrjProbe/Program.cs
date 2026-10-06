using System.Threading;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Models;
using TombLib.LevelData;
using TombLib.LevelData.IO;

// Portal area accuracy: sectors covered by a portal in OUR export vs the reference, per direction. "Extra" sectors become holes in the floor/ceiling or wall.
using var level = new TrLevel();
level.Load(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", new Progress<int>(v => { }));
var settings = new Prj2Loader.Settings { IgnoreWads = true, IgnoreTextures = true, IgnoreSoundsCatalogs = true };
var ours = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_export_test.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
var refs = Prj2Loader.LoadFromPrj2(@"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2", null, CancellationToken.None, settings).Rooms.Where(r => r != null).ToList();
bool RoomMatches(TombLib.LevelData.Room rr, LevelRoom r1) =>
    Math.Abs(rr.Position.X * 1024 - r1.X) < 1100 && Math.Abs(rr.Position.Z * 1024 - r1.Z) < 1100 && Math.Abs(rr.Position.Y + r1.YBottom) < 300;
long both = 0, extra = 0, missing = 0; long fBoth = 0, fExtra = 0, fMissing = 0;
foreach (var r1 in level.Rooms)
{
    var o = ours.FirstOrDefault(rr => RoomMatches(rr, r1)); var r = refs.FirstOrDefault(rr => RoomMatches(rr, r1)); if (o == null || r == null) continue;
    for (int x = 0; x < Math.Min(o.NumXSectors, r.NumXSectors); x++)
    for (int z = 0; z < Math.Min(o.NumZSectors, r.NumZSectors); z++)
    {
        bool a = o.Sectors[x, z].FloorPortal != null, b = r.Sectors[x, z].FloorPortal != null;
        if (a && b) fBoth++; else if (a) fExtra++; else if (b) fMissing++;
        a = o.Sectors[x, z].CeilingPortal != null; b = r.Sectors[x, z].CeilingPortal != null;
        if (a && b) both++; else if (a) extra++; else if (b) missing++;
    }
}
Console.WriteLine($"floor portal sectors: in both {fBoth}, EXTRA in ours {fExtra}, missing in ours {fMissing}");
Console.WriteLine($"ceiling portal sectors: in both {both}, EXTRA in ours {extra}, missing in ours {missing}");
return 0;