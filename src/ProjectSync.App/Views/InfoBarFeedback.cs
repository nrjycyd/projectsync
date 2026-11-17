using System.Windows.Automation;
using System.Windows.Automation.Peers;
using Wpf.Ui.Controls;

namespace ProjectSync.App.Views;

internal static class InfoBarFeedback
{
    internal static void ShowFeedback(this InfoBar bar, string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    {
        if (bar.IsOpen && bar.Message == message && bar.Severity == severity)
            return;

        bar.Severity = severity;
        bar.Message = message;
        AutomationProperties.SetName(bar, message);
        AutomationProperties.SetLiveSetting(bar, AutomationLiveSetting.Polite);
        bar.IsOpen = true;
        UIElementAutomationPeer.FromElement(bar)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
