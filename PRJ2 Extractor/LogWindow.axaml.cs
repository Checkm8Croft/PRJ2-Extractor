using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using PRJ2_Extractor.ViewModels;

namespace PRJ2_Extractor;

public partial class LogWindow : Window
{
    private readonly MainViewModel _viewModel;

    public LogWindow() : this(new MainViewModel()) { }

    public LogWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // Keep the newest lines in view, like a console.
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Opened += (_, _) => ScrollToEnd();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.FullLogText))
            ScrollToEnd();
    }

    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
}
