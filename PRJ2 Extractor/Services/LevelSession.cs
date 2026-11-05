using System.IO;
using System.Windows.Media.Imaging;
using PRJ2_Extractor.Core;

namespace PRJ2_Extractor.Services;

/// <summary>Raised when a level file cannot be read; the message is meant for the user.</summary>
public sealed class LevelLoadException(string message) : Exception(message);

/// <summary>What an export produced. <paramref name="Notes"/> are informational lines (not problems) about what was converted.</summary>
public sealed record ExportResult(string Prj2Path, IReadOnlyList<string> Warnings, IReadOnlyList<string> WrittenFiles, TimeSpan Elapsed,
    IReadOnlyList<string>? Notes = null);

/// <summary>One label / value pair of the level summary.</summary>
public sealed record LevelStat(string Label, string Value);

/// <summary>
/// A loaded Tomb Raider 4 / 5 or TombEngine level and the operations the UI needs on it. Everything here is free of UI types except the
/// texture preview bitmap, which is frozen after loading so that the background export thread and the UI thread can both read it.
/// </summary>
public sealed class LevelSession : IDisposable
{
    /// <summary>The TR4 / TR5 level, or null for a TombEngine level.</summary>
    public TrLevel? Level { get; }
    /// <summary>The TombEngine level, or null for a TR4 / TR5 level.</summary>
    public TenLevel? Ten { get; }
    public string FilePath { get; }
    public bool IsTen => Ten != null;
    public bool IsTr5 { get; }
    public string FormatName => Ten != null ? $"TombEngine {Ten.VersionText}"
        : IsTr5 ? "Tomb Raider 5" : Level!.Ng != null ? "Tomb Raider 4 (TRNG)" : "Tomb Raider 4";
    public int RoomCount => Ten?.Rooms.Count ?? Level!.NumRooms;
    public IReadOnlyList<LevelStat> Stats { get; }

    /// <summary>First texture atlas of the level for the preview (null when there is none or it cannot be decoded).</summary>
    public BitmapSource? TextureBitmap { get; }
    /// <summary>True when the atlas is a stack of 256 x 256 tiles (TR4 / TR5); TombEngine atlases are single large pages.</summary>
    public bool TextureIsTiled => Ten == null;

    private LevelSession(TrLevel level, string filePath)
    {
        Level = level;
        FilePath = filePath;
        IsTr5 = filePath.EndsWith(".trc", StringComparison.OrdinalIgnoreCase);
        TextureBitmap = level.TextureBitmap;
        Stats =
        [
            new("Rooms", level.NumRooms.ToString("N0")),
            new("Moveables", level.NumMoveables.ToString("N0")),
            new("Static meshes", level.NumStatics.ToString("N0")),
            new("Room texture tiles", level.NumRoomTextiles.ToString("N0")),
            new("Object texture tiles", level.NumObjTextiles.ToString("N0")),
            new("Object textures", level.ObjectTextures.Length.ToString("N0")),
        ];
    }

    private LevelSession(TenLevel ten, string filePath)
    {
        Ten = ten;
        FilePath = filePath;
        TextureBitmap = DecodeAtlas(ten);
        Stats =
        [
            new("Rooms", ten.Rooms.Count.ToString("N0")),
            new("Moveables", (ten.Items.Count + ten.AiObjects.Count).ToString("N0")),
            new("Static meshes", ten.Rooms.Sum(r => r.Statics.Count).ToString("N0")),
            new("Lights", ten.Rooms.Sum(r => r.Lights.Count).ToString("N0")),
            new("Cameras and flybys", (ten.Cameras.Count + ten.FlybyCameras.Count).ToString("N0")),
            new("Trigger sectors", ten.Rooms.Sum(r => r.Sectors.Count(s => s.TriggerIndex >= 0)).ToString("N0")),
            new("Room texture atlases", ten.RoomAtlases.Count.ToString("N0")),
            new("Animated texture atlases", ten.AnimatedAtlases.Count.ToString("N0")),
        ];
    }

