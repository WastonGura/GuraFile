using Microsoft.UI.Xaml;

namespace GuraFile;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Title = "GuraFile";
        _window.AppWindow.Title = "GuraFile";
        _window.Activate();
    }
}
