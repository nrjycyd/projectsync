using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProjectSync.App;

public partial class App : Application
{
    internal const string DataDirectoryName = "ProjectSync-WpfUiTrial";
    private Mutex? instance;
    internal static string DiagnosticsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataDirectoryName, "logs");

    internal static void AppendDiagnostic(string message)
    {
        try
        {
            Directory.CreateDirectory(DiagnosticsDirectory);
            var now = DateTimeOffset.Now;
            File.AppendAllText(Path.Combine(DiagnosticsDirectory, $"projectsync-{now:yyyy-MM-dd}.log"),
                $"{now:HH:mm:ss.fff zzz} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static readonly DependencyProperty UseNativeComboBoxMotionProperty =
        DependencyProperty.RegisterAttached("UseNativeComboBoxMotion", typeof(bool), typeof(App),
            new PropertyMetadata(false, OnUseNativeComboBoxMotionChanged));

    public static bool GetUseNativeComboBoxMotion(ComboBox control) =>
        (bool)control.GetValue(UseNativeComboBoxMotionProperty);

    public static void SetUseNativeComboBoxMotion(ComboBox control, bool value) =>
        control.SetValue(UseNativeComboBoxMotionProperty, value);

    private static void OnUseNativeComboBoxMotionChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ComboBox comboBox) return;

        if ((bool)e.NewValue)
            comboBox.DropDownOpened += ComboBoxPopupOpened;
        else
            comboBox.DropDownOpened -= ComboBoxPopupOpened;
    }

    private static void ComboBoxPopupOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox)
            UseNativePopupMotion(comboBox, "DropDownBorder");
    }

    private void ContextMenuPopupOpened(object sender, RoutedEventArgs e) =>
        UseNativePopupMotion((ContextMenu)sender, "Border");

    private static void UseNativePopupMotion(Control control, string templatePart)
    {
        // WPF UI begins a second slide after the open event. Reset its transform before rendering.
        _ = control.Dispatcher.InvokeAsync(() =>
        {
            bool isOpen = control is ComboBox { IsDropDownOpen: true } or ContextMenu { IsOpen: true };
            if (isOpen && control.Template?.FindName(templatePart, control) is Border border &&
                border.RenderTransform is TranslateTransform)
            {
                border.SetCurrentValue(UIElement.RenderTransformProperty, new TranslateTransform());
            }
        }, DispatcherPriority.Send);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        using var identity = WindowsIdentity.GetCurrent();
        instance = new Mutex(false, $@"Local\{DataDirectoryName}-{identity.User}", out bool firstInstance);
        if (!firstInstance)
        {
            Views.FluentDialog.Ask(null, "ProjectSync", "ProjectSync 已在运行。请切换到现有窗口。", "关闭");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += ShowFatalError;
        base.OnStartup(e);
    }

    private void ShowFatalError(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs args)
    {
        AppendDiagnostic(args.Exception.ToString());
        args.Handled = true;
        DispatcherUnhandledException -= ShowFatalError;
        try
        {
            Views.FluentDialog.Ask(null, "ProjectSync",
                "ProjectSync 遇到意外错误并将退出。诊断日志位于项目配置目录的 logs 文件夹。", "关闭");
        }
        finally { Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
