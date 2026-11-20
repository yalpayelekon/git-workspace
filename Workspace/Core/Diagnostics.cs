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

    public static async Task<AnalysisResult> TypeScriptAsync(string root, CancellationToken cancellation)
    {
        var tsc = Processes.Find("tsc") ?? Processes.Find("tsc.cmd") ?? Processes.Find("tsc.exe");
        if (tsc == null) throw new FileNotFoundException("tsc was not found on PATH. Install typescript for TypeScript CLI snapshots.");
        var projects = TypeScriptProjectArgs(root);
        if (projects.Count == 0)
            return new([], "No TypeScript/JavaScript sources found for CLI snapshot.", false);

        var problems = new List<Problem>();
        var log = new System.Text.StringBuilder();
        var failed = false;
        foreach (var args in projects)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await Processes.RunAsync(tsc, args, root, cancellation, 8 * 1024 * 1024);
            log.AppendLine(result.Display);
            var batch = ParseTypeScript(result.Output + "\n" + result.Error, root);
            problems.AddRange(batch);
            if (result.ExitCode != 0 && batch.Count == 0) failed = true;
            failed |= result.Truncated;
        }
        return new(problems.Distinct().ToList(), log.ToString(), failed && problems.Count == 0);
    }

    /// <summary>
    /// Build one or more tsc argument lists. Solution-style tsconfigs with project references are
    /// expanded so referenced projects are checked with --noEmit. Package-only repos pass explicit sources.
    /// </summary>
    public static List<string[]> TypeScriptProjectArgs(string root)
    {
        var tsconfig = Path.Combine(root, "tsconfig.json");
        if (File.Exists(tsconfig))
        {
            var references = ReadTsconfigReferences(tsconfig, root);
            if (references.Count > 0)
            {
                var projects = new List<string[]>();
                if (!TsconfigHasEmptyFilesOnly(tsconfig))
                    projects.Add(["--noEmit", "-p", tsconfig, "--pretty", "false"]);
                foreach (var reference in references)
                    projects.Add(["--noEmit", "-p", reference, "--pretty", "false"]);
                if (projects.Count > 0) return projects;
            }
            return [["--noEmit", "-p", tsconfig, "--pretty", "false"]];
        }

        var jsconfig = Path.Combine(root, "jsconfig.json");
        if (File.Exists(jsconfig))
            return [["--noEmit", "-p", jsconfig, "--pretty", "false"]];

        var sources = EnumerateTypeScriptSources(root).Take(100).ToArray();
        if (sources.Length == 0) return [];
        var args = new List<string> { "--noEmit", "--pretty", "false", "--allowJs", "true", "--checkJs", "false" };
        args.AddRange(sources);
        return [args.ToArray()];
    }

    private static List<string> ReadTsconfigReferences(string tsconfigPath, string root)
    {
        var result = new List<string>();
        if (!TryParseJsoncObject(File.ReadAllText(tsconfigPath), out var rootElement)) return result;
        if (!rootElement.TryGetProperty("references", out var references) || references.ValueKind != JsonValueKind.Array)
            return result;
        var baseDir = Path.GetDirectoryName(tsconfigPath) ?? root;
        foreach (var item in references.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("path", out var pathNode)) continue;
            var relative = pathNode.GetString();
            if (string.IsNullOrWhiteSpace(relative)) continue;
            var resolved = Path.GetFullPath(relative, baseDir);
            if (Directory.Exists(resolved))
            {
                var nested = Path.Combine(resolved, "tsconfig.json");
                result.Add(File.Exists(nested) ? nested : resolved);
            }
            else
                result.Add(resolved);
        }
        return result;
    }

    private static bool TsconfigHasEmptyFilesOnly(string tsconfigPath)
    {
        if (!TryParseJsoncObject(File.ReadAllText(tsconfigPath), out var rootElement)) return false;
        if (!rootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
            return false;
        return files.GetArrayLength() == 0 &&
               (!rootElement.TryGetProperty("include", out var include) ||
                include.ValueKind != JsonValueKind.Array ||
                include.GetArrayLength() == 0);
    }

    private static bool TryParseJsoncObject(string text, out JsonElement rootElement)
    {
        rootElement = default;
        try
        {
            using var document = JsonDocument.Parse(StripJsonc(text));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            rootElement = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string StripJsonc(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                builder.Append(c);
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; builder.Append(c); continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
                if (i < text.Length) builder.Append(text[i]);
                continue;
            }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/')) i++;
                i++;
                continue;
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static IEnumerable<string> EnumerateTypeScriptSources(string root)
    {
        IEnumerable<string> SafeEnumerate(string directory)
        {
            IEnumerable<string> files = [];
            IEnumerable<string> dirs = [];
            try
            {
                files = Directory.EnumerateFiles(directory)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".ts" or ".tsx" or ".js" or ".jsx" or ".mts" or ".cts" or ".mjs" or ".cjs");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { dirs = Directory.EnumerateDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var file in files) yield return file;
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("dist", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("vendor", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var nested in SafeEnumerate(dir)) yield return nested;
            }
        }
        return SafeEnumerate(root);
    }

    public static async Task<AnalysisResult> RustAsync(string root, CancellationToken cancellation)
    {
        var cargo = Processes.Find("cargo") ?? Processes.Find("cargo.exe");
        if (cargo == null) throw new FileNotFoundException("cargo was not found on PATH. Install Rust for cargo check snapshots.");
        var result = await Processes.RunAsync(cargo, ["check", "--message-format=json", "--quiet"], root, cancellation, 8 * 1024 * 1024);
        var problems = ParseCargo(result.Output + "\n" + result.Error, root);
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    public static async Task<AnalysisResult> CSharpAsync(string root, CancellationToken cancellation, string? projectOrSolution = null)
    {
        var dotnet = Processes.Find("dotnet") ?? Processes.Find("dotnet.exe");
        if (dotnet == null) throw new FileNotFoundException("dotnet was not found on PATH.");
        var target = projectOrSolution ?? WorkspaceProfile.FindDotNetTarget(root)
            ?? throw new FileNotFoundException("No .sln or .csproj was found for the C# CLI snapshot.");
        // Restore on the snapshot so fresh checkouts are not stuck on NETSDK1004 assets-file errors.
        var result = await Processes.RunAsync(dotnet, ["build", target, "--nologo", "-v:q"], root, cancellation, 8 * 1024 * 1024);
        var problems = ParseDotNet(result.Output + "\n" + result.Error, root);
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    public static async Task<AnalysisResult> PhpAsync(string root, CancellationToken cancellation)
    {
        var phpstan = Processes.Find("phpstan") ?? Processes.Find("phpstan.bat") ?? Processes.Find("phpstan.exe");
        if (phpstan != null)
        {
            var result = await Processes.RunAsync(phpstan, ["analyse", "--no-progress", "--error-format=json"], root, cancellation, 8 * 1024 * 1024);
            var stanProblems = ParsePhpStan(result.Output + "\n" + result.Error, root);
            var unexpected = result.ExitCode != 0 && stanProblems.Count == 0;
            return new(stanProblems, result.Display, unexpected || result.Truncated);
        }

        var php = Processes.Find("php") ?? Processes.Find("php.exe");
        if (php == null) throw new FileNotFoundException("Neither phpstan nor php was found on PATH for PHP CLI snapshots.");
        var files = EnumeratePhpFiles(root).Take(80).ToArray();
        if (files.Length == 0) return new([], "No PHP files found for php -l.", false);
        var lintProblems = new List<Problem>();
        var log = new System.Text.StringBuilder();
        var failed = false;
        foreach (var file in files)
        {
            cancellation.ThrowIfCancellationRequested();
            var result = await Processes.RunAsync(php, ["-l", file], root, cancellation, 1024 * 1024);
            log.AppendLine(result.Display);
            var batch = ParsePhpLint(result.Output + "\n" + result.Error, root);
            lintProblems.AddRange(batch);
            // Per-invocation: a later unrecognized failure must not be masked by earlier parse errors.
            if (result.ExitCode != 0 && batch.Count == 0) failed = true;
            failed |= result.Truncated;
        }
        return new(lintProblems, log.ToString(), failed);
    }

    public static async Task<AnalysisResult> CppAsync(string root, CancellationToken cancellation)
    {
        var tidy = Processes.Find("clang-tidy") ?? Processes.Find("clang-tidy.exe");
        if (tidy == null) throw new FileNotFoundException("clang-tidy was not found on PATH for C/C++ CLI snapshots.");
        var databaseDir = FindCompileCommandsDirectory(root)
            ?? throw new FileNotFoundException("compile_commands.json was not found (root or build/). Export it for clang-tidy snapshots.");
        var files = ReadCompileCommandFiles(Path.Combine(databaseDir, "compile_commands.json"), root).Take(40).ToArray();
        if (files.Length == 0) return new([], "compile_commands.json had no source files.", true);
        var args = new List<string> { "-p", databaseDir, "-quiet" };
        args.AddRange(files);
        var result = await Processes.RunAsync(tidy, args, root, cancellation, 8 * 1024 * 1024);
        var problems = ParseClangTidy(result.Output + "\n" + result.Error, root);
        var unexpected = result.ExitCode != 0 && problems.Count == 0;
        return new(problems, result.Display, unexpected || result.Truncated);
    }

    private static string? FindCompileCommandsDirectory(string root)
    {
        if (File.Exists(Path.Combine(root, "compile_commands.json"))) return root;
        var build = Path.Combine(root, "build");
        if (File.Exists(Path.Combine(build, "compile_commands.json"))) return build;
        return null;
    }

    /// <summary>
    /// Resolve source files from compile_commands.json. Relative <c>file</c> paths are resolved
    /// against each entry's <c>directory</c> (falling back to the repo root).
    /// </summary>
    public static IReadOnlyList<string> ReadCompileCommandFiles(string compileCommandsPath, string root)
    {
        var result = new List<string>();
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(compileCommandsPath)); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return result; }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("file", out var fileNode)) continue;
                var file = fileNode.GetString();
                if (string.IsNullOrWhiteSpace(file)) continue;
                var directory = root;
                if (item.TryGetProperty("directory", out var dirNode))
                {
                    var dir = dirNode.GetString();
                    if (!string.IsNullOrWhiteSpace(dir))
                        directory = Path.IsPathRooted(dir) ? Path.GetFullPath(dir) : Path.GetFullPath(dir, root);
                }
                var full = Path.IsPathRooted(file) ? Path.GetFullPath(file) : Path.GetFullPath(file, directory);
                if (!seen.Add(full)) continue;
                var extension = Path.GetExtension(full).ToLowerInvariant();
                if (extension is ".c" or ".cc" or ".cpp" or ".cxx")
                    result.Add(full);
            }
        }
        return result;
    }

    private static IEnumerable<string> EnumeratePhpFiles(string root)
    {
        IEnumerable<string> SafeEnumerate(string directory)
        {
            IEnumerable<string> files = [];
            IEnumerable<string> dirs = [];
            try { files = Directory.EnumerateFiles(directory, "*.php"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { dirs = Directory.EnumerateDirectories(directory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            foreach (var file in files) yield return file;
            foreach (var dir in dirs)
            {
                var name = Path.GetFileName(dir);
                if (name.Equals("vendor", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var nested in SafeEnumerate(dir)) yield return nested;
            }
        }
        return SafeEnumerate(root);
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

    [GeneratedRegex(@"^(.+?)\((\d+),(\d+)\):\s*(error|warning)\s+(TS\d+):\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeScriptPattern();
    public static List<Problem> ParseTypeScript(string text, string root)
    {
        var result = new List<Problem>();
        foreach (var line in text.Split('\n'))
        {
            var match = TypeScriptPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            result.Add(new("TypeScript", match.Groups[4].Value.ToLowerInvariant(),
                Path.GetFullPath(match.Groups[1].Value, root),
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
                match.Groups[6].Value, match.Groups[5].Value));
        }
        return result;
    }

    public static List<Problem> ParseCargo(string text, string root)
    {
        var result = new List<Problem>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(trimmed) || !trimmed.TrimStart().StartsWith('{')) continue;
            try
            {
                using var document = JsonDocument.Parse(trimmed);
                var rootElement = document.RootElement;
                if (!rootElement.TryGetProperty("reason", out var reason) ||
                    reason.GetString() != "compiler-message")
                    continue;
                if (!rootElement.TryGetProperty("message", out var message)) continue;
                var level = message.TryGetProperty("level", out var levelNode) ? levelNode.GetString() ?? "warning" : "warning";
                if (level is "help" or "note") continue;
                var severity = level.Equals("error", StringComparison.OrdinalIgnoreCase) ? "error" : "warning";
                var textMessage = message.TryGetProperty("message", out var msgNode) ? msgNode.GetString() ?? "" : "";
                var code = "";
                if (message.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.Object &&
                    codeNode.TryGetProperty("code", out var codeValue))
                    code = codeValue.GetString() ?? "";
                var spans = message.TryGetProperty("spans", out var spansNode) && spansNode.ValueKind == JsonValueKind.Array
                    ? spansNode
                    : default;
                string? file = null;
                var lineNumber = 1;
                var column = 1;
                if (spans.ValueKind == JsonValueKind.Array)
                {
                    foreach (var span in spans.EnumerateArray())
                    {
                        var primary = span.TryGetProperty("is_primary", out var primaryNode) && primaryNode.GetBoolean();
                        if (!primary && file != null) continue;
                        file = span.TryGetProperty("file_name", out var fileNode) ? fileNode.GetString() : file;
                        if (span.TryGetProperty("line_start", out var lineNode)) lineNumber = lineNode.GetInt32();
                        if (span.TryGetProperty("column_start", out var colNode)) column = colNode.GetInt32();
                        if (primary) break;
                    }
                }
                if (string.IsNullOrWhiteSpace(file)) continue;
                result.Add(new("Rust", severity, Path.GetFullPath(file, root), lineNumber, column, textMessage, code));
            }
            catch (JsonException) { }
        }
        return result;
    }

    [GeneratedRegex(@"^(.+?)\((\d+),(\d+)\):\s*(error|warning)\s+(\w+):\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetPattern();
    public static List<Problem> ParseDotNet(string text, string root)
    {
        var result = new List<Problem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var match = DotNetPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var code = match.Groups[5].Value;
            var file = match.Groups[1].Value;
            // Skip MSBuild / SDK infrastructure noise without source interest.
            if ((code.StartsWith("MSB", StringComparison.OrdinalIgnoreCase) ||
                 code.StartsWith("NETSDK", StringComparison.OrdinalIgnoreCase)) &&
                !file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            var problem = new Problem("C#", match.Groups[4].Value.ToLowerInvariant(),
                Path.GetFullPath(file, root),
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
                match.Groups[6].Value, code);
            var key = $"{problem.File}|{problem.Line}|{problem.Column}|{problem.Code}|{problem.Message}";
            if (!seen.Add(key)) continue;
            result.Add(problem);
        }
        return result;
    }

    public static List<Problem> ParsePhpStan(string json, string root)
    {
        var result = new List<Problem>();
        var start = json.IndexOf('{');
        var end = json.LastIndexOf('}');
        if (start < 0 || end <= start) return result;
        try
        {
            using var document = JsonDocument.Parse(json[start..(end + 1)]);
            if (!document.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
                return result;
            foreach (var file in files.EnumerateObject())
            {
                if (!file.Value.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var message in messages.EnumerateArray())
                {
                    var text = message.TryGetProperty("message", out var msg) ? msg.GetString() ?? "" : "";
                    var line = message.TryGetProperty("line", out var lineNode) && lineNode.ValueKind == JsonValueKind.Number
                        ? lineNode.GetInt32()
                        : 1;
                    var identifier = message.TryGetProperty("identifier", out var idNode) ? idNode.GetString() ?? "" : "";
                    result.Add(new("PHP", "error", Path.GetFullPath(file.Name, root), Math.Max(1, line), 1, text, identifier));
                }
            }
        }
        catch (JsonException) { }
        return result;
    }

    [GeneratedRegex(@"in (.+) on line (\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PhpLintPattern();
    public static List<Problem> ParsePhpLint(string text, string root)
    {
        var result = new List<Problem>();
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Contains("No syntax errors detected", StringComparison.OrdinalIgnoreCase)) continue;
            var match = PhpLintPattern().Match(trimmed);
            if (!match.Success) continue;
            var message = trimmed;
            var inIndex = trimmed.IndexOf(" in ", StringComparison.OrdinalIgnoreCase);
            if (inIndex > 0) message = trimmed[..inIndex].Trim();
            result.Add(new("PHP", "error", Path.GetFullPath(match.Groups[1].Value, root),
                int.Parse(match.Groups[2].Value), 1, message, "syntax"));
        }
        return result;
    }

    [GeneratedRegex(@"^(.+?):(\d+):(\d+):\s*(error|warning|note):\s*(.*?)(?:\s*\[([^\]]+)\])?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClangTidyPattern();
    public static List<Problem> ParseClangTidy(string text, string root)
    {
        var result = new List<Problem>();
        foreach (var line in text.Split('\n'))
        {
            var match = ClangTidyPattern().Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            var severity = match.Groups[4].Value.ToLowerInvariant() switch
            {
                "error" => "error",
                "warning" => "warning",
                _ => "info"
            };
            result.Add(new("C/C++", severity, Path.GetFullPath(match.Groups[1].Value, root),
                int.Parse(match.Groups[2].Value), int.Parse(match.Groups[3].Value),
                match.Groups[5].Value.Trim(), match.Groups[6].Value));
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
