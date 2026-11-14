using System.Configuration;
using System.Data;
using System.Windows;
using PRJ2_Extractor.Services;

namespace PRJ2_Extractor
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // The theme (saved choice, or Windows' own) has to be in place before MainWindow (StartupUri) is created.
            ThemeService.Initialize();
            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            ThemeService.Shutdown();
            base.OnExit(e);
        }
    }

}
