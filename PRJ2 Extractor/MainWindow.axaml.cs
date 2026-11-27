using System.ComponentModel;
using System.IO;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using PRJ2_Extractor.Services;
using PRJ2_Extractor.ViewModels;

namespace PRJ2_Extractor;

/// <summary>
/// Main window. All state and commands live in <see cref="MainViewModel"/>; this file only does what needs the visual tree:
/// dropped files, the zoom that depends on the viewport size, and scrolling the activity log.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.Dialogs = new WindowDialogService(this);

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LogEntries.CollectionChanged += (_, _) => ScrollLogToEnd();

        AddHandler(DragDrop.DragOverEvent, Window_DragOver);
        AddHandler(DragDrop.DragLeaveEvent, Window_DragLeave);
        AddHandler(DragDrop.DropEvent, Window_Drop);
        // The wheel has to be seen before the scroll viewer scrolls with it.
        PreviewScroller.AddHandler(PointerWheelChangedEvent, PreviewScroller_PointerWheel, RoutingStrategies.Tunnel);

        Opened += OnOpened;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // "Open with" / drag onto the .exe: the first argument that is a file is the level to open.
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (path != null)
            _viewModel.OpenDroppedFile(path);
    }

    // ---- drag and drop
    private void Window_DragOver(object? sender, DragEventArgs e)
    {
        var file = FirstDroppedFile(e);
        bool accepted = file != null && LevelSession.IsSupportedExtension(file) && !_viewModel.IsBusy;
        e.DragEffects = accepted ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.IsDragOver = accepted;
        e.Handled = true;
    }

    private void Window_DragLeave(object? sender, DragEventArgs e) => _viewModel.IsDragOver = false;

    private void Window_Drop(object? sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        var file = FirstDroppedFile(e);
        if (file != null)
            _viewModel.OpenDroppedFile(file);
        e.Handled = true;
    }

    private static string? FirstDroppedFile(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(item => item.TryGetLocalPath()).FirstOrDefault(path => path != null);

    // ---- texture preview zoom
    private void FitButton_Click(object? sender, RoutedEventArgs e) => FitWidth(maxZoom: 8);

    /// <summary>Scales the atlas so that its width fills the preview area (never above <paramref name="maxZoom"/>).</summary>
    private void FitWidth(double maxZoom)
    {
        if (_viewModel.TextureWidth <= 0) return;
        double available = (PreviewScroller.Viewport.Width > 0 ? PreviewScroller.Viewport.Width : PreviewScroller.Bounds.Width) - 24;
        if (available <= 0) return;
        _viewModel.Zoom = Math.Min(maxZoom, available / _viewModel.TextureWidth);
    }

    private void PreviewScroller_PointerWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return; // a plain wheel scrolls the atlas
        _viewModel.Zoom *= e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new level: show its atlas at a size that fits the preview (not magnified more than 2x).
        if (e.PropertyName == nameof(MainViewModel.TextureSource) && _viewModel.HasTexture)
            Dispatcher.UIThread.Post(() => FitWidth(maxZoom: 2), DispatcherPriority.Loaded);
    }

    private void ShowFullLogs_Click(object? sender, RoutedEventArgs e) =>
        new LogWindow(_viewModel).Show(this);

    // ---- activity log
    private void ScrollLogToEnd() =>
        Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
}
