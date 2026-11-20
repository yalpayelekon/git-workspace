using System.Text.RegularExpressions;

namespace GitWorkspace.Core;

public sealed record DiffLine(string OldText, string NewText, int? OldNumber, int? NewNumber, bool Changed);
public static partial class DiffAlignment
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Multiline)]
    private static partial Regex Hunks();
    public static IReadOnlyList<DiffLine> Align(string before, string after, string patch)
    {
        static string[] Lines(string text)
        {
            if (text.Length == 0) return [];
            var lines = text.Replace("\r\n", "\n").Split('\n');
            return text.EndsWith('\n') ? lines[..^1] : lines;
        }
        var oldLines = Lines(before); var newLines = Lines(after);
        var rows = new List<DiffLine>(); var old = 0; var next = 0;
        foreach (Match hunk in Hunks().Matches(patch))
        {
            var removed = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;
            var added = hunk.Groups[4].Success ? int.Parse(hunk.Groups[4].Value) : 1;
            var oldStart = int.Parse(hunk.Groups[1].Value) - (removed == 0 ? 0 : 1);
            var newStart = int.Parse(hunk.Groups[3].Value) - (added == 0 ? 0 : 1);
            while (old < oldStart || next < newStart)
            {
                var left = old < oldStart && old < oldLines.Length; var right = next < newStart && next < newLines.Length;
                if (!left && !right) break;
                rows.Add(new(left ? oldLines[old] : "", right ? newLines[next] : "", left ? ++old : null, right ? ++next : null, false));
            }
            for (var i = 0; i < Math.Max(removed, added); i++)
            {
                var left = i < removed && old < oldLines.Length; var right = i < added && next < newLines.Length;
                rows.Add(new(left ? oldLines[old] : "", right ? newLines[next] : "", left ? ++old : null, right ? ++next : null, true));
            }
        }
        while (old < oldLines.Length || next < newLines.Length)
        {
            var left = old < oldLines.Length; var right = next < newLines.Length;
            rows.Add(new(left ? oldLines[old] : "", right ? newLines[next] : "", left ? ++old : null, right ? ++next : null, false));
        }
        return rows;
    }
}
