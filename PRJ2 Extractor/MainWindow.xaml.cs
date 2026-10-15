using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
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

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.LogEntries.CollectionChanged += (_, _) => ScrollLogToEnd();
        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // "Open with" / drag onto the .exe: the first argument that is a file is the level to open.
        var path = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
        if (path != null)
            _viewModel.OpenDroppedFile(path);
    }

    // ---- drag and drop
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        var file = FirstDroppedFile(e);
        e.Effects = file != null && LevelSession.IsSupportedExtension(file) && !_viewModel.IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
        _viewModel.IsDragOver = e.Effects == DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_DragLeave(object sender, DragEventArgs e) => _viewModel.IsDragOver = false;

    private void Window_Drop(object sender, DragEventArgs e)
    {
        _viewModel.IsDragOver = false;
        var file = FirstDroppedFile(e);
        if (file != null)
            _viewModel.OpenDroppedFile(file);
    }

    private static string? FirstDroppedFile(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 ? files[0] : null;

    // ---- texture preview zoom
    private void FitButton_Click(object sender, RoutedEventArgs e) => FitWidth(maxZoom: 8);

    /// <summary>Scales the atlas so that its width fills the preview area (never above <paramref name="maxZoom"/>).</summary>
    private void FitWidth(double maxZoom)
    {
        if (_viewModel.TextureWidth <= 0) return;
        double available = (PreviewScroller.ViewportWidth > 0 ? PreviewScroller.ViewportWidth : PreviewScroller.ActualWidth) - 24;
        if (available <= 0) return;
        _viewModel.Zoom = Math.Min(maxZoom, available / _viewModel.TextureWidth);
    }

    private void PreviewScroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return; // a plain wheel scrolls the atlas
        _viewModel.Zoom *= e.Delta > 0 ? 1.15 : 1 / 1.15;
        e.Handled = true;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A new level: show its atlas at a size that fits the preview (not magnified more than 2x).
        if (e.PropertyName == nameof(MainViewModel.TextureSource) && _viewModel.HasTexture)
            Dispatcher.BeginInvoke(() => FitWidth(maxZoom: 2), DispatcherPriority.Loaded);
    }

    // ---- activity log
    private void ScrollLogToEnd()
    {
        if (LogList.Items.Count == 0) return;
        Dispatcher.BeginInvoke(() => LogList.ScrollIntoView(LogList.Items[^1]), DispatcherPriority.Background);
    }
}
