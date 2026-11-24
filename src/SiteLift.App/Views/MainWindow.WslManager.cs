using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using SiteLift.Core;

namespace SiteLift.App.Views;

public partial class MainWindow
{
    private void OpenWslManager(object sender, RoutedEventArgs e)
    {
        if (running || detectingWsl) return;
        new WslManagerWindow(ProjectsUsingWsl) { Owner = this, Icon = Icon }.ShowDialog();
    }

    private IEnumerable<WslProjectReference> ProjectsUsingWsl(string instance)
    {
        foreach (var project in savedProjects)
        {
            JsonNode? config;
            try { config = JsonNode.Parse(project.Configuration); }
            catch (System.Text.Json.JsonException) { continue; }
            string? name = (config?["distro"] as JsonValue)?.TryGetValue<string>(out var configured) == true ? configured : null;
            if ((config?["wslRepo"] as JsonValue)?.TryGetValue<string>(out var path) == true &&
                TryParseWslNetworkPath(UnquotePath(path), out _, out var fromPath)) name = fromPath;
            if (string.Equals(instance, name, StringComparison.OrdinalIgnoreCase))
            {
                var framework = SiteFrameworkDetector.Detect(project.Configuration);
                yield return new WslProjectReference(project.Name, framework);
            }
        }
    }
}
