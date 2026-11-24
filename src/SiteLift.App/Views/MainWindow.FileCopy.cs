using System.Linq;
using System.Text;
using System.Windows;
using SiteLift.Core;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private void OpenFileCopy(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        var dialog = new FileCopyWindow(async () => Lines(await QueryWslAsync(Encoding.Unicode, "-l", "-q"))
            .Select(name => name.Trim('\0'))
            .Where(name => name.Length > 0 && !WslOutputParser.IsSystemUtilityDistribution(name))
            .ToArray())
        {
            Owner = this, Icon = Icon
        };
        dialog.ShowDialog();
    }
}
