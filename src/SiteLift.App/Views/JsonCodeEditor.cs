using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace SiteLift.App.Views;

public sealed class JsonCodeEditor : TextBox
{
    private static readonly CultureInfo ZhCulture = CultureInfo.GetCultureInfo("zh-CN");

    private sealed record Palette(
        Brush Key,
        Brush String,
        Brush Escape,
        Brush BoolNull,
        Brush Number,
        Brush Punctuation,
        Brush[] Brackets,
        Brush LineNumber,
        Brush ActiveLineNumber,
        Brush ActiveLineBackground,
        Brush IndentGuide,
        Brush ActiveIndentGuide);

    private static readonly Palette LightPalette = CreatePalette(
        key: "#0451A5",
        str: "#A31515",
        escape: "#EE0000",
        boolNull: "#0000FF",
        number: "#098658",
        punctuation: "#3B3B3B",
        brackets: new[] { "#0431FA", "#319331", "#7B3814" },
        lineNumber: "#6E7681",
        activeLineNumber: "#171184",
        activeLineBackground: "#07000000",
        indentGuide: "#16000000",
        activeIndentGuide: "#38000000");

    private static readonly Palette DarkPalette = CreatePalette(
        key: "#9CDCFE",
        str: "#CE9178",
        escape: "#D7BA7D",
        boolNull: "#569CD6",
        number: "#B5CEA8",
        punctuation: "#CCCCCC",
        brackets: new[] { "#FFD700", "#DA70D6", "#179FFF" },
        lineNumber: "#6E7681",
        activeLineNumber: "#CCCCCC",
        activeLineBackground: "#0BFFFFFF",
        indentGuide: "#18FFFFFF",
        activeIndentGuide: "#45FFFFFF");

    private enum TokenKind { Punctuation, Key, String, Escape, BoolNull, Number, Bracket }
    private readonly record struct TokenSpan(int Start, int Length, TokenKind Kind, int BracketLevel = 0);
    private readonly record struct Scope(int StartLine, int EndLine, int Level);
    private sealed record LineInfo(int CharStart, string Text, int LeadingSpaces, List<TokenSpan> Spans);

    private ScrollViewer? contentHost;
    private ScrollContentPresenter? contentPresenter;
    private JsonGutterLayer? gutterLayer;
    private JsonSyntaxLayer? syntaxLayer;

    private string? analyzedText;
    private List<LineInfo> analyzedLines = new();
    private List<Scope> analyzedScopes = new();
    private bool themeSubscribed;
    private bool invalidatePending;

    public JsonCodeEditor()
    {
        Loaded += OnEditorLoaded;
        Unloaded += OnEditorUnloaded;
        IsVisibleChanged += (_, _) => ScheduleInvalidateLayers();
        IsEnabledChanged += (_, _) => InvalidateLayers();
        SizeChanged += (_, _) => ScheduleInvalidateLayers();
    }

    public override void OnApplyTemplate()
    {
        if (contentHost is not null)
            contentHost.ScrollChanged -= OnHostScrollChanged;

        base.OnApplyTemplate();

        contentHost = GetTemplateChild("PART_ContentHost") as ScrollViewer;
        contentPresenter = null;
        gutterLayer = GetTemplateChild("PART_GutterLayer") as JsonGutterLayer;
        syntaxLayer = GetTemplateChild("PART_SyntaxLayer") as JsonSyntaxLayer;

        if (gutterLayer is not null) gutterLayer.Editor = this;
        if (syntaxLayer is not null) syntaxLayer.Editor = this;
        if (contentHost is not null) contentHost.ScrollChanged += OnHostScrollChanged;

        EnsureAnalyzed();
        ScheduleInvalidateLayers();
    }

