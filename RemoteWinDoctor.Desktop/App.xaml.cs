using System.Globalization;
using System.Windows;

namespace RemoteWinDoctor.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // On fa-IR Windows, .NET formats 15.85 as "15/85". The UI shows technical values
        // (GB, MHz, %), so format numbers invariantly regardless of the user's locale.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        base.OnStartup(e);
    }
}
