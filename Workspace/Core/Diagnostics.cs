using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GitWorkspace.Core;

public sealed record Problem(string Source, string Severity, string File, int Line, int Column, string Message, string Code = "");
public sealed record AnalysisResult(IReadOnlyList<Problem> Problems, string Log, bool Failed);

public static partial class Diagnostics
{
    public static async Task<AnalysisResult> DartStackAsync(string root, CancellationToken cancellation)
    {
        var flutter = Processes.Find("flutter") ?? Processes.Find("flutter.bat")
            ?? new[] { @"C:\src\flutter\bin\flutter.bat", @"C:\flutter\bin\flutter.bat" }.FirstOrDefault(File.Exists);
        if (flutter != null) return await FlutterAsync(root, flutter, cancellation);
        return await DartAsync(root, cancellation);
    }

    public static Task<AnalysisResult> FlutterAsync(string root, CancellationToken cancellation) =>
        DartStackAsync(root, cancellation);

    private static async Task<AnalysisResult> FlutterAsync(string root, string flutter, CancellationToken cancellation)
    {
        var report = Path.Combine(Path.GetTempPath(), $"gitworkspace-analysis-{Guid.NewGuid():N}.txt");
        try
        {
            // Use Flutter's supported report file, not locale-sensitive console decorations.
            var result = await Processes.RunAsync(flutter, ["--no-color", "analyze", "--no-pub", "--write", report], root, cancellation);
            var problems = File.Exists(report) ? ParseFlutter(await File.ReadAllTextAsync(report, cancellation), root) : [];
            return new(problems, result.Display, !File.Exists(report) || (result.ExitCode != 0 && problems.Count == 0));
        }
        finally { if (File.Exists(report)) File.Delete(report); }
    }

