using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SiteLift.Core;

namespace SiteLift.App.Views;

internal sealed record WslProjectReference(string Name, SiteFramework Framework);

public partial class WslManagerWindow
{
    private sealed record DevelopmentTool(string Caption, string Tool, ImageSource? Icon, string? OverflowLabel = null)
    {
        public bool IsOverflow => OverflowLabel is not null;
    }

    private static ImageSource ResolveToolIcon(string tool, bool darkTheme)
    {
        if (tool is "git" or "python3" or "node" or "pnpm")
        {
            string file = tool == "pnpm" && darkTheme ? "pnpm-dark.png" : $"{tool}.png";
            var uri = new Uri($"pack://application:,,,/SiteLift.App;component/Assets/DevelopmentTools/{file}", UriKind.Absolute);
            return new BitmapImage(uri);
        }

        return tool switch
        {
            "hugo" => SiteFrameworkVisuals.Resolve(SiteFramework.Hugo, darkTheme).Image!,
            "mdbook" => SiteFrameworkVisuals.Resolve(SiteFramework.MdBook, darkTheme).Image!,
            "ruby" => SiteFrameworkVisuals.Resolve(SiteFramework.Jekyll, darkTheme).Image!,
            "go" => MakeToolDrawing(
                (darkTheme ? "#38BDF8" : "#00ADD8", "M12,3 C7,3 3,7 3,12 C3,17 7,21 12,21 C17,21 21,17 21,12 C21,7 17,3 12,3 Z"),
                (darkTheme ? "#0F172A" : "#FFFFFF", "M11.6,8.2 C9.3,8.2 7.6,9.8 7.6,12 C7.6,14.2 9.3,15.8 11.7,15.8 C13.8,15.8 15.4,14.5 15.6,12.4 L15.6,11.3 L11.5,11.3 L11.5,12.8 L13.8,12.8 C13.5,13.7 12.7,14.2 11.7,14.2 C10.3,14.2 9.4,13.3 9.4,12 C9.4,10.7 10.3,9.8 11.7,9.8 C12.5,9.8 13.1,10.1 13.6,10.6 L14.9,9.4 C14.1,8.6 13,8.2 11.6,8.2 Z")),
            "rustc" => MakeToolDrawing(
                (darkTheme ? "#FDBA74" : "#B45309", "M12,2.5 L14.1,4.7 L17.2,4.3 L18,7.3 L20.8,8.7 L19.8,11.6 L21.4,14.3 L19,16.2 L18.9,19.3 L15.9,19.7 L14.1,22.2 L11.4,20.7 L8.5,21.9 L7.1,19.1 L4.1,18.3 L4.4,15.2 L2.3,12.9 L4.1,10.4 L3.5,7.4 L6.4,6.4 L7.7,3.6 L10.7,4.5 Z"),
                (darkTheme ? "#1F1610" : "#FFFFFF", "M8.8,8.2 L12.9,8.2 C14.6,8.2 15.7,9.2 15.7,10.7 C15.7,11.8 15,12.6 14,13 L15.9,15.8 L13.8,15.8 L12.1,13.2 L10.6,13.2 L10.6,15.8 L8.8,15.8 Z M10.6,9.8 L10.6,11.7 L12.7,11.7 C13.4,11.7 13.9,11.3 13.9,10.7 C13.9,10.1 13.4,9.8 12.7,9.8 Z")),
            _ => SiteFrameworkVisuals.Resolve(SiteFramework.VuePress, darkTheme).Image!
        };
    }

    private static DrawingImage MakeToolDrawing(params (string Hex, string PathData)[] layers)
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        foreach (var (hex, pathData) in layers)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            var geo = Geometry.Parse(pathData);
            geo.Freeze();
            group.Children.Add(new GeometryDrawing(brush, null, geo));
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static async Task<(WslToolProbeResult? Probe, DevelopmentTool[] Tools, string Status)> ReadInstalledToolsAsync(
        string instance, string? user = null, CancellationToken cancellation = default)
    {
        try
        {
            var args = new List<string> { "-d", instance };
            if (!string.IsNullOrWhiteSpace(user))
            {
                args.Add("-u");
                args.Add(user.Trim());
            }
            args.AddRange(new[] { "--exec", "bash", "-c", WslDevelopmentEnvironment.ProbeScript });

            var result = await InvokeAsync(args.ToArray(), encoding: Encoding.UTF8,
                timeout: TimeSpan.FromSeconds(10), cancellation: cancellation);
            if (result.Code != 0)
                return (null, Array.Empty<DevelopmentTool>(), "检测失败");

            var probe = WslDevelopmentEnvironment.ParseProbeOutput(result.Output);
            var tools = BuildTableTools(probe);
            return (probe, tools, tools.Length == 0 ? "未检测到工具" : "");
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or IOException or Win32Exception or InvalidOperationException)
        {
            return (null, Array.Empty<DevelopmentTool>(), "检测失败");
        }
    }

    private static DevelopmentTool[] BuildTableTools(WslToolProbeResult probe)
    {
        bool darkTheme = Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Dark;
        var all = new List<DevelopmentTool>();

        if (probe.GitVersion is not null)
            all.Add(new DevelopmentTool($"Git {probe.GitVersion}", "git", ResolveToolIcon("git", darkTheme)));
        if (probe.NodeVersion is not null)
            all.Add(new DevelopmentTool($"Node.js {probe.NodeVersion}", "node", ResolveToolIcon("node", darkTheme)));
        if (probe.PnpmVersion is not null)
            all.Add(new DevelopmentTool($"pnpm {probe.PnpmVersion}", "pnpm", ResolveToolIcon("pnpm", darkTheme)));
        if (probe.PythonVersion is not null)
        {
            string pyExtra = probe.PythonReadyForMkDocs ? "（含 pip/venv）" : "（缺 pip/venv）";
            all.Add(new DevelopmentTool($"Python {probe.PythonVersion}{pyExtra}", "python3", ResolveToolIcon("python3", darkTheme)));
        }
        if (probe.HugoVersion is not null)
        {
            string hugoKind = probe.HugoIsExtended ? " Extended" : "";
            all.Add(new DevelopmentTool($"Hugo{hugoKind} {probe.HugoVersion}", "hugo", ResolveToolIcon("hugo", darkTheme)));
        }
        if (probe.GoVersion is not null)
            all.Add(new DevelopmentTool($"Go {probe.GoVersion}", "go", ResolveToolIcon("go", darkTheme)));
        if (probe.RubyVersion is not null)
        {
            string rubyExtra = probe.HasBundler ? "（含 Bundler）" : "";
            all.Add(new DevelopmentTool($"Ruby {probe.RubyVersion}{rubyExtra}", "ruby", ResolveToolIcon("ruby", darkTheme)));
        }
        if (probe.MdBookVersion is not null)
            all.Add(new DevelopmentTool($"mdBook {probe.MdBookVersion}", "mdbook", ResolveToolIcon("mdbook", darkTheme)));
        if (probe.RustVersion is not null)
            all.Add(new DevelopmentTool($"Rust {probe.RustVersion}", "rustc", ResolveToolIcon("rustc", darkTheme)));

        if (all.Count <= 4)
            return all.ToArray();

        string fullTooltip = "已检测到的全部工具：\n" + string.Join("\n", all.Select(item => "• " + item.Caption));
        int extraCount = all.Count - 3;
        return new[]
        {
            all[0],
            all[1],
            all[2],
            new DevelopmentTool(fullTooltip, "overflow", null, $"+{extraCount}")
        };
    }
}
