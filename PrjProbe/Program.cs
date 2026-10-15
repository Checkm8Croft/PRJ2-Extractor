using System.IO;
using PRJ2_Extractor.Services;

// Smoke test of the UI's service layer: load on a worker thread, read the frozen bitmap on the main thread, export from another worker thread.
var progress = new Progress<int>(v => { });
using var session = await LevelSession.LoadAsync(@"C:\Users\Checkm8ra1n\Documents\alexhub2.tr4", progress);
var bmp = session.Level.TextureBitmap!;
Console.WriteLine($"{session.FormatName}: frozen={bmp.IsFrozen}, {bmp.PixelWidth}x{bmp.PixelHeight}; stats: {string.Join(", ", session.Stats.Select(s => $"{s.Label}={s.Value}"))}");
Console.WriteLine($"bitmap readable on main thread: {bmp.PixelWidth > 0}");

string tmp = Path.Combine(Path.GetTempPath(), "ui_smoke"); if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
var result = await session.ExportPrj2Async(Path.Combine(tmp, "smoke.prj2"));
Console.WriteLine($"export: {result.Elapsed.TotalSeconds:F1}s, warnings={result.Warnings.Count}, files={string.Join(", ", result.WrittenFiles.Select(Path.GetFileName))}");
Console.WriteLine($"prj2 size: {new FileInfo(result.Prj2Path).Length}");
var again = await session.ExportPrj2Async(Path.Combine(tmp, "smoke2.prj2"));
Console.WriteLine($"second export from the same session ok: {File.Exists(again.Prj2Path)}");

foreach (var bad in new[] { @"C:\Windows\notepad.exe", @"C:\Users\Checkm8ra1n\Documents\alexhub2_orig.prj2" })
{
    try { await LevelSession.LoadAsync(bad, null); Console.WriteLine($"{Path.GetFileName(bad)}: loaded?!"); }
    catch (LevelLoadException ex) { Console.WriteLine($"{Path.GetFileName(bad)}: {ex.Message}"); }
}
var textFile = Path.Combine(tmp, "fake.tr4"); File.WriteAllText(textFile, "not a level");
try { await LevelSession.LoadAsync(textFile, null); Console.WriteLine("fake.tr4: loaded?!"); }
catch (LevelLoadException ex) { Console.WriteLine($"fake.tr4: {ex.Message}"); }
catch (Exception ex) { Console.WriteLine($"fake.tr4: UNHANDLED {ex.GetType().Name}: {ex.Message}"); }
return 0;