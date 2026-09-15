using System.Windows;
using Velopack;

namespace WeArchive.App;

/// <summary>
/// Explicit entry point.
/// <para>
/// Velopack has to see its own command-line hooks before WPF initialises, and it must run from
/// the start of <c>Main</c> for the installer and updater to work correctly, so the entry
/// point is declared here instead of relying on the one WPF generates.
/// </para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // No-op when the application is not running from a Velopack installation.
        VelopackApp.Build().Run();

        var application = new App();
        application.InitializeComponent();
        application.Run();
    }
}