    /// <summary>PNG atlases only; DDS (BC1 / BC3) ones are not decoded, so there is no preview for them.</summary>
    private static BitmapSource? DecodeAtlas(TenLevel ten)
    {
        var blob = ten.RoomAtlases.FirstOrDefault()?.ColorMap;
        if (blob == null || blob.Length < 8 || blob[0] != 0x89 || blob[1] != 0x50 || blob[2] != 0x4E || blob[3] != 0x47) return null;
        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(blob), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool IsSupportedExtension(string path) =>
        path.EndsWith(".tr4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".trc", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".ten", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the level on a background thread. <paramref name="progress"/> receives 0-100.</summary>
    public static Task<LevelSession> LoadAsync(string path, IProgress<int>? progress) => Task.Run(() =>
    {
        if (!IsSupportedExtension(path))
            throw new LevelLoadException("This is not a Tomb Raider 4 (.tr4), Tomb Raider 5 (.trc) or TombEngine (.ten) level.");

        if (path.EndsWith(".ten", StringComparison.OrdinalIgnoreCase))
            return LoadTen(path, progress);

        var level = new TrLevel();
        byte result;
        try
        {
            result = level.Load(path, progress);
        }
        catch (EndOfStreamException)
        {
            level.Dispose();
            throw new LevelLoadException("The file ends unexpectedly; it is truncated or damaged.");
        }
        catch (Exception ex)
        {
            level.Dispose();
            throw new LevelLoadException($"The level could not be read: {ex.Message}");
        }

        if (result != 0)
        {
            level.Dispose();
            throw new LevelLoadException(result switch
            {
                1 => "The file does not exist.",
                2 => "The Tomb Raider 4/5 signature was not found: this is not a level file.",
                3 => "The level is encrypted and cannot be read.",
                _ => "The level could not be read (damaged or unsupported file)."
            });
        }

        // The bitmap was created on this worker thread; freezing it makes it readable from any thread.
        if (level.TextureBitmap is { IsFrozen: false } bitmap)
            bitmap.Freeze();

        return new LevelSession(level, path);
    });

    private static LevelSession LoadTen(string path, IProgress<int>? progress)
    {
        if (!File.Exists(path)) throw new LevelLoadException("The file does not exist.");
        var ten = new TenLevel();
        string? error;
        try
        {
            error = ten.Load(path, progress);
        }
        catch (Exception ex)
        {
            throw new LevelLoadException($"The level could not be read: {ex.Message}");
        }
        if (error != null) throw new LevelLoadException(error);
        return new LevelSession(ten, path);
    }

    /// <summary>Exports the level as a Tomb Editor project; the room textures are written next to it (a .tga atlas for TR4 / TR5, a folder of .png atlases for TombEngine).</summary>
    public Task<ExportResult> ExportPrj2Async(string prj2Path) => Task.Run(() =>
    {
        var started = DateTime.Now;
        var fullPath = Path.GetFullPath(prj2Path);
        var folder = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(folder);

        var written = new List<string> { fullPath };
        if (Ten != null)
        {
            var all = TenExporter.Export(Ten, prj2Path);
            var notes = all.Where(w => w.StartsWith("Info:", StringComparison.Ordinal)).Select(w => w[5..].Trim()).ToList();
            var warnings = all.Where(w => !w.StartsWith("Info:", StringComparison.Ordinal)).ToList();

            var textures = Path.Combine(folder, Path.GetFileNameWithoutExtension(fullPath) + "_textures");
            if (Directory.Exists(textures))
                written.AddRange(Directory.EnumerateFiles(textures, "*.png").Where(f => File.GetLastWriteTime(f) >= started.AddSeconds(-2)));
            return new ExportResult(fullPath, warnings, written, DateTime.Now - started, notes);
        }

        var trWarnings = Prj2Exporter.Export(Level!, prj2Path);
        written.AddRange(Directory.EnumerateFiles(folder, "*.tga")
            .Where(f => File.GetLastWriteTime(f) >= started.AddSeconds(-2)));
        return new ExportResult(fullPath, trWarnings, written, DateTime.Now - started);
    });

    public void Dispose() => Level?.Dispose();
}
