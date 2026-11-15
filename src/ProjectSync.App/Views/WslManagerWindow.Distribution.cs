using System;
using System.Windows;
using System.Windows.Media;

namespace ProjectSync.App.Views;

public partial class WslManagerWindow
{
    private static string DistributionLabel(string? flavor, string? version)
    {
        string name = flavor?.ToLowerInvariant() switch
        {
            "ubuntu" => "Ubuntu",
            "debian" => "Debian",
            "kali" or "kali-linux" => "Kali Linux",
            null or "" => "未知发行版",
            _ => flavor
        };
        return string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";
    }

    private static ImageSource DistributionLogo(string? flavor, bool running)
    {
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 24, 24));
            switch (flavor?.ToLowerInvariant())
            {
                case "ubuntu":
                    context.DrawEllipse(running ? Brushes.OrangeRed : Brushes.Gray, null, new Point(12, 12), 11, 11);
                    context.DrawEllipse(null, new Pen(Brushes.White, 1.35), new Point(12, 12), 6, 6);
                    foreach (var point in new[] { new Point(6, 12), new Point(15, 6.8), new Point(15, 17.2) })
                        context.DrawEllipse(Brushes.White, null, point, 1.9, 1.9);
                    break;
                case "debian":
                    context.DrawGeometry(null, new Pen(running ? Brushes.Crimson : Brushes.Gray, 1.65),
                        Geometry.Parse("M11,22 C3,19 1,11 5,5 C9,-1 20,1 21,9 C22,15 15,19 11,15 C8,12 10,7 14,8 C17,8 18,12 15,13"));
                    break;
                default:
                    var pen = new Pen(running ? Brushes.SeaGreen : Brushes.Gray, 1.5);
                    context.DrawRoundedRectangle(null, pen, new Rect(2, 4, 20, 16), 2, 2);
                    context.DrawGeometry(null, pen, Geometry.Parse("M6,8 L10,12 6,16 M12,16 H18"));
                    break;
            }
        }
        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }
}
