using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows;

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

    protected override void OnStartup(StartupEventArgs e)
    {
        using var identity = WindowsIdentity.GetCurrent();
        instance = new Mutex(false, $@"Local\{DataDirectoryName}-{identity.User}", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("ProjectSync 已在运行。请切换到现有窗口。", "ProjectSync");
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            AppendDiagnostic(args.Exception.ToString());
            args.Handled = true;
            MessageBox.Show("ProjectSync 遇到意外错误并将退出。诊断日志位于项目配置目录的 logs 文件夹。",
                "ProjectSync", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        };
        var accent = (System.Windows.Media.SolidColorBrush)FindResource("Accent");
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(accent.Color);
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        instance?.Dispose();
        base.OnExit(e);
    }
}
