using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using SiteLift.Core;

namespace SiteLift.App.Views;

internal sealed record FrameworkIconVisual(ImageSource? Image, bool IsGeneric, string DisplayName);

internal static class SiteFrameworkVisuals
{
    private static readonly Dictionary<(SiteFramework Framework, bool Dark), FrameworkIconVisual> Cache = new();

    public static FrameworkIconVisual Resolve(SiteFramework framework, bool darkTheme)
    {
        string displayName = SiteFrameworkDetector.GetDisplayName(framework);
        if (framework == SiteFramework.Generic)
            return new FrameworkIconVisual(null, IsGeneric: true, displayName);

        var key = (framework, darkTheme);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        var (image, _) = BuildArtwork(framework, darkTheme);
        var visual = new FrameworkIconVisual(image, IsGeneric: false, displayName);
        Cache[key] = visual;
        return visual;
    }

    private static (DrawingImage Image, string TintHex) BuildArtwork(SiteFramework framework, bool dark)
    {
        return framework switch
        {
            // VuePress: classic layered green V logomark (optically centered in 24x24 viewport)
            SiteFramework.VuePress => (MakeDrawing(
                ("#3EAF7C", "M2,4 L7.6,4 L12,11.6 L16.4,4 L22,4 L12,21.2 Z"),
                (dark ? "#A7F3D0" : "#35495E", "M6.2,4 L9.6,4 L12,8.2 L14.4,4 L17.8,4 L12,14.2 Z")), "#3EAF7C"),

            // VitePress: indigo-violet V with golden lightning bolt
            SiteFramework.VitePress => (MakeDrawing(
                (dark ? "#818CF8" : "#5C73E7", "M2.5,4 L8,4 L12,14.5 L16,4 L21.5,4 L12,21 Z"),
                ("#FACC15", "M13.5,2.5 L8.8,11.2 L12.2,11.2 L10.7,17.2 L16.4,8.6 L12.9,8.6 Z")), "#646CFF"),

            // Hexo: iconic blue hexagon with crisp H cutout
            SiteFramework.Hexo => (MakeDrawing(
                (dark ? "#38BDF8" : "#0E83CD", "M12,2 L20.5,6.8 L20.5,17.2 L12,22 L3.5,17.2 L3.5,6.8 Z"),
                (dark ? "#1E293B" : "#FFFFFF", "M8.3,7.3 L10.2,7.3 L10.2,11.1 L13.8,11.1 L13.8,7.3 L15.7,7.3 L15.7,16.7 L13.8,16.7 L13.8,12.8 L10.2,12.8 L10.2,16.7 L8.3,16.7 Z")), "#0E83CD"),

            // Hugo: magenta-pink hexagon with H monogram
            SiteFramework.Hugo => (MakeDrawing(
                (dark ? "#FF66A1" : "#FF4088", "M12,2 L20.5,6.8 L20.5,17.2 L12,22 L3.5,17.2 L3.5,6.8 Z"),
                (dark ? "#2B1622" : "#FFFFFF", "M8.3,7.3 L10.2,7.3 L10.2,11.1 L13.8,11.1 L13.8,7.3 L15.7,7.3 L15.7,16.7 L13.8,16.7 L13.8,12.8 L10.2,12.8 L10.2,16.7 L8.3,16.7 Z")), "#FF4088"),

            // Astro: bold A frame with orange flame tail
            SiteFramework.Astro => (MakeDrawing(
                (dark ? "#E2E8F0" : "#1F2937", "M9.3,3 L14.7,3 L19.6,17.2 L16.4,17.2 L14.8,12.2 L9.2,12.2 L7.6,17.2 L4.4,17.2 Z M10.1,9.7 L13.9,9.7 L12,4.6 Z"),
                ("#FF5D01", "M9.6,16.5 C10.4,18.2 11.4,19.1 12.1,21 C14.5,19.9 15.2,18.1 14.4,16.2 C13.5,17.1 11.2,17.4 9.6,16.5 Z")), "#FF5D01"),

            // Docusaurus: fresh green dual-leaf / crest emblem
            SiteFramework.Docusaurus => (MakeDrawing(
                (dark ? "#3ECC5F" : "#2E8555", "M12,2.5 C7,2.5 3.5,6.3 3.5,11.2 C3.5,16.2 7.2,20.5 12,21.5 C16.8,20.5 20.5,16.2 20.5,11.2 C20.5,6.3 17,2.5 12,2.5 Z"),
                (dark ? "#132A1B" : "#FFFFFF", "M8.2,15.8 C8.2,10.8 11.2,7.6 16.2,7.2 C15.8,12.2 12.8,15.4 8.2,15.8 Z")), "#2E8555"),

            // MkDocs: Material cobalt blue layered pages
            SiteFramework.MkDocs => (MakeDrawing(
                (dark ? "#60A5FA" : "#2962FF", "M4,4.5 L15.5,4.5 L20,9 L20,19.5 L4,19.5 Z"),
                (dark ? "#1E3A5F" : "#FFFFFF", "M15,4.5 L15,9.5 L20,9.5 Z M7.5,11.8 L16.5,11.8 L16.5,13.4 L7.5,13.4 Z M7.5,15 L14,15 L14,16.6 L7.5,16.6 Z")), "#2962FF"),

            // Quartz: faceted teal crystal diamond
            SiteFramework.Quartz => (MakeDrawing(
                (dark ? "#56B4D3" : "#284B63", "M12,2 L20,12 L12,22 L4,12 Z"),
                (dark ? "#BAE6FD" : "#7DD3FC", "M12,2 L16,12 L12,22 L12,2 Z")), "#3898B8"),

            // Rspress: amber book-bolt emblem
            SiteFramework.Rspress => (MakeDrawing(
                (dark ? "#FBBF24" : "#EA580C", "M4,4.5 C7.5,4.5 10.5,5.4 12,7.2 C13.5,5.4 16.5,4.5 20,4.5 L20,18 C16.5,18 13.5,18.9 12,20.5 C10.5,18.9 7.5,18 4,18 Z"),
                (dark ? "#2E1F08" : "#FFFBEB", "M12,8.5 L12,18.6 L10.7,18.6 L10.7,8.5 Z")), "#F59E0B"),

            // Nuxt: iconic emerald twin peaks
            SiteFramework.Nuxt => (MakeDrawing(
                ("#00DC82", "M2,18.5 L9.8,5 L14.5,13.2 L12.1,13.2 L9.8,9.2 L4.4,18.5 Z"),
                (dark ? "#6EE7B7" : "#00A86B", "M10.2,18.5 L15.4,9.5 L21.5,18.5 L18.8,18.5 L15.4,13.2 L12.6,18.5 Z")), "#00DC82"),

            // Next.js: clean monochrome circle with N monogram
            SiteFramework.NextJs => (MakeDrawing(
                (dark ? "#E5E7EB" : "#171717", "M12,2.5 A9.5,9.5 0 1,1 11.99,2.5 Z"),
                (dark ? "#171717" : "#FFFFFF", "M8.3,16.2 L8.3,7.8 L10.1,7.8 L14.3,14 L14.3,7.8 L15.9,7.8 L15.9,16.2 L14.1,16.2 L9.9,10 L9.9,16.2 Z")), dark ? "#9CA3AF" : "#404040"),

            // Jekyll: ruby flask emblem
            SiteFramework.Jekyll => (MakeDrawing(
                (dark ? "#F87171" : "#CC0000", "M9.5,3 L14.5,3 L14.5,4.6 L13.4,4.6 L13.4,9.2 L18.2,17.8 C18.8,19 17.9,20.5 16.5,20.5 L7.5,20.5 C6.1,20.5 5.2,19 5.8,17.8 L10.6,9.2 L10.6,4.6 L9.5,4.6 Z"),
                (dark ? "#FEE2E2" : "#FFFFFF", "M8.5,14.5 L15.5,14.5 L16.7,17.8 C16.9,18.3 16.6,18.8 16,18.8 L8,18.8 C7.4,18.8 7.1,18.3 7.3,17.8 Z")), "#CC0000"),

            // mdBook: warm Rust-amber open book
            SiteFramework.MdBook => (MakeDrawing(
                (dark ? "#F59E0B" : "#C2410C", "M3.5,5.5 C7.2,5.5 10.2,6.4 12,8.2 C13.8,6.4 16.8,5.5 20.5,5.5 L20.5,18.5 C16.8,18.5 13.8,19.4 12,21 C10.2,19.4 7.2,18.5 3.5,18.5 Z"),
                (dark ? "#2B1E0D" : "#FFF7ED", "M5.4,7.6 C8,7.8 10,8.6 11.2,9.8 L11.2,18.2 C9.8,17.2 7.8,16.6 5.4,16.5 Z M18.6,7.6 L18.6,16.5 C16.2,16.6 14.2,17.2 12.8,18.2 L12.8,9.8 C14,8.6 16,7.8 18.6,7.6 Z")), "#D97706"),

            // Docsify: emerald page & leaf
            SiteFramework.Docsify => (MakeDrawing(
                (dark ? "#4ADE80" : "#42B983", "M5,3.5 L15.5,3.5 L19,7 L19,20.5 L5,20.5 Z"),
                (dark ? "#122B1E" : "#FFFFFF", "M8.5,10 L15.5,10 L15.5,11.6 L8.5,11.6 Z M8.5,13.5 L15.5,13.5 L15.5,15.1 L8.5,15.1 Z M8.5,17 L13,17 L13,18.6 L8.5,18.6 Z")), "#42B983"),

            _ => (MakeDrawing(("#0078D4", "M3,5 L21,5 L21,19 L3,19 Z")), "#0078D4")
        };
    }

    private static DrawingImage MakeDrawing(params (string Hex, string PathData)[] layers)
    {
        var group = new DrawingGroup();
        // Transparent 24x24 viewport anchor so every icon scales uniformly to the same 24x24 coordinate system.
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 24, 24))));
        foreach (var (hex, pathData) in layers)
        {
            var brush = new SolidColorBrush(ColorFromHex(hex));
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

    private static Color ColorFromHex(string hex) =>
        (Color)ColorConverter.ConvertFromString(hex);
}
