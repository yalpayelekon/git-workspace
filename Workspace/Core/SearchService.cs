using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GitWorkspace.Core;

public sealed record SearchRequest(string Query, bool MatchCase, bool WholeWord, bool UseRegex, string Include, string Exclude);
public sealed record SearchHit(string Path, int Line, int Column, string Preview);
public sealed record SearchFileGroup(string Path, IReadOnlyList<SearchHit> Hits);
public sealed record SearchResult(IReadOnlyList<SearchFileGroup> Files, int MatchCount, bool Truncated, string? Error);

public sealed class SearchService(string root)
{
    public const int MaxMatches = 2000;
    private static readonly TimeSpan RegexBudget = TimeSpan.FromMilliseconds(50);
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\][^\x07]*(?:\x07|\x1B\\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HitLine = new(@"^(.*?):(\d+):(.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public Task<SearchResult> SearchAsync(SearchRequest request, CancellationToken cancellation = default) =>
        SearchAsync(request.Query, request.MatchCase, request.WholeWord, request.UseRegex, request.Include, request.Exclude, cancellation);

    public async Task<SearchResult> SearchAsync(
        string query,
        bool matchCase,
        bool wholeWord,
        bool useRegex,
        string include,
        string exclude,
        CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new([], 0, false, null);

        if (useRegex)
        {
            try { _ = Compile(query, matchCase); }
            catch (ArgumentException ex) { return new([], 0, false, "Invalid regular expression: " + ex.Message); }
        }

        var git = Processes.Find("git.exe")
            ?? throw new InvalidOperationException("Install Git for Windows and reopen the app.");
        var args = new List<string>
        {
            "--no-optional-locks",
            "-c", "core.quotepath=false",
            "-c", "color.grep=never",
            "-C", root,
            "grep", "--color=never", "-n", "-I", "--untracked",
        };
        if (!matchCase) args.Add("-i");
        if (wholeWord) args.Add("-w");
        args.Add(useRegex ? "-E" : "-F");
        args.Add("-e");
        args.Add(query);
        args.Add("--");

        var includes = SplitGlobs(include).Select(NormalizeGlob).ToArray();
        var excludes = SplitGlobs(exclude).Select(NormalizeGlob).ToArray();
        if (includes.Length == 0 && excludes.Length > 0) args.Add(".");
        foreach (var pattern in includes) args.Add($":(glob){pattern}");
        foreach (var pattern in excludes) args.Add($":(exclude,glob){pattern}");

        // Allow larger grep payloads than a single file preview; still bound for UI safety.
        var command = await Processes.RunAsync(git, args, root, cancellation, maxCharacters: 8 * 1024 * 1024);
        if (command.ExitCode > 1)
        {
            var error = command.Error.Trim();
            if (error.Length == 0) error = command.Output.Trim();
            if (error.Length == 0) error = "Search failed.";
            return new([], 0, false, error);
        }

        return Parse(command.Output, command.Truncated);
    }

    public static SearchResult Parse(string output, bool outputTruncated = false)
    {
        var groups = new Dictionary<string, List<SearchHit>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        var truncated = outputTruncated;
        var count = 0;
        using var reader = new StringReader(output);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            if (!TryParseHit(line, out var hit)) continue;
            if (!groups.TryGetValue(hit.Path, out var hits))
            {
                hits = [];
                groups[hit.Path] = hits;
                order.Add(hit.Path);
            }
            hits.Add(hit);
            count++;
            if (count < MaxMatches) continue;

            // Exactly MaxMatches is a full page, not truncation, unless another hit remains.
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                if (!TryParseHit(line, out _)) continue;
                truncated = true;
                break;
            }
            break;
        }

        var files = order.Select(path => new SearchFileGroup(path, groups[path])).ToArray();
        return new(files, count, truncated, null);
    }

    public static bool TryParseHit(string line, out SearchHit hit)
    {
        hit = null!;
        line = StripAnsi(line);
        // git grep -n: path:line:text  (path may contain ':' on uncommon setups; prefer :digits:)
        var match = HitLine.Match(line);
        if (!match.Success) return false;
        var path = DecodeGitPath(match.Groups[1].Value).Replace('\\', '/');
        if (!int.TryParse(match.Groups[2].Value, out var lineNumber) || lineNumber <= 0) return false;
        var preview = StripAnsi(match.Groups[3].Value).TrimEnd();
        if (preview.Length > 240) preview = preview[..237] + "...";
        hit = new(path, lineNumber, 1, preview);
        return true;
    }

    public static int EstimateColumn(string preview, string query, bool matchCase, bool useRegex)
    {
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(preview)) return 1;
        try
        {
            if (useRegex)
            {
                var found = Compile(query, matchCase).Match(preview);
                return found.Success ? found.Index + 1 : 1;
            }
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var index = preview.IndexOf(query, comparison);
            return index >= 0 ? index + 1 : 1;
        }
        catch (ArgumentException) { return 1; }
        catch (RegexMatchTimeoutException) { return 1; }
    }

    public static string DecodeGitPath(string path)
    {
        path = path.Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
            path = path[1..^1];
        if (!path.Contains('\\', StringComparison.Ordinal)) return path;

        // Git quotes UTF-8 bytes as octal escapes (\304\237 → ğ). Decode bytes, then UTF-8.
        var bytes = new List<byte>(path.Length);
        for (var i = 0; i < path.Length; i++)
        {
            if (path[i] != '\\' || i + 1 >= path.Length)
            {
                AppendUtf8(bytes, path[i]);
                continue;
            }

            var next = path[++i];
            if (next is >= '0' and <= '7')
            {
                var length = 1;
                while (length < 3 && i + length < path.Length && path[i + length] is >= '0' and <= '7') length++;
                bytes.Add((byte)Convert.ToInt32(path.Substring(i, length), 8));
                i += length - 1;
                continue;
            }

            AppendUtf8(bytes, next switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '"' => '"',
                '\\' => '\\',
                'a' => '\a',
                'b' => '\b',
                'f' => '\f',
                'v' => '\v',
                _ => next,
            });
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void AppendUtf8(List<byte> bytes, char character)
    {
        if (character < 0x80) bytes.Add((byte)character);
        else bytes.AddRange(Encoding.UTF8.GetBytes([character]));
    }

    public static string StripAnsi(string text) =>
        string.IsNullOrEmpty(text) ? text : AnsiEscape.Replace(text, "");

    private static Regex Compile(string query, bool matchCase) =>
        new(query, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, RegexBudget);

    private static IEnumerable<string> SplitGlobs(string text) =>
        (text ?? "").Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => part.Length > 0);

    private static string NormalizeGlob(string pattern)
    {
        pattern = pattern.Trim().Replace('\\', '/');
        if (pattern.Length == 0) return pattern;
        if (!pattern.Contains('/')) pattern = "**/" + pattern;
        return pattern;
    }
}
