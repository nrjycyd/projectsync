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
