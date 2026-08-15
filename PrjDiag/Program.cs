using PRJ2_Extractor.Core;
using System.IO;

using var level = new TrLevel();
byte result = level.Load(@"C:\Users\Checkm8ra1n\Documents\rich2.trc", new Progress<int>(v => { }));

foreach (var r in level.Rooms)
    foreach (var l in r.Lights)
        if (Math.Abs(l.X - r.X) > 50000 || Math.Abs(l.Z - r.Z) > 50000)
            Console.WriteLine($"Room X={r.X} Z={r.Z} YBottom={r.YBottom} YTop={r.YTop}: light Type={l.LightType} X={l.X} Y={l.Y} Z={l.Z} In={l.In} Out={l.Out} Col=({l.ColourR},{l.ColourG},{l.ColourB})");
return 0;
