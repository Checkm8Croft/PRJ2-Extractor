using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using PRJ2_Extractor.Core;

namespace PRJ2_Extractor;

public partial class MainWindow : Window
{
    private const double ProgramVersion = 0.80;

    private TrLevel? _level;
    private AktrekkerPrj? _aktrekker;
    private string _lastTr4Path = "";

    public MainWindow()
    {
        InitializeComponent();
        Title = $"TR4 to PRJ  v{ProgramVersion:F2}";
        SetPlaceholderTexture();
    }

    private void SetPlaceholderTexture()
    {
        var bmp = new WriteableBitmap(256, 256, 96, 96, PixelFormats.Bgr24, null);
        var pixels = new byte[256 * 256 * 3];
        for (int i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 255;
        }
        bmp.WritePixels(new Int32Rect(0, 0, 256, 256), pixels, 256 * 3, 0);
        TextureImage.Source = bmp;
    }

    private void OpenTr4_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            DefaultExt = ".tr4",
            Filter = "Tomb Raider 4 Files (*.tr4)|*.tr4|All files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadTr4File(dlg.FileName);
    }

    private void LoadTr4File(string path)
    {
        if (!path.EndsWith(".tr4", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Not a TR4 file!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            _level?.Dispose();
            _level = null;
            UnloadAktrekker();

            _level = new TrLevel();
            byte r;
            try
            {
                var progress = new Progress<int>(v => LoadProgressBar.Value = v);
                r = _level.Load(path, progress);
            }
            catch (EndOfStreamException)
            {
                r = 4;
            }

            LoadProgressBar.Value = 0;

            switch (r)
            {
                case 0:
                    break;
                case 1:
                    MessageBox.Show("File does not exist!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ClearLevel();
                    return;
                case 2:
                    MessageBox.Show("TR4 signature not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ClearLevel();
                    return;
                case 3:
                    MessageBox.Show("Encrypted TR4 file!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ClearLevel();
                    return;
                default:
                    MessageBox.Show("Error reading TR4!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    ClearLevel();
                    return;
            }

            if (_level.TextureBitmap != null)
            {
                TextureImage.Source = _level.TextureBitmap;
                TextureImage.Height = _level.TextureBitmap.PixelHeight;
            }

            _lastTr4Path = path;
            StatusText.Text = path;
            UpdateUiState();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error reading TR4: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            ClearLevel();
        }
    }

    private void ClearLevel()
    {
        _level?.Dispose();
        _level = null;
        SetPlaceholderTexture();
        StatusText.Text = "";
        UpdateUiState();
    }

    private void LoadPrj_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            DefaultExt = ".prj",
            Filter = "TRLE Project Files (*.prj)|*.prj|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            LoadPrjFile(dlg.FileName);
    }

    private void LoadPrjFile(string path)
    {
        if (!path.EndsWith(".prj", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Not a PRJ file!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_level == null)
        {
            MessageBox.Show("TR4 file not loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        byte r;
        try
        {
            UnloadAktrekker();
            _aktrekker = new AktrekkerPrj(0, 300);
            try
            {
                r = _aktrekker.Load(path);
            }
            catch (EndOfStreamException)
            {
                r = 2;
            }
        }
        catch (Exception)
        {
            r = 2;
        }

        if (r == 0)
        {
            var test = _level.ConvertToPrj("", saveTga: false);
            if (!test.IsCompatible(_aktrekker!))
                r = 3;
        }

        switch (r)
        {
            case 0:
                UpdateUiState();
                return;
            case 1:
                MessageBox.Show("PRJ signature not found!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            case 2:
                MessageBox.Show("Error reading PRJ!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
            case 3:
                MessageBox.Show("Incompatible PRJ!", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }

        UnloadAktrekker();
    }

    private void UnloadPrj_Click(object sender, RoutedEventArgs e) => UnloadAktrekker();

    private void UnloadAktrekker()
    {
        _aktrekker?.Dispose();
        _aktrekker = null;
        UpdateUiState();
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_level == null) return;

        var dlg = new SaveFileDialog
        {
            DefaultExt = "prj",
            Filter = "TRLE Project Files (*.prj)|*.prj",
            FileName = string.IsNullOrEmpty(_lastTr4Path)
                ? "output"
                : Path.GetFileNameWithoutExtension(_lastTr4Path)
        };

        if (dlg.ShowDialog() != true) return;

        var p = _level.ConvertToPrj(dlg.FileName);
        _level.MakeDoors(p, Tr2PrjLinksMenuItem.IsChecked);

        string extra = "";
        if (p.InvalidBlockHeights)
        {
            var reportPath = Path.ChangeExtension(dlg.FileName, ".txt");
            File.WriteAllLines(reportPath, p.InvalidHeights);
            extra = Environment.NewLine + "Error report created: " + Path.GetFileName(reportPath);
        }

        if (_aktrekker != null && p.IsCompatible(_aktrekker))
        {
            if (CopyDoorsCheckBox.IsChecked == true) p.CopyDoorsFromPrj(_aktrekker);
            if (CopyTexturesCheckBox.IsChecked == true) p.CopyTexFromPrj(_aktrekker);
            if (CopyLightsCheckBox.IsChecked == true) p.CopyLightsFromPrj(_aktrekker);
        }

        p.Save(dlg.FileName);
        MessageBox.Show($"{Path.GetFileName(dlg.FileName)} saved.{extra}", "Information",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveTga_Click(object sender, RoutedEventArgs e)
    {
        if (_level?.TextureBitmap == null) return;
        if (_level.TextureBitmap.PixelWidth == 0 || _level.TextureBitmap.PixelHeight == 0) return;

        var dlg = new SaveFileDialog
        {
            DefaultExt = "tga",
            Filter = "TrueVision Targa Files (*.tga)|*.tga"
        };
        if (dlg.ShowDialog() == true)
            TgaWriter.Save(_level.TextureBitmap, dlg.FileName);
    }

    private void TextureImage_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        SaveTgaMenuItem.IsEnabled = _level?.TextureBitmap != null &&
                                    _level.TextureBitmap.PixelWidth > 0 &&
                                    _level.TextureBitmap.PixelHeight > 0;
    }

    private void TextureScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TextureScrollViewer.ScrollToVerticalOffset(TextureScrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        if (files.Length == 0) return;

        var path = files[0];
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".tr4")
            LoadTr4File(path);
        else if (ext == ".prj")
        {
            if (_level == null)
                MessageBox.Show("TR4 file not loaded.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                LoadPrjFile(path);
        }
        else
            MessageBox.Show("Unknown filetype.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void UpdateUiState()
    {
        var hasLevel = _level != null;
        SaveAsMenuItem.IsEnabled = hasLevel;
        LoadPrjButton.IsEnabled = hasLevel;
        UnloadPrjButton.IsEnabled = _aktrekker != null;
        CopyDoorsCheckBox.IsEnabled = _aktrekker != null;
        CopyTexturesCheckBox.IsEnabled = _aktrekker != null;
        CopyLightsCheckBox.IsEnabled = _aktrekker != null;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _level?.Dispose();
        _aktrekker?.Dispose();
        base.OnClosed(e);
    }
}
