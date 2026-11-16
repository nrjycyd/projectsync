using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

    private static BitmapSource? ReadDistributionIcon(string? location, bool isRunning)
    {
        if (location is null) return null;

        try
        {
            using var stream = File.OpenRead(Path.Combine(location, "shortcut.ico"));
            var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.MinBy(candidate => Math.Abs(candidate.PixelWidth - 32));
            if (frame is null) return null;

            BitmapSource logo = CopyIconPixels(frame, grayscale: !isRunning);
            logo.Freeze();
            return logo;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            FormatException or NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BitmapSource CopyIconPixels(BitmapSource source, bool grayscale)
    {
        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        if (grayscale)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int luminance = (77 * pixels[i + 2] + 150 * pixels[i + 1] + 29 * pixels[i]) / 256;
                // Keep stopped icons legible on both theme backgrounds without changing alpha.
                byte gray = (byte)(112 + luminance * 96 / 255);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = gray;
            }
        }

        // An ICO frame can still refer to its thread-bound decoder even after freezing.
        return BitmapSource.Create(bitmap.PixelWidth, bitmap.PixelHeight, bitmap.DpiX, bitmap.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
    }
}
