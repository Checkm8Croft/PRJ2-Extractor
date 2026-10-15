using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PRJ2_Extractor.Core;
using PRJ2_Extractor.Services;

namespace PRJ2_Extractor.ViewModels;

public enum LogLevel { Info, Success, Warning, Error }

public sealed record LogEntry(string Time, LogLevel Level, string Message);

/// <summary>State and commands of the main window: open a TR4/TR5 level, preview its textures, export a Tomb Editor .prj2.</summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string Version = "0.2";
    private const int MaxWarningsLogged = 60;

    private LevelSession? _session;
    private string _levelName = "";
    private string _levelFolder = "";
    private string _levelFormat = "";
    private IReadOnlyList<LevelStat> _stats = [];
    private BitmapSource? _textureSource;
    private double _zoom = 1.0;
    private string _outputFolder = "";
    private string _outputName = "";
    private bool _isBusy;
    private bool _isIndeterminate;
    private int _progress;
    private string _busyText = "";
    private string _statusText = "Open a Tomb Raider 4 or 5 level to begin.";
    private string? _lastExportPath;
    private bool _isDragOver;

    public MainViewModel()
    {
        OpenLevelCommand = new RelayCommand(OpenLevelDialog, () => !IsBusy);
        CloseLevelCommand = new RelayCommand(CloseLevel, () => HasLevel && !IsBusy);
        BrowseOutputCommand = new RelayCommand(BrowseOutput, () => HasLevel && !IsBusy);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => HasLevel && !IsBusy);
        SaveTgaCommand = new RelayCommand(SaveTga, () => TextureSource != null && !IsBusy);
        ShowInFolderCommand = new RelayCommand(ShowInFolder, () => LastExportPath != null);
        ClearLogCommand = new RelayCommand(() => LogEntries.Clear(), () => LogEntries.Count > 0);
        ZoomInCommand = new RelayCommand(() => Zoom *= 1.25);
        ZoomOutCommand = new RelayCommand(() => Zoom /= 1.25);
        ActualSizeCommand = new RelayCommand(() => Zoom = 1.0);
    }

    // ---- level
    public bool HasLevel => _session != null;
    public string LevelName { get => _levelName; private set => SetProperty(ref _levelName, value); }
    public string LevelFolder { get => _levelFolder; private set => SetProperty(ref _levelFolder, value); }
    public string LevelFormat { get => _levelFormat; private set => SetProperty(ref _levelFormat, value); }
    public IReadOnlyList<LevelStat> Stats { get => _stats; private set => SetProperty(ref _stats, value); }
    public string WindowTitle => HasLevel ? $"{LevelName} - PRJ2 Extractor" : "PRJ2 Extractor";
    public string VersionText => $"v{Version}";

    // ---- texture preview
    public BitmapSource? TextureSource
    {
        get => _textureSource;
        private set
        {
            if (SetProperty(ref _textureSource, value))
            {
                OnPropertyChanged(nameof(HasTexture));
                OnPropertyChanged(nameof(TextureWidth));
                OnPropertyChanged(nameof(TextureHeight));
                OnPropertyChanged(nameof(TextureCaption));
            }
        }
    }
    public bool HasTexture => _textureSource != null;
    public double TextureWidth => _textureSource?.PixelWidth ?? 0;
    public double TextureHeight => _textureSource?.PixelHeight ?? 0;
    public string TextureCaption => _textureSource == null
        ? "No texture loaded"
        : $"{_textureSource.PixelWidth} x {_textureSource.PixelHeight} px  -  {_textureSource.PixelHeight / 256} tiles";

    public double Zoom
    {
        get => _zoom;
        set
        {
            value = Math.Clamp(value, 0.25, 8.0);
            if (SetProperty(ref _zoom, value)) OnPropertyChanged(nameof(ZoomText));
        }
    }
    public string ZoomText => $"{Math.Round(_zoom * 100)}%";

    // ---- export
    public string OutputFolder { get => _outputFolder; set { if (SetProperty(ref _outputFolder, value)) OnPropertyChanged(nameof(OutputSummary)); } }
    public string OutputName { get => _outputName; set { if (SetProperty(ref _outputName, value)) OnPropertyChanged(nameof(OutputSummary)); } }
    public string OutputSummary => string.IsNullOrWhiteSpace(OutputName) ? "" : $"{OutputName.Trim()}.prj2  +  texture atlas (.tga)";
    public string? LastExportPath
    {
        get => _lastExportPath;
        private set { if (SetProperty(ref _lastExportPath, value)) OnPropertyChanged(nameof(HasExport)); }
    }
    public bool HasExport => _lastExportPath != null;

    // ---- activity
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEditOutput));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }
    public bool CanEditOutput => HasLevel && !IsBusy;
    public bool IsIndeterminate { get => _isIndeterminate; private set => SetProperty(ref _isIndeterminate, value); }
    public int Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string BusyText { get => _busyText; private set => SetProperty(ref _busyText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsDragOver { get => _isDragOver; set => SetProperty(ref _isDragOver, value); }

    public ObservableCollection<LogEntry> LogEntries { get; } = [];

    public ICommand OpenLevelCommand { get; }
    public ICommand CloseLevelCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand SaveTgaCommand { get; }
    public ICommand ShowInFolderCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ActualSizeCommand { get; }

    // ---- opening
    private void OpenLevelDialog()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a Tomb Raider level",
            Filter = "Tomb Raider levels (*.tr4;*.trc)|*.tr4;*.trc|Tomb Raider 4 (*.tr4)|*.tr4|Tomb Raider 5 (*.trc)|*.trc|All files (*.*)|*.*",
            InitialDirectory = HasLevel && Directory.Exists(LevelFolder) ? LevelFolder : ""
        };
        if (dialog.ShowDialog() == true)
            _ = LoadLevelAsync(dialog.FileName);
    }

    /// <summary>Called by the window for a file dropped on it or passed on the command line.</summary>
    public void OpenDroppedFile(string path)
    {
        if (IsBusy) return;
        if (!LevelSession.IsSupportedExtension(path))
        {
            Log(LogLevel.Error, $"'{Path.GetFileName(path)}' is not a .tr4 or .trc level.");
            StatusText = "That file type is not supported.";
            return;
        }
        _ = LoadLevelAsync(path);
    }

    public async Task LoadLevelAsync(string path)
    {
        if (IsBusy) return;
        BeginBusy($"Reading {Path.GetFileName(path)}...", indeterminate: false);
        try
        {
            var session = await LevelSession.LoadAsync(path, new Progress<int>(v => Progress = v));
            ReplaceSession(session);
            Log(LogLevel.Success, $"Opened {Path.GetFileName(path)} ({session.FormatName}): {session.Level.NumRooms} rooms.");
            StatusText = $"{LevelName} is ready to export.";
        }
        catch (LevelLoadException ex)
        {
            Log(LogLevel.Error, $"{Path.GetFileName(path)}: {ex.Message}");
            StatusText = "The level could not be opened.";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"{Path.GetFileName(path)}: unexpected error: {ex.Message}");
            StatusText = "The level could not be opened.";
        }
        finally
        {
            EndBusy();
        }
    }

    private void ReplaceSession(LevelSession session)
    {
        _session?.Dispose();
        _session = session;

        LevelName = Path.GetFileNameWithoutExtension(session.FilePath);
        LevelFolder = Path.GetDirectoryName(session.FilePath) ?? "";
        LevelFormat = session.FormatName;
        Stats = session.Stats;
        TextureSource = session.Level.TextureBitmap;
        Zoom = 1.0;
        OutputFolder = LevelFolder;
        OutputName = LevelName;
        LastExportPath = null;

        OnPropertyChanged(nameof(HasLevel));
        OnPropertyChanged(nameof(CanEditOutput));
        OnPropertyChanged(nameof(WindowTitle));
        CommandManager.InvalidateRequerySuggested();
    }

    private void CloseLevel()
    {
        _session?.Dispose();
        _session = null;
        LevelName = LevelFolder = LevelFormat = "";
        Stats = [];
        TextureSource = null;
        OutputFolder = OutputName = "";
        LastExportPath = null;
        StatusText = "Open a Tomb Raider 4 or 5 level to begin.";
        OnPropertyChanged(nameof(HasLevel));
        OnPropertyChanged(nameof(CanEditOutput));
        OnPropertyChanged(nameof(WindowTitle));
        CommandManager.InvalidateRequerySuggested();
    }

    // ---- export
    private void BrowseOutput()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose where to save the Tomb Editor project",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : LevelFolder
        };
        if (dialog.ShowDialog() == true)
            OutputFolder = dialog.FolderName;
    }

    private string? ValidateOutput(out string prj2Path)
    {
        prj2Path = "";
        var name = OutputName.Trim();
        if (name.Length == 0) return "Enter a name for the project.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "The project name contains characters that a file name cannot have.";
        if (string.IsNullOrWhiteSpace(OutputFolder)) return "Choose an output folder.";
        try
        {
            prj2Path = Path.GetFullPath(Path.Combine(OutputFolder.Trim(), name + ".prj2"));
        }
        catch (Exception)
        {
            return "The output folder is not a valid path.";
        }
        return null;
    }

    private async Task ExportAsync()
    {
        if (_session == null) return;

        var problem = ValidateOutput(out var prj2Path);
        if (problem != null)
        {
            Log(LogLevel.Error, problem);
            StatusText = problem;
            return;
        }

        if (File.Exists(prj2Path))
        {
            var answer = MessageBox.Show($"{Path.GetFileName(prj2Path)} already exists in this folder.\n\nReplace it?",
                "Replace project?", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
        }

        BeginBusy("Exporting the Tomb Editor project...", indeterminate: true);
        Log(LogLevel.Info, $"Exporting to {prj2Path}");
        try
        {
            var result = await _session.ExportPrj2Async(prj2Path);
            LastExportPath = result.Prj2Path;

            var others = result.WrittenFiles.Where(f => !string.Equals(f, result.Prj2Path, StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFileName).ToList();
            var extra = others.Count > 0 ? $" and {string.Join(", ", others)}" : "";
            Log(LogLevel.Success, $"Exported {Path.GetFileName(result.Prj2Path)}{extra} in {result.Elapsed.TotalSeconds:F1} s.");

            if (result.Warnings.Count > 0)
            {
                Log(LogLevel.Warning, $"{result.Warnings.Count} warning(s) during the export:");
                foreach (var warning in result.Warnings.Take(MaxWarningsLogged))
                    Log(LogLevel.Warning, warning);
                if (result.Warnings.Count > MaxWarningsLogged)
                    Log(LogLevel.Warning, $"...and {result.Warnings.Count - MaxWarningsLogged} more.");
            }

            StatusText = result.Warnings.Count == 0
                ? $"Exported {Path.GetFileName(result.Prj2Path)}."
                : $"Exported {Path.GetFileName(result.Prj2Path)} with {result.Warnings.Count} warning(s).";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"The export failed: {ex.Message}");
            StatusText = "The export failed.";
        }
        finally
        {
            EndBusy();
        }
    }

    private void ShowInFolder()
    {
        if (LastExportPath == null || !File.Exists(LastExportPath)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LastExportPath}\"") { UseShellExecute = true });
    }

    // ---- textures
    private void SaveTga()
    {
        if (_session?.Level.TextureBitmap is not { PixelWidth: > 0, PixelHeight: > 0 } bitmap) return;

        var dialog = new SaveFileDialog
        {
            Title = "Save the level's texture atlas",
            DefaultExt = "tga",
            Filter = "Targa image (*.tga)|*.tga",
            FileName = $"{LevelName}_textures.tga",
            InitialDirectory = Directory.Exists(OutputFolder) ? OutputFolder : LevelFolder
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            TgaWriter.Save(bitmap, dialog.FileName);
            Log(LogLevel.Success, $"Saved {Path.GetFileName(dialog.FileName)} ({bitmap.PixelWidth} x {bitmap.PixelHeight} px).");
            StatusText = $"Saved {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not save the texture: {ex.Message}");
        }
    }

    // ---- helpers
    private void BeginBusy(string text, bool indeterminate)
    {
        BusyText = text;
        StatusText = text;
        Progress = 0;
        IsIndeterminate = indeterminate;
        IsBusy = true;
    }

    private void EndBusy()
    {
        IsBusy = false;
        IsIndeterminate = false;
        Progress = 0;
        BusyText = "";
    }

    private void Log(LogLevel level, string message) =>
        LogEntries.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), level, message));

    public void Dispose() => _session?.Dispose();
}
