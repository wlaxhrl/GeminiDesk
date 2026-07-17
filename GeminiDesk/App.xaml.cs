using System.Windows;
using Velopack;

namespace GeminiDesk;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        FileAssociationService.RegisterKeyPresetIcon();
        app.Run();
    }
}
