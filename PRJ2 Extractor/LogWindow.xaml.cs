using System.Windows;
using System.Windows.Controls;
using PRJ2_Extractor.ViewModels;

namespace PRJ2_Extractor;

public partial class LogWindow : Window
{
    public LogWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void LogTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        LogTextBox.ScrollToEnd();
}
