using Microsoft.UI.Xaml;

namespace InventorMeta.App;

public partial class App
{
    public static Window MainWindowInstance { get; private set; } = null!;

    public App() { InitializeComponent(); }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}