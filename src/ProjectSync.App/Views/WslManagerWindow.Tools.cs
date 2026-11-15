using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ProjectSync.App.Views;

public partial class WslManagerWindow
{
    private sealed record DevelopmentTool(string Caption, ImageSource Icon);

    private static async Task<(DevelopmentTool[] Tools, string Status)> ReadInstalledToolsAsync(string instance)
    {
        // Avoid activating Corepack downloads or package-manager version switching during a read-only check.
        const string script = """
            cd /tmp || exit 1; export COREPACK_ENABLE_NETWORK=0 COREPACK_ENABLE_AUTO_PIN=0 npm_config_manage_package_manager_versions=false; nvm_dir="$HOME/.nvm"; if [[ -n $XDG_CONFIG_HOME && -s $XDG_CONFIG_HOME/nvm/nvm.sh ]]; then nvm_dir="$XDG_CONFIG_HOME/nvm"; fi; if [[ -s $nvm_dir/nvm.sh ]]; then source "$nvm_dir/nvm.sh" --no-use && nvm use --silent default >/dev/null 2>&1; fi; for tool in git python3 node pnpm; do if version=$(command "$tool" --version 2>/dev/null); then printf '%s\t%s\n' "$tool" "$version"; fi; done
            """;
        try
        {
            var result = await InvokeAsync(new[] { "-d", instance, "--exec", "bash", "-c", script },
                encoding: Encoding.UTF8).WaitAsync(TimeSpan.FromSeconds(8));
            if (result.Code != 0) return (Array.Empty<DevelopmentTool>(), "检测失败");
            var tools = new List<DevelopmentTool>();
            foreach (string line in result.Output.Split('\n'))
            {
                var match = Regex.Match(line.TrimEnd(), @"^(git|python3|node|pnpm)\t(?:git version |Python )?v?([0-9][A-Za-z0-9.+-]*)$");
                if (!match.Success) continue;
                string tool = match.Groups[1].Value;
                string name = tool switch { "git" => "Git", "python3" => "Python", "node" => "Node.js", _ => "pnpm" };
                tools.Add(new DevelopmentTool($"{name} {match.Groups[2].Value}", ToolIcon(tool)));
            }
            return (tools.ToArray(), tools.Count == 0 ? "未检测到工具" : "");
        }
        catch (Exception error) when (error is TimeoutException or IOException or Win32Exception or InvalidOperationException)
        {
            return (Array.Empty<DevelopmentTool>(), "检测失败");
        }
    }

    private static ImageSource ToolIcon(string tool)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            void Shape(string geometry, Brush? fill, Pen? pen = null) =>
                context.DrawGeometry(fill, pen, Geometry.Parse(geometry));

            switch (tool)
            {
                case "git":
                    Shape("M12,1 L23,12 12,23 1,12 Z", Brushes.OrangeRed);
                    Shape("M8,7 L15,14 M11,10 V17", null, new Pen(Brushes.White, 1.6));
                    foreach (var point in new[] { new Point(8, 7), new Point(15, 14), new Point(11, 17) })
                        context.DrawEllipse(Brushes.White, null, point, 1.5, 1.5);
                    break;
                case "python3":
                    Shape("M10,2 H16 Q20,2 20,6 V11 H9 V14 H4 Q1,14 1,11 V8 Q1,5 4,5 H10 Z", Brushes.SteelBlue);
                    Shape("M14,22 H8 Q4,22 4,18 V13 H15 V10 H20 Q23,10 23,13 V16 Q23,19 20,19 H14 Z", Brushes.Gold);
                    context.DrawEllipse(Brushes.White, null, new Point(14, 4.5), 1.2, 1.2);
                    context.DrawEllipse(Brushes.White, null, new Point(10, 19.5), 1.2, 1.2);
                    break;
                case "node":
                    var green = new Pen(Brushes.ForestGreen, 1.8);
                    Shape("M12,1 L22,6 V18 L12,23 2,18 V6 Z M10,8 V15 Q9,18 6,16 M18,9 Q16,7 14,9 Q12,11 16,12 Q20,13 17,16 Q15,18 12,16", null, green);
                    break;
                default:
                    for (int row = 0; row < 3; row++)
                        for (int column = 0; column < 3; column++)
                            context.DrawRectangle(row == 0 || column == 2 ? Brushes.Goldenrod : Brushes.DimGray,
                                null, new Rect(1 + column * 8, 1 + row * 8, 6, 6));
                    break;
            }
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