    private void OnEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (!themeSubscribed)
        {
            ApplicationThemeManager.Changed += OnThemeChanged;
            themeSubscribed = true;
        }
        EnsureAnalyzed();
        ScheduleInvalidateLayers();
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        if (themeSubscribed)
        {
            ApplicationThemeManager.Changed -= OnThemeChanged;
            themeSubscribed = false;
        }
    }

    private void OnThemeChanged(ApplicationTheme currentTheme, Color systemAccent) =>
        Dispatcher.BeginInvoke(InvalidateLayers);

    private void OnHostScrollChanged(object sender, ScrollChangedEventArgs e) =>
        InvalidateLayers();

    protected override void OnTextChanged(TextChangedEventArgs e)
    {
        base.OnTextChanged(e);
        EnsureAnalyzed();
        ScheduleInvalidateLayers();
    }

    protected override void OnSelectionChanged(RoutedEventArgs e)
    {
        base.OnSelectionChanged(e);
        InvalidateLayers();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateLayers();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateLayers();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && !IsReadOnly && AcceptsTab)
        {
            int start = SelectionStart;
            SelectedText = "  ";
            SelectionStart = start + 2;
            SelectionLength = 0;
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    internal void InvalidateLayers()
    {
        gutterLayer?.InvalidateVisual();
        syntaxLayer?.InvalidateVisual();
    }

    private void ScheduleInvalidateLayers()
    {
        if (invalidatePending) return;
        invalidatePending = true;
        _ = Dispatcher.BeginInvoke(() =>
        {
            invalidatePending = false;
            InvalidateLayers();
        }, DispatcherPriority.Render);
    }

    private static Palette CurrentPalette =>
        ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? DarkPalette : LightPalette;

    private int FindLogicalLineIndex(int charIndex)
    {
        int count = analyzedLines.Count;
        if (count <= 1) return 0;
        int lo = 0;
        int hi = count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (analyzedLines[mid].CharStart <= charIndex)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return Math.Clamp(hi, 0, count - 1);
    }

    private void EnsureAnalyzed()
    {
        string text = Text ?? string.Empty;
        if (analyzedText is not null &&
            (ReferenceEquals(text, analyzedText) || string.Equals(text, analyzedText, StringComparison.Ordinal)))
            return;

        analyzedText = text;
        var lines = new List<LineInfo>();
        var scopes = new List<Scope>();
        var openStack = new Stack<(int Line, int Level)>();
        int depth = 0;
        int lineIndex = 0;
        int pos = 0;
        int total = text.Length;

        while (true)
        {
            int lineStart = pos;
            while (pos < total && text[pos] != '\n' && text[pos] != '\r') pos++;
            string line = text.Substring(lineStart, pos - lineStart);
            lines.Add(TokenizeLine(lineStart, line, lineIndex, ref depth, openStack, scopes));
            lineIndex++;

            if (pos >= total) break;
            if (text[pos] == '\r' && pos + 1 < total && text[pos + 1] == '\n') pos += 2;
            else pos++;
        }

        analyzedLines = lines;
        analyzedScopes = scopes;
    }

    private static LineInfo TokenizeLine(
        int charStart, string line, int lineIndex, ref int depth,
        Stack<(int Line, int Level)> openStack, List<Scope> scopes)
    {
        int n = line.Length;
        int leadingSpaces = 0;
        for (int lead = 0; lead < n && (line[lead] == ' ' || line[lead] == '\t'); lead++)
            leadingSpaces += line[lead] == '\t' ? 2 : 1;

        var spans = new List<TokenSpan>();
        int i = 0;
        while (i < n)
        {
            char ch = line[i];
            if (ch == ' ' || ch == '\t')
            {
                i++;
                continue;
            }

            if (ch == '"')
            {
                int start = i;
                int j = i + 1;
                while (j < n)
                {
                    if (line[j] == '\\' && j + 1 < n) { j += 2; continue; }
                    if (line[j] == '"') { j++; break; }
                    j++;
                }

                int peek = j;
                while (peek < n && (line[peek] == ' ' || line[peek] == '\t')) peek++;
                bool isKey = peek < n && line[peek] == ':';

                if (isKey)
                {
                    spans.Add(new TokenSpan(start, j - start, TokenKind.Key));
                }
                else
                {
                    int segStart = start;
                    int p = start + 1;
                    int contentEnd = j > start + 1 && line[j - 1] == '"' ? j - 1 : j;
                    while (p < contentEnd)
                    {
                        if (line[p] == '\\' && p + 1 < contentEnd)
                        {
                            if (p > segStart)
                                spans.Add(new TokenSpan(segStart, p - segStart, TokenKind.String));
                            int escLen = line[p + 1] == 'u' && p + 6 <= contentEnd ? 6 : 2;
                            spans.Add(new TokenSpan(p, escLen, TokenKind.Escape));
                            p += escLen;
                            segStart = p;
                        }
                        else
                        {
                            p++;
                        }
                    }
                    if (j > segStart)
                        spans.Add(new TokenSpan(segStart, j - segStart, TokenKind.String));
                }

                i = j;
                continue;
            }

            if (ch == '{' || ch == '[')
            {
                spans.Add(new TokenSpan(i, 1, TokenKind.Bracket, depth));
                openStack.Push((lineIndex, depth));
                depth++;
                i++;
                continue;
            }

            if (ch == '}' || ch == ']')
            {
                depth = Math.Max(0, depth - 1);
                spans.Add(new TokenSpan(i, 1, TokenKind.Bracket, depth));
                if (openStack.Count > 0)
                {
                    var opener = openStack.Pop();
                    if (lineIndex > opener.Line)
                        scopes.Add(new Scope(opener.Line, lineIndex, opener.Level));
                }
                i++;
                continue;
            }

            if (ch == ':' || ch == ',')
            {
                spans.Add(new TokenSpan(i, 1, TokenKind.Punctuation));
                i++;
                continue;
            }

            if (MatchesKeyword(line, i, "true"))
            {
                spans.Add(new TokenSpan(i, 4, TokenKind.BoolNull));
                i += 4;
                continue;
            }
            if (MatchesKeyword(line, i, "false"))
            {
                spans.Add(new TokenSpan(i, 5, TokenKind.BoolNull));
                i += 5;
                continue;
            }
            if (MatchesKeyword(line, i, "null"))
            {
                spans.Add(new TokenSpan(i, 4, TokenKind.BoolNull));
                i += 4;
                continue;
            }

            if (ch == '-' || (ch >= '0' && ch <= '9'))
            {
                int start = i++;
                while (i < n && IsNumberChar(line[i])) i++;
                spans.Add(new TokenSpan(start, i - start, TokenKind.Number));
                continue;
            }

            spans.Add(new TokenSpan(i, 1, TokenKind.Punctuation));
            i++;
        }

        return new LineInfo(charStart, line, leadingSpaces, spans);
    }

    private static bool MatchesKeyword(string line, int index, string keyword)
    {
        if (index + keyword.Length > line.Length) return false;
        if (!line.AsSpan(index, keyword.Length).SequenceEqual(keyword.AsSpan())) return false;
        int after = index + keyword.Length;
        return after >= line.Length || !char.IsLetterOrDigit(line[after]);
    }

    private static bool IsNumberChar(char ch) =>
        (ch >= '0' && ch <= '9') || ch is '.' or 'e' or 'E' or '+' or '-';

    private Scope? FindActiveScope(int activeLine)
    {
        Scope? best = null;
        foreach (var scope in analyzedScopes)
        {
            if (activeLine >= scope.StartLine && activeLine <= scope.EndLine)
            {
                if (best is null || scope.Level > best.Value.Level)
                    best = scope;
            }
        }
        return best;
    }

    private double ConfiguredLineHeight
    {
        get
        {
            double lh = TextBlock.GetLineHeight(this);
            return !double.IsNaN(lh) && lh > 0 ? lh : 20.0;
        }
    }

    private ScrollContentPresenter? GetContentPresenter()
    {
        if (contentPresenter is not null && IsAncestorOf(contentPresenter))
            return contentPresenter;
        if (contentHost is null) return null;
        contentPresenter = contentHost.Template?.FindName("PART_ScrollContentPresenter", contentHost) as ScrollContentPresenter;
        return contentPresenter;
    }

    private Rect GetViewportClipRect(FrameworkElement layer, bool fullLayerWidth)
    {
        if (GetContentPresenter() is { ActualWidth: > 0, ActualHeight: > 0 } presenter && IsAncestorOf(presenter))
        {
            Point tl = presenter.TranslatePoint(new Point(0, 0), layer);
            double x = fullLayerWidth ? 0 : tl.X;
            double w = fullLayerWidth ? layer.ActualWidth : presenter.ActualWidth;
            return new Rect(x, tl.Y, Math.Max(0, w), Math.Max(0, presenter.ActualHeight));
        }
        return new Rect(0, 0, Math.Max(0, layer.ActualWidth), Math.Max(0, layer.ActualHeight));
    }

    private (int StartLine, int EndLine) EstimateVisibleLogicalRange(double viewportHeight)
    {
        int total = analyzedLines.Count;
        if (total == 0) return (0, -1);
        if (total <= 300) return (0, total - 1);
        double lh = ConfiguredLineHeight;
        double vOffset = contentHost?.VerticalOffset ?? 0;
        int start = Math.Clamp((int)Math.Floor((vOffset - Padding.Top) / lh) - 6, 0, total - 1);
        int visibleCount = (int)Math.Ceiling(viewportHeight / lh) + 14;
        int end = Math.Clamp(start + visibleCount, start, total - 1);
        return (start, end);
    }

    private bool TryGetCharOrigin(int charIndex, FrameworkElement layer, out Point origin, out double rectHeight)
    {
        origin = default;
        rectHeight = ConfiguredLineHeight;
        int textLen = Text?.Length ?? 0;
        if (charIndex < 0 || charIndex > textLen || !IsAncestorOf(layer))
            return false;

        Rect rect = GetRectFromCharacterIndex(charIndex, trailingEdge: false);
        if (rect.IsEmpty)
            return false;

        origin = TranslatePoint(rect.TopLeft, layer);
        rectHeight = rect.Height > 0 ? rect.Height : ConfiguredLineHeight;
        return true;
    }

    internal void RenderGutter(DrawingContext dc, FrameworkElement layer)
    {
        EnsureAnalyzed();
        Rect clipRect = GetViewportClipRect(layer, fullLayerWidth: true);
        if (clipRect.Width <= 0 || clipRect.Height <= 0 || analyzedLines.Count == 0) return;

        var palette = CurrentPalette;
        double pixelsPerDip = VisualTreeHelper.GetDpi(layer).PixelsPerDip;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeights.Normal, FontStretch);
        double fontSize = FontSize;
        double rightEdge = layer.ActualWidth - 16.0;
        int activeLine = FindLogicalLineIndex(CaretIndex);
        Brush? disabledBrush = !IsEnabled ? TryFindResource("TextFillColorDisabledBrush") as Brush : null;

        dc.PushClip(new RectangleGeometry(clipRect));

        var (startLine, endLine) = EstimateVisibleLogicalRange(clipRect.Height);
        double slotHeight = ConfiguredLineHeight;
        bool needRetry = false;

        for (int lineIdx = startLine; lineIdx <= endLine; lineIdx++)
        {
            var info = analyzedLines[lineIdx];
            if (!TryGetCharOrigin(info.CharStart, layer, out Point origin, out double rectHeight))
            {
                needRetry = true;
                continue;
            }
            if (origin.Y + slotHeight < clipRect.Top - slotHeight) continue;
            if (origin.Y > clipRect.Bottom + slotHeight) break;

            Brush brush = disabledBrush ?? (lineIdx == activeLine ? palette.ActiveLineNumber : palette.LineNumber);
            DrawLineNumber(dc, lineIdx + 1, origin.Y, rectHeight, rightEdge, typeface, fontSize, pixelsPerDip, brush);
        }

        dc.Pop();
        if (needRetry) ScheduleInvalidateLayers();
    }

    private static void DrawLineNumber(
        DrawingContext dc, int number, double y, double lineRectHeight, double rightEdge,
        Typeface typeface, double fontSize, double pixelsPerDip, Brush brush)
    {
        var ft = new FormattedText(
            number.ToString(CultureInfo.InvariantCulture),
            ZhCulture, FlowDirection.LeftToRight, typeface, fontSize, brush,
            null, TextFormattingMode.Ideal, pixelsPerDip);
        double yOffset = Math.Max(0, (lineRectHeight - ft.Height) * 0.5);
        dc.DrawText(ft, new Point(rightEdge - ft.Width, y + yOffset));
    }

    internal void RenderSyntax(DrawingContext dc, FrameworkElement layer)
    {
        EnsureAnalyzed();
        Rect clipRect = GetViewportClipRect(layer, fullLayerWidth: false);
        if (clipRect.Width <= 0 || clipRect.Height <= 0 || analyzedLines.Count == 0) return;

        var palette = CurrentPalette;
        double pixelsPerDip = VisualTreeHelper.GetDpi(layer).PixelsPerDip;
        var typeface = new Typeface(FontFamily, FontStyle, FontWeights.Normal, FontStretch);
        double fontSize = FontSize;
        double slotHeight = ConfiguredLineHeight;
        int activeLine = FindLogicalLineIndex(CaretIndex);
        Scope? activeScope = FindActiveScope(activeLine);
        Brush? disabledBrush = !IsEnabled ? TryFindResource("TextFillColorDisabledBrush") as Brush : null;

        dc.PushClip(new RectangleGeometry(clipRect));

        var (startLine, endLine) = EstimateVisibleLogicalRange(clipRect.Height);
        bool needRetry = false;

        // 1. Active line subtle fill highlight (matches VS Code current line highlight)
        if (activeLine >= startLine && activeLine <= endLine &&
            TryGetCharOrigin(analyzedLines[activeLine].CharStart, layer, out Point activeOrigin, out double activeRectHeight))
        {
            double topY = Math.Round(activeOrigin.Y - Math.Max(0, (slotHeight - activeRectHeight) * 0.5));
            dc.DrawRectangle(palette.ActiveLineBackground, null, new Rect(clipRect.Left, topY, clipRect.Width, slotHeight));
        }

        // 2. Visible logical lines: indent guides + syntax colored text
        for (int lineIdx = startLine; lineIdx <= endLine; lineIdx++)
        {
            var info = analyzedLines[lineIdx];
            if (!TryGetCharOrigin(info.CharStart, layer, out Point origin, out double rectHeight))
            {
                needRetry = true;
                continue;
            }
            if (origin.Y + slotHeight < clipRect.Top - slotHeight) continue;
            if (origin.Y > clipRect.Bottom + slotHeight) break;

            double slotTop = origin.Y - Math.Max(0, (slotHeight - rectHeight) * 0.5);

            if (info.LeadingSpaces >= 2)
            {
                int levels = info.LeadingSpaces / 2;
                double hairline = 1.0 / pixelsPerDip;
                double gy1 = Math.Round(slotTop * pixelsPerDip) / pixelsPerDip;
                double gy2 = Math.Round((slotTop + slotHeight) * pixelsPerDip) / pixelsPerDip;
                double gh = Math.Max(hairline, gy2 - gy1);
                for (int lvl = 0; lvl < levels; lvl++)
                {
                    if (!TryGetCharOrigin(info.CharStart + lvl * 2, layer, out Point guideOrigin, out _))
                        break;
                    double gx = Math.Floor(guideOrigin.X * pixelsPerDip) / pixelsPerDip;
                    bool isActiveGuide = activeScope is { } s &&
                        s.Level == lvl &&
                        lineIdx > s.StartLine &&
                        lineIdx < s.EndLine;
                    Brush guideBrush = isActiveGuide ? palette.ActiveIndentGuide : palette.IndentGuide;
                    dc.DrawRectangle(guideBrush, null, new Rect(gx, gy1, hairline, gh));
                }
            }

            if (info.Text.Length == 0) continue;

            var ft = new FormattedText(
                info.Text, ZhCulture, FlowDirection.LeftToRight, typeface, fontSize,
                disabledBrush ?? palette.Punctuation,
                null, TextFormattingMode.Ideal, pixelsPerDip);

            if (disabledBrush is null)
            {
                foreach (var span in info.Spans)
                {
                    if (span.Start >= info.Text.Length) break;
                    int len = Math.Min(span.Length, info.Text.Length - span.Start);
                    if (len <= 0) continue;
                    ft.SetForegroundBrush(ResolveBrush(span, palette), span.Start, len);
                }
            }

            double yOffset = Math.Max(0, (rectHeight - ft.Height) * 0.5);
            dc.DrawText(ft, new Point(origin.X, origin.Y + yOffset));
        }

        dc.Pop();
        if (needRetry) ScheduleInvalidateLayers();
    }

    private static Brush ResolveBrush(TokenSpan span, Palette p) => span.Kind switch
    {
        TokenKind.Key => p.Key,
        TokenKind.String => p.String,
        TokenKind.Escape => p.Escape,
        TokenKind.BoolNull => p.BoolNull,
        TokenKind.Number => p.Number,
        TokenKind.Bracket => p.Brackets[span.BracketLevel % p.Brackets.Length],
        _ => p.Punctuation
    };

    private static Palette CreatePalette(
        string key, string str, string escape, string boolNull, string number, string punctuation,
        string[] brackets, string lineNumber, string activeLineNumber, string activeLineBackground,
        string indentGuide, string activeIndentGuide)
    {
        var bracketBrushes = new Brush[brackets.Length];
        for (int i = 0; i < brackets.Length; i++)
            bracketBrushes[i] = FrozenBrush(brackets[i]);

        return new Palette(
            Key: FrozenBrush(key),
            String: FrozenBrush(str),
            Escape: FrozenBrush(escape),
            BoolNull: FrozenBrush(boolNull),
            Number: FrozenBrush(number),
            Punctuation: FrozenBrush(punctuation),
            Brackets: bracketBrushes,
            LineNumber: FrozenBrush(lineNumber),
            ActiveLineNumber: FrozenBrush(activeLineNumber),
            ActiveLineBackground: FrozenBrush(activeLineBackground),
            IndentGuide: FrozenBrush(indentGuide),
            ActiveIndentGuide: FrozenBrush(activeIndentGuide));
    }

    private static SolidColorBrush FrozenBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}

public sealed class JsonGutterLayer : FrameworkElement
{
    internal JsonCodeEditor? Editor { get; set; }

    protected override void OnRender(DrawingContext drawingContext) =>
        Editor?.RenderGutter(drawingContext, this);
}

public sealed class JsonSyntaxLayer : FrameworkElement
{
    internal JsonCodeEditor? Editor { get; set; }

    protected override void OnRender(DrawingContext drawingContext) =>
        Editor?.RenderSyntax(drawingContext, this);
}