    public static async Task<AnalysisResult> DartAsync(string root, CancellationToken cancellation)
    {
        var dart = Processes.Find("dart") ?? Processes.Find("dart.exe")
            ?? throw new FileNotFoundException("Neither Flutter nor the Dart SDK was found on PATH.");
        var result = await Processes.RunAsync(dart, ["analyze", "--format=machine"], root, cancellation, 8 * 1024 * 1024);
        var problems = ParseDartMachine(result.Output + "\n" + result.Error, root);
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    /// <param name="moduleDirectory">Directory that contains go.mod (repo root or a nested module).</param>
    public static async Task<AnalysisResult> GoAsync(string moduleDirectory, CancellationToken cancellation)
    {
        var directory = Path.GetFullPath(moduleDirectory);
        if (!File.Exists(Path.Combine(directory, "go.mod")))
            throw new FileNotFoundException("go.mod was not found in " + directory);
        var result = await Processes.RunAsync(Processes.Find("go.exe") ?? throw new FileNotFoundException("Go was not found on PATH."),
            ["vet", "-json", "./..."], directory, cancellation, 8 * 1024 * 1024);
        var problems = ParseGo(result.Output + "\n" + result.Error, directory);
        // Vet findings can have a nonzero status. Keep tool failures distinct from clean analysis.
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    public static async Task<AnalysisResult> PythonAsync(string root, CancellationToken cancellation)
    {
        var ruff = Processes.Find("ruff") ?? Processes.Find("ruff.exe");
        if (ruff == null) throw new FileNotFoundException("ruff was not found on PATH. Install ruff for Python CLI snapshots.");
        var result = await Processes.RunAsync(ruff, ["check", "--output-format", "json", "."], root, cancellation, 8 * 1024 * 1024);
        var problems = ParseRuff(result.Output, root);
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    public static List<Problem> ParseRuff(string json, string root)
    {
        var result = new List<Problem>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var path = item.TryGetProperty("filename", out var file) ? file.GetString() ?? "" : "";
                var message = item.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                var code = item.TryGetProperty("code", out var codeNode) ? codeNode.GetString() ?? "" : "";
                var severity = "warning";
                if (item.TryGetProperty("severity", out var severityNode))
                {
                    var raw = severityNode.ValueKind == JsonValueKind.Number
                        ? severityNode.GetInt32().ToString()
                        : severityNode.GetString() ?? "";
                    severity = raw is "1" or "error" ? "error" : "warning";
                }
                var line = 1;
                var column = 1;
                if (item.TryGetProperty("location", out var location))
                {
                    if (location.TryGetProperty("row", out var row)) line = row.GetInt32();
                    if (location.TryGetProperty("column", out var col)) column = col.GetInt32();
                }
                if (path.Length == 0) continue;
                result.Add(new("Python", severity, Path.GetFullPath(path, root), line, column, message, code));
            }
        }
        catch (JsonException)
        {
            // Keep CLI log; treat as tool failure via empty parse.
        }
        return result;
    }

    [GeneratedRegex(@"^(INFO|WARNING|ERROR)\|[^|]*\|([^|]*)\|([^|]+)\|(\d+)\|(\d+)\|\d+\|(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DartMachinePattern();
    public static List<Problem> ParseDartMachine(string text, string root)
    {
        var result = new List<Problem>();
        foreach (var line in text.Split('\n'))
        {
            var match = DartMachinePattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var severity = match.Groups[1].Value.ToLowerInvariant() switch
            {
                "error" => "error",
                "warning" => "warning",
                _ => "info"
            };
            var file = match.Groups[3].Value;
            if (file.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(file, UriKind.Absolute, out var uri))
                    file = uri.LocalPath;
            }
            result.Add(new("Dart", severity, Path.GetFullPath(file, root),
                int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value),
                match.Groups[6].Value, match.Groups[2].Value));
        }
        return result;
    }

    [GeneratedRegex(@"^\[(error|warning|info)\] (.*) \((.*):(\d+):(\d+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex FlutterPattern();
    public static List<Problem> ParseFlutter(string report, string root)
    {
        var result = new List<Problem>();
        foreach (var line in report.Split('\n'))
        {
            var match = FlutterPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            result.Add(new("Flutter", match.Groups[1].Value, Path.GetFullPath(match.Groups[3].Value, root),
                int.Parse(match.Groups[4].Value), int.Parse(match.Groups[5].Value), match.Groups[2].Value));
        }
        return result;
    }

    [GeneratedRegex(@"^(.*?):(\d+)(?::(\d+))?:\s*(.*)$")]
    private static partial Regex PositionPattern();
    public static List<Problem> ParseGo(string text, string directory)
    {
        var result = new List<Problem>();
        var json = new System.Text.StringBuilder();
        var depth = 0;
        var inString = false;
        var escaped = false;
        // go vet emits one JSON object per package, interleaved with '# package' lines.
        foreach (var line in text.Split('\n'))
        {
            if (depth == 0 && !line.TrimStart().StartsWith('{'))
            {
                var plain = line.Trim();
                if (plain.StartsWith("vet: ", StringComparison.Ordinal)) plain = plain[5..];
                var compiler = PositionPattern().Match(plain);
                if (compiler.Success) result.Add(PositionProblem(compiler, directory, "error", "compiler"));
                continue;
            }
            json.AppendLine(line);
            foreach (var character in line)
            {
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (character == '\\') escaped = true;
                    else if (character == '"') inString = false;
                }
                else if (character == '"') inString = true;
                else if (character == '{') depth++;
                else if (character == '}') depth--;
            }
            if (depth != 0) continue;
            using var document = JsonDocument.Parse(json.ToString());
            foreach (var package in document.RootElement.EnumerateObject())
                foreach (var analyzer in package.Value.EnumerateObject())
                    if (analyzer.Value.ValueKind == JsonValueKind.Array)
                        foreach (var finding in analyzer.Value.EnumerateArray())
                        {
                            if (!finding.TryGetProperty("posn", out var pos) || !finding.TryGetProperty("message", out var message)) continue;
                            var match = PositionPattern().Match(pos.GetString() + ": " + message.GetString());
                            if (match.Success) result.Add(PositionProblem(match, directory, "warning", analyzer.Name));
                        }
            json.Clear();
        }
        return result.Distinct().ToList();
    }

    private static Problem PositionProblem(Match match, string root, string severity, string code) =>
        new("Go", severity, Path.GetFullPath(match.Groups[1].Value, root), int.Parse(match.Groups[2].Value),
            int.TryParse(match.Groups[3].Value, out var column) ? column : 1, match.Groups[4].Value, code);
}
