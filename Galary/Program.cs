using Velopack;

namespace Galary;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run(); // handles install/update/uninstall hooks; must run first
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
