using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SiteLift.Core;

public sealed record WslDistributionRow(
    string Name,
    bool IsDefault,
    string StateLabel,
    bool IsRunning,
    int Version);

public static class WslOutputParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> RunningStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Running", "正在运行", "运行中", "執行中", "正在執行",
        "実行中", "Wird ausgeführt", "En cours d'exécution", "실행 중"
    };

    private static readonly HashSet<string> StoppedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "Stopped", "已停止", "停止", "停止済み", "Angehalten", "Arrêté", "중지됨"
    };

    public static bool HostNeedsSetup(string output) =>
        Regex.IsMatch(output,
            @"REGDB_E_CLASSNOTREG|WSL_E_(?:WSL_NOT_INSTALLED|WSL_OPTIONAL_COMPONENT_REQUIRED|INSTALL_COMPONENT_REQUIRED)|WSL installation appears to be corrupted|Windows Subsystem for Linux is not installed",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsEmptyDistributionList(string output) =>
        Regex.IsMatch(output,
            @"WSL_E_NO_DISTRO|has no installed distributions|没有(?:已)?安装的\s*(?:Linux\s*)?(?:分发版|发行版)|未安装任何\s*(?:Linux\s*)?(?:分发版|发行版)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsSystemUtilityDistribution(string name) =>
        name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("rancher-desktop-data", StringComparison.OrdinalIgnoreCase);

    public static string ExtractWslVersion(string output) =>
        Regex.Match(output, @"\d+(?:\.\d+){2,3}").Value;

    public static string ExtractInstallHelp(string help) =>
        Regex.Match(help, @"(?ms)^\s{4}--install\b.*?(?=^ {4}--\w|\z)").Value;

    public static IReadOnlyList<WslDistributionRow> ParseDistributionList(string output)
    {
        var rows = new List<WslDistributionRow>();
        foreach (string line in output.Split('\n'))
        {
            var match = Regex.Match(line.TrimEnd('\r', ' ', '\t'), @"^\s*(\*?)\s*(.+?)\s{2,}(.+?)\s{2,}([12])$");
            if (!match.Success) continue;
            string name = match.Groups[2].Value.Trim();
            string rawState = match.Groups[3].Value.Trim();
            bool running = RunningStates.Contains(rawState);
            string normalizedState = running ? "运行中"
                : StoppedStates.Contains(rawState) ? "已停止"
                : rawState;
            rows.Add(new WslDistributionRow(
                name,
                match.Groups[1].Length > 0,
                normalizedState,
                running,
                int.Parse(match.Groups[4].Value)));
        }
        return rows;
    }

    public static string[] ParseOnlineDistributions(string output) =>
        output.Split('\n')
            .Select(line => Regex.Match(line, @"^\s*([A-Za-z0-9][A-Za-z0-9._-]*)\s{2,}\S"))
            .Where(match => match.Success && !match.Groups[1].Value.Equals("NAME", StringComparison.OrdinalIgnoreCase))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // The WSL host emits UTF-16LE while e2fsck and resize2fs emit UTF-8.
    // UTF-16LE Chinese characters (e.g. '上' = U+4E0A = bytes 0A 4E) contain byte 0x0A
    // in their low byte; splitting naively on 0x0A corrupts the character and misaligns the rest of the line.
    public static string DecodeResizeOutput(byte[] output)
    {
        var decoder = new IncrementalResizeDecoder();
        var lines = new List<string>();
        decoder.Append(output, lines.Add);
        decoder.Flush(lines.Add);
        return string.Join("\n", lines);
    }

    public sealed class IncrementalResizeDecoder
    {
        private readonly List<byte> buffer = new();

        public void Append(ReadOnlySpan<byte> chunk, Action<string> onLine)
        {
            for (int i = 0; i < chunk.Length; i++)
                buffer.Add(chunk[i]);
            DrainCompleteLines(onLine);
        }

        public void Flush(Action<string> onLine)
        {
            DrainCompleteLines(onLine);
            if (buffer.Count == 0) return;
            var remaining = buffer.ToArray();
            buffer.Clear();
            string decoded = DecodeSegment(remaining, forceWide: DetectUtf16Line(remaining));
            EmitLines(decoded, onLine);
        }

        private void DrainCompleteLines(Action<string> onLine)
        {
            while (buffer.Count > 0)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(buffer);
                if (!TryFindLineEnd(span, out int lineLength, out int consumed, out bool isWide))
                    break;

                var segment = span[..lineLength];
                string decoded = DecodeSegment(segment, isWide);
                buffer.RemoveRange(0, consumed);
                EmitLines(decoded, onLine);
            }
        }

        private static bool TryFindLineEnd(ReadOnlySpan<byte> data, out int lineLength, out int consumed, out bool isWide)
        {
            bool wideCandidate = StartsWithBom(data) || ContainsZeroBeforeFirstNewline(data);
            if (wideCandidate)
            {
                int offset = StartsWithBom(data) ? 2 : 0;
                for (int i = offset; i + 1 < data.Length; i += 2)
                {
                    if (data[i] == (byte)'\n' && data[i + 1] == 0)
                    {
                        lineLength = i;
                        consumed = i + 2;
                        isWide = true;
                        return true;
                    }
                }
                lineLength = consumed = 0;
                isWide = true;
                return false;
            }

            // No NUL bytes seen before a potential '\n': could be UTF-8, or a pure-CJK UTF-16LE line
            // whose first NUL only appears at '\r\0\n\0' or '\n\0'.
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] != (byte)'\n') continue;

                // Even-aligned 0x0A 0x00 is unambiguously UTF-16LE LF.
                if (i % 2 == 0 && i + 1 < data.Length && data[i + 1] == 0)
                {
                    lineLength = i;
                    consumed = i + 2;
                    isWide = true;
                    return true;
                }

                // Wait for one more byte if 0x0A is at the very end on an even offset and preceding bytes aren't valid UTF-8.
                var prefix = data[..i];
                bool validUtf8 = IsValidStrictUtf8(prefix);
                if (i == data.Length - 1 && i % 2 == 0 && !validUtf8)
                {
                    lineLength = consumed = 0;
                    isWide = true;
                    return false;
                }

                // Special case: line starts with byte 0x0A followed by non-zero byte.
                // Could be a UTF-16LE character (e.g. '上' = 0A 4E) or a blank UTF-8 line followed by UTF-8 text.
                if (i == 0)
                {
                    if (data.Length < 2)
                    {
                        lineLength = consumed = 0;
                        isWide = false;
                        return false;
                    }
                    var rest = data[1..];
                    var utf8Check = CheckUtf8Prefix(rest);
                    if (utf8Check == Utf8PrefixState.Incomplete)
                    {
                        lineLength = consumed = 0;
                        isWide = false;
                        return false;
                    }
                    if (utf8Check == Utf8PrefixState.Invalid || HasEvenAlignedWideNewline(data))
                        continue;
                }

                if (validUtf8)
                {
                    lineLength = i;
                    consumed = i + 1;
                    isWide = false;
                    return true;
                }
                // Prefix was not valid UTF-8, so this 0x0A is inside a UTF-16LE character; keep scanning!
            }

            lineLength = consumed = 0;
            isWide = false;
            return false;
        }

        private enum Utf8PrefixState { Valid, Incomplete, Invalid }

        private static Utf8PrefixState CheckUtf8Prefix(ReadOnlySpan<byte> bytes)
        {
            int i = 0;
            while (i < bytes.Length)
            {
                byte b = bytes[i];
                if (b == 0) return Utf8PrefixState.Invalid;
                if (b == (byte)'\n') return Utf8PrefixState.Valid;
                if (b < 0x80)
                {
                    i++;
                    continue;
                }
                int expected = b switch
                {
                    >= 0xC2 and <= 0xDF => 2,
                    >= 0xE0 and <= 0xEF => 3,
                    >= 0xF0 and <= 0xF4 => 4,
                    _ => -1
                };
                if (expected < 0) return Utf8PrefixState.Invalid;
                if (i + expected > bytes.Length)
                {
                    for (int j = i + 1; j < bytes.Length; j++)
                        if ((bytes[j] & 0xC0) != 0x80) return Utf8PrefixState.Invalid;
                    return Utf8PrefixState.Incomplete;
                }
                for (int j = 1; j < expected; j++)
                    if ((bytes[i + j] & 0xC0) != 0x80) return Utf8PrefixState.Invalid;
                if (!IsValidStrictUtf8(bytes.Slice(i, expected)))
                    return Utf8PrefixState.Invalid;
                i += expected;
            }
            return Utf8PrefixState.Incomplete;
        }

        private static bool StartsWithBom(ReadOnlySpan<byte> data) =>
            data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE;

        private static bool ContainsZeroBeforeFirstNewline(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i < data.Length; i++)
            {
                if (data[i] == 0) return true;
                if (data[i] == (byte)'\n')
                {
                    if (i + 1 < data.Length && data[i + 1] == 0) return true;
                    if (IsValidStrictUtf8(data[..i]) && !(i == 0 && HasEvenAlignedWideNewline(data)))
                        return false;
                }
            }
            return false;
        }

        private static bool HasEvenAlignedWideNewline(ReadOnlySpan<byte> data)
        {
            for (int i = 0; i + 1 < data.Length; i += 2)
            {
                if (data[i] == (byte)'\n' && data[i + 1] == 0)
                    return true;
            }
            return false;
        }

        private static bool DetectUtf16Line(ReadOnlySpan<byte> data) =>
            StartsWithBom(data) || data.IndexOf((byte)0) >= 0 || (!IsValidStrictUtf8(data) && data.Length % 2 == 0);

        private static bool IsValidStrictUtf8(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IndexOf((byte)0) >= 0) return false;
            try
            {
                StrictUtf8.GetCharCount(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static string DecodeSegment(ReadOnlySpan<byte> bytes, bool forceWide)
        {
            if (bytes.Length == 0) return "";
            if (forceWide)
            {
                if (StartsWithBom(bytes)) bytes = bytes[2..];
                if (bytes.Length % 2 != 0) bytes = bytes[..^1];
                return Encoding.Unicode.GetString(bytes);
            }
            return Encoding.UTF8.GetString(bytes);
        }

        private static void EmitLines(string decoded, Action<string> onLine)
        {
            string normalized = decoded.Replace("\r\n", "\n").Replace('\r', '\n');
            foreach (string raw in normalized.Split('\n'))
            {
                string line = raw.Trim('\uFEFF', '\0', '\r', '\n');
                if (line.Length > 0)
                    onLine(line);
            }
        }
    }
}
