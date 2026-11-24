using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace SiteLift.App.Views;

internal static class FluentDialog
{
    internal static FluentWindow Create(Window? owner, string title, FrameworkElement content, double width, double? height = null)
    {
        var panel = new DockPanel();
        var titleBar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false };
        titleBar.SetResourceReference(TitleBar.ButtonsForegroundProperty, "TextFillColorPrimaryBrush");
        DockPanel.SetDock(titleBar, Dock.Top);
        panel.Children.Add(titleBar);
        var bodyHost = new Grid { Background = Brushes.Transparent, Children = { content } };
        panel.Children.Add(bodyHost);
        var window = new FluentWindow
        {
            Title = title, Width = width, MinHeight = 0, FontSize = 14,
            SizeToContent = height.HasValue ? SizeToContent.Manual : SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Content = panel,
            ExtendsContentIntoTitleBar = true, WindowCornerPreference = WindowCornerPreference.Round,
            WindowBackdropType = WindowBackdropType.Mica,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            UseLayoutRounding = true
        };
        if (owner is not null)
        {
            window.Owner = owner;
            window.Icon = owner.Icon;
        }
        if (height.HasValue) window.Height = height.Value;
        window.SetResourceReference(Window.FontFamilyProperty, "WorkspaceTypeface");
        window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
        TextOptions.SetTextRenderingMode(window, TextRenderingMode.ClearType);
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Ideal);
        TextOptions.SetTextHintingMode(window, TextHintingMode.Fixed);
        return window;
    }

    internal static void LimitToWorkArea(Window dialog, Window owner)
    {
        Size available = SystemParameters.WorkArea.Size;
        IntPtr monitor = MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2); // MONITOR_DEFAULTTONEAREST
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
        {
            var dpi = VisualTreeHelper.GetDpi(owner);
            available = new Size((info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
        }

        const double edgeMargin = 24;
        dialog.MaxWidth = Math.Max(1, available.Width - edgeMargin);
        dialog.MaxHeight = Math.Max(1, available.Height - edgeMargin);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    internal static MessageBoxResult Ask(Window? owner, string title, string message,
        string accept, string? decline = null, bool canCancel = false, bool destructive = false)
    {
        var panel = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var footer = new Border
        {
            Margin = new Thickness(0, 20, 0, 0),
            Child = actions
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer
        {
            MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new System.Windows.Controls.TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap,
                LineHeight = 20, LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            }
        });

        var dialog = Create(owner, title, panel, 460);
        var result = MessageBoxResult.Cancel;
        Button? safeButton = null;
        if (canCancel) safeButton = AddAction("取消", MessageBoxResult.Cancel, ControlAppearance.Secondary, true);
        if (decline is not null) safeButton = AddAction(decline, MessageBoxResult.No, ControlAppearance.Secondary, !canCancel);
        var confirm = AddAction(accept, MessageBoxResult.Yes,
            destructive ? ControlAppearance.Danger : ControlAppearance.Primary, decline is null);
        confirm.IsDefault = decline is null || canCancel;
        dialog.Loaded += (_, _) => (canCancel ? confirm : safeButton ?? confirm).Focus();
        dialog.ShowDialog();
        return result;

        Button AddAction(string label, MessageBoxResult choice, ControlAppearance appearance, bool isCancel)
        {
            var button = new Button
            {
                Content = label, Appearance = appearance, IsCancel = isCancel,
                MinWidth = 84,
                Margin = new Thickness(8, 0, 0, 0)
            };
            button.SetResourceReference(FrameworkElement.StyleProperty, "WorkspaceTextButton");
            button.Click += (_, _) => { result = choice; dialog.Close(); };
            actions.Children.Add(button);
            return button;
        }
    }
}
