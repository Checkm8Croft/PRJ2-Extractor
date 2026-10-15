using System.IO;
using PRJ2_Extractor.Core;

namespace PRJ2_Extractor.Services;

/// <summary>Raised when a level file cannot be read; the message is meant for the user.</summary>
public sealed class LevelLoadException(string message) : Exception(message);

/// <summary>What an export produced.</summary>
public sealed record ExportResult(string Prj2Path, IReadOnlyList<string> Warnings, IReadOnlyList<string> WrittenFiles, TimeSpan Elapsed);

/// <summary>One label / value pair of the level summary.</summary>
public sealed record LevelStat(string Label, string Value);

/// <summary>
/// A loaded TR4 / TR5 level and the operations the UI needs on it. Everything here is free of UI types except the level's
/// texture bitmap, which is frozen after loading so that the background export thread and the UI thread can both read it.
/// </summary>
public sealed class LevelSession : IDisposable
{
    public TrLevel Level { get; }
    public string FilePath { get; }
    public bool IsTr5 { get; }
    public string FormatName => IsTr5 ? "Tomb Raider 5" : "Tomb Raider 4";
    public IReadOnlyList<LevelStat> Stats { get; }

    private LevelSession(TrLevel level, string filePath)
    {
        Level = level;
        FilePath = filePath;
        IsTr5 = filePath.EndsWith(".trc", StringComparison.OrdinalIgnoreCase);
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

    public static bool IsSupportedExtension(string path) =>
        path.EndsWith(".tr4", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".trc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the level on a background thread. <paramref name="progress"/> receives 0-100.</summary>
    public static Task<LevelSession> LoadAsync(string path, IProgress<int>? progress) => Task.Run(() =>
    {
        if (!IsSupportedExtension(path))
            throw new LevelLoadException("This is not a Tomb Raider 4 (.tr4) or Tomb Raider 5 (.trc) level.");

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

    /// <summary>Exports the level as a Tomb Editor project; the room texture atlas (.tga) is written next to it.</summary>
    public Task<ExportResult> ExportPrj2Async(string prj2Path) => Task.Run(() =>
    {
        var started = DateTime.Now;
        var folder = Path.GetDirectoryName(Path.GetFullPath(prj2Path)) ?? ".";
        Directory.CreateDirectory(folder);

        var warnings = Prj2Exporter.Export(Level, prj2Path);

        var written = new List<string> { Path.GetFullPath(prj2Path) };
        written.AddRange(Directory.EnumerateFiles(folder, "*.tga")
            .Where(f => File.GetLastWriteTime(f) >= started.AddSeconds(-2)));
        return new ExportResult(Path.GetFullPath(prj2Path), warnings, written, DateTime.Now - started);
    });

    public void Dispose() => Level.Dispose();
}