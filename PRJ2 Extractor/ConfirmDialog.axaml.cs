using Avalonia.Controls;
using Avalonia.Interactivity;

namespace PRJ2_Extractor;

/// <summary>A small yes / no question. "No" is the default button, so pressing Enter never replaces anything.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog() => InitializeComponent();

    public ConfirmDialog(string title, string message) : this()
    {
        Title = title;
        MessageText.Text = message;
    }

    private void Yes_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void No_Click(object? sender, RoutedEventArgs e) => Close(false);
}
