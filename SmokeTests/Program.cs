using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using Microsoft.Web.WebView2.Wpf;
using System.Text.Json.Nodes;
using GitWorkspace;
using GitWorkspace.Core;
using GitWorkspace.Lsp;
using GitWorkspace.Terminal;

internal static class Program
{
    private static int exitCode;
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new App { UseProvidedWindow = true }; app.InitializeComponent();
        // Tests supply their own window; avoid App.OnStartup's production checkout discovery.
        var root = Path.Combine(Path.GetTempPath(), "gitworkspace-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var runner = new Window { Width = 1, Height = 1, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        runner.Loaded += async (_, _) =>
        {
            try
            {
                if (args is ["--analyzers", var repository])
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    var profile = WorkspaceProfile.Detect(repository);
                    Check(profile.HasAnyLanguage, "Analyzer target has no supported language project markers");
                    if (profile.HasDart)
                    {
                        var result = await Diagnostics.FlutterAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer Flutter: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "Flutter analyzer invocation failed");
                    }
                    if (profile.HasGo)
                    {
                        var result = await Diagnostics.GoAsync(profile.GoModuleDirectory!, timeout.Token);
                        Console.WriteLine($"Analyzer Go: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "Go analyzer invocation failed");
                    }
                    if (profile.HasPython)
                    {
                        var result = await Diagnostics.PythonAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer Python: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "Python analyzer invocation failed");
                    }
                    if (profile.HasTypeScript)
                    {
                        var result = await Diagnostics.TypeScriptAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer TypeScript: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "TypeScript analyzer invocation failed");
                    }
                    if (profile.HasRust)
                    {
                        var result = await Diagnostics.RustAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer Rust: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "Rust analyzer invocation failed");
                    }
                    if (profile.HasCSharp)
                    {
                        var result = await Diagnostics.CSharpAsync(repository, timeout.Token, profile.DotNetProjectPath);
                        Console.WriteLine($"Analyzer C#: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "C# analyzer invocation failed");
                    }
                    if (profile.HasPhp)
                    {
                        var result = await Diagnostics.PhpAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer PHP: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "PHP analyzer invocation failed");
                    }
                    if (profile.HasCpp)
                    {
                        var result = await Diagnostics.CppAsync(repository, timeout.Token);
                        Console.WriteLine($"Analyzer C/C++: {result.Problems.Count} problems; failed={result.Failed}\n{result.Log[..Math.Min(1000, result.Log.Length)]}");
                        Check(!result.Failed, "C/C++ analyzer invocation failed");
                    }
                }
                else if (args is ["--git-locks"])
                {
                    await GitLockChecks(Path.Combine(root, "lock-checks"));
                }
                else
                {
                    await CoreChecks(root);
                    await GitLockChecks(Path.Combine(root, "ignored", "lock-checks"));
                    await TerminalChecks(root);
                    await UiChecks(root);
                    await SearchRaceChecks(root);
                    await WebTerminalChecks(root);
                }
                Console.WriteLine("Workspace smoke checks passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exitCode = 1; }
            finally
            {
                // Absolute disposable target was created above; it never aliases the checkout.
                try
                {
                    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                app.Shutdown();
            }
        };
        // A plain Application shares the resource dictionary but does not discover a real repo.
        app.Startup += (_, _) => { };
        app.Run(runner);
        return exitCode;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task<CommandResult> Git(GitService git, params string[] arguments)
    {
        var result = await git.RunAsync(arguments); result.RequireSuccess(); return result;
    }
    private static async Task CoreChecks(string root)
    {
        var git = new GitService(root);
        await Git(git, "init", "-b", "main");
        await Git(git, "config", "user.name", "Workspace Test"); await Git(git, "config", "user.email", "workspace@example.invalid");
        await Git(git, "config", "commit.gpgsign", "false"); await Git(git, "config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "original\n");
        await Git(git, "add", "--all");
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "modified before first commit\n");
        (await git.UnstageAsync(await git.StatusAsync())).RequireSuccess();
        Check((await git.StatusAsync()).Single().Untracked, "Initial-commit unstage failed for a subsequently modified file");
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "original\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "ignored/\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "initial");
        foreach (var name in new[] { "[abc].txt", "space ; & ü.txt", "--dash.txt" }) await File.WriteAllTextAsync(Path.Combine(root, name), "new\n");
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "modified\n");
        var files = await git.StatusAsync();
        Check(files.Count == 4 && files.All(f => f.Unstaged), "Status lost unusual filenames");
        Check(GitService.ParseNumstat("2\t1\toops.txt\0-\t-\tbin.dat\03\t0\t\0old.txt\0new.txt\0")["new.txt"]
            is { Added: 3, Deleted: 0 }, "Numstat -z rename/binary parsing failed");
        Check(GitService.ParseNumstat("1\t0\tspace ; & ü.txt\0").ContainsKey("space ; & ü.txt"),
            "Numstat -z lost an unusual unquoted path");
        Check(GitService.SumLineCounts([
            new GitFile("a.txt", null, 'M', ' ', 2, 1),
            new GitFile("b.txt", null, 'A', ' ', 5, null),
            new GitFile("c.txt", null, ' ', 'M', null, 3),
        ]) is { Added: 7, Deleted: 4 }, "SumLineCounts did not add nullable line counts");
        var (_, unstagedCounts) = await git.LineCountsAsync(files);
        Check(unstagedCounts.TryGetValue("a.txt", out var aCount) && aCount.Added > 0,
            "Unstaged line counts missed modified a.txt");
        Check(unstagedCounts.TryGetValue("[abc].txt", out var bracketCount) && bracketCount.Added == 1 && bracketCount.Deleted == 0,
            "Untracked line counts missed bracket path");
        Check(unstagedCounts.ContainsKey("space ; & ü.txt"),
            "Unstaged line counts missed unusual filename that Git would quote without -z");
        (await git.StageAsync(files.Where(f => f.Path == "[abc].txt"))).RequireSuccess();
        Check((await git.StatusAsync()).Single(f => f.Path == "[abc].txt").Staged, "Literal bracket path was not staged");
        Check(!(await git.StatusAsync()).Single(f => f.Path == "a.txt").Staged, "Staging a literal path staged a different file");
        (await git.UnstageAsync((await git.StatusAsync()).Where(f => f.Staged))).RequireSuccess();
        await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "discard me\n");
        (await git.DiscardAsync((await git.StatusAsync()).Where(f => f.Path == "a.txt"))).RequireSuccess();
        Check((await File.ReadAllTextAsync(Path.Combine(root, "a.txt"))).Replace("\r\n", "\n") == "original\n",
            "Discard did not restore tracked worktree content");
        await File.WriteAllTextAsync(Path.Combine(root, "untracked-discard.txt"), "gone\n");
        (await git.DiscardAsync((await git.StatusAsync()).Where(f => f.Path == "untracked-discard.txt"))).RequireSuccess();
        Check(!File.Exists(Path.Combine(root, "untracked-discard.txt")),
            "Discard did not delete an untracked file");
        var outside = Path.Combine(Path.GetTempPath(), "gitworkspace-discard-target-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(outside);
        try
        {
            var outsideMarker = Path.Combine(outside, "keep-me.txt");
            await File.WriteAllTextAsync(outsideMarker, "safe\n");
            var link = Path.Combine(root, "outside-link");
            var mklink = await Processes.RunAsync("cmd.exe", ["/c", "mklink", "/J", link, outside], root);
            Check(mklink.ExitCode == 0 && Directory.Exists(link),
                "Could not create a directory junction for Discard safety coverage: " + mklink.Display);
            var throughLink = await git.DiscardAsync([new GitFile("outside-link/keep-me.txt", null, '?', '?')]);
            Check(throughLink.ExitCode != 0 && File.Exists(outsideMarker),
                "Discard allowed deleting through a directory link into an outside target");
            (await git.DiscardAsync([new GitFile("outside-link", null, '?', '?')])).RequireSuccess();
            Check(!Directory.Exists(link), "Discard left the untracked directory link in the repository");
            Check(File.Exists(outsideMarker) && (await File.ReadAllTextAsync(outsideMarker)).Replace("\r\n", "\n") == "safe\n",
                "Discard followed a directory link and deleted outside the repository");
        }
        finally
        {
            var leftover = Path.Combine(root, "outside-link");
            if (Directory.Exists(leftover) && (File.GetAttributes(leftover) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(leftover);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
        await Git(git, "mv", "a.txt", "renamed.txt");
        var rename = (await git.StatusAsync()).Single(f => f.OriginalPath != null);
        Check(rename.Path == "renamed.txt" && rename.OriginalPath == "a.txt", "Rename parsing failed");
        (await git.UnstageAsync([rename])).RequireSuccess();
        Check(File.Exists(Path.Combine(root, "renamed.txt")), "Unstage changed working files");
        await File.WriteAllTextAsync(Path.Combine(root, "renamed.txt"), "original\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "second");
        await Git(git, "checkout", "-b", "side");
        await File.WriteAllTextAsync(Path.Combine(root, "side.txt"), "side\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "side");
        await Git(git, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(root, "main.txt"), "main\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "main");
        await Git(git, "merge", "--no-ff", "side", "-m", "merge");
        var history = await git.HistoryAsync(150);
        Check(history[0].Parents.Length == 2 && history.Count == 5, "History lost merge parents or records");
        Check(history.All(c => c.Graph != null && c.Graph.Below.All(edge => edge.To >= 0)), "Invalid graph lanes");
        Check((await git.CompareAsync(history[^1].Hash, history[0].Hash, null)).RequireSuccess().Contains("side"), "Compare missed branch changes");
        Check((await git.HistoryAsync(150, "renamed.txt")).Count >= 2, "File history did not follow rename");
        var mergedFiles = await git.ChangedFilesAsync(history[0]);
        Check(mergedFiles.Any(f => f.Path == "side.txt" && f.State == "A"), "Merge first-parent file list missed addition");
        var addedDiff = await git.RevisionDiffAsync(mergedFiles.Single(f => f.Path == "side.txt"));
        Check(addedDiff.Old.Text == "" && addedDiff.New.Text == "side\n", "Added revision snapshots were incorrect");
        var rootFiles = await git.ChangedFilesAsync(history[^1]);
        Check(rootFiles.Any(f => f.Path == "a.txt"), "Root commit file list was empty");
        var renameCommit = history.Single(c => c.Subject == "second");
        var renamedFile = (await git.ChangedFilesAsync(renameCommit)).Single(f => f.Path == "renamed.txt");
        Check(renamedFile.OldPath == "a.txt" && renamedFile.State.StartsWith('R'), "History rename lost old path");
        var renameDiff = await git.RevisionDiffAsync(renamedFile);
        Check(renameDiff.Old.Text == renameDiff.New.Text, "Rename diff read incorrect revisions");
        var alignment = DiffAlignment.Align("one\ntwo\nthree\n", "one\nnew\nextra\nthree\n", "@@ -2 +2,2 @@\n-two\n+new\n+extra\n");
        Check(alignment.Count == 4 && alignment[2].OldNumber == null && alignment[2].NewNumber == 3 && alignment[3].OldNumber == 3, "Diff alignment lost gaps or original line numbers");
        var deletion = DiffAlignment.Align("one\nremoved\nlast\n", "one\nlast\n", "@@ -2 +1,0 @@");
        Check(deletion[1].NewNumber == null && deletion[2].NewNumber == 2, "Deletion alignment shifted remaining lines");
        Check((await git.RunAsync(["push"])).ExitCode != 0, "Missing remote did not fail");

        Directory.CreateDirectory(Path.Combine(root, "ignored"));
        var large = Path.Combine(root, "ignored", "large");
        await File.WriteAllBytesAsync(large, new byte[Files.PreviewLimit + 1]);
        Check(!(await Files.ReadAsync(large)).IsText, "Large preview was not bounded");
        await File.WriteAllBytesAsync(large, [0, 1, 2]); Check(!(await Files.ReadAsync(large)).IsText, "Binary preview was not identified");
        await File.WriteAllTextAsync(large, "UTF-16 örnek", Encoding.Unicode); Check((await Files.ReadAsync(large)).Text == "UTF-16 örnek", "UTF-16 preview failed");
        var flutter = Diagnostics.ParseFlutter("[warning] Something (with parentheses) (lib/a.dart:12:3)\n[error] Invalid (D:\\repo\\a.dart:2:5)", root);
        Check(flutter.Count == 2 && flutter[0].Line == 12 && flutter[1].Column == 5, "Flutter report locations failed");
        var go = Diagnostics.ParseGo("# p\n{\"p\":{\"printf\":[{\"posn\":\"D:\\\\repo\\\\a.go:15:7\",\"message\":\"brace } in message\"}]}}\nvet: a.go:2:4: syntax error", root);
        Check(go.Count == 2 && go[0].Line == 15 && go[0].Column == 7 && go[1].Line == 2 && go[1].Column == 4, "Go diagnostic locations failed");
        Check(AppInfo.ProductName == "GitWorkspace" && AppPaths.RootDirectory.EndsWith("GitWorkspace", StringComparison.OrdinalIgnoreCase),
            "App branding / LocalAppData path mismatch");
        Check(App.TryGetRepositoryArgument(["--repository", "\"" + root + "\""], out var quotedRepo)
            && string.Equals(quotedRepo, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "Quoted --repository path was not normalized");
        Check(App.TryGetRepositoryArgument(["--other", "--repository", root], out var midRepo)
            && string.Equals(midRepo, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase),
            "--repository was not accepted unless first argument");
        var emptyProfile = WorkspaceProfile.Detect(root);
        Check(!emptyProfile.HasAnyLanguage,
            "Empty git repo should not enable language profiles");
        await File.WriteAllTextAsync(Path.Combine(root, "pubspec.yaml"), "name: sample\n");
        Directory.CreateDirectory(Path.Combine(root, "go"));
        await File.WriteAllTextAsync(Path.Combine(root, "go", "go.mod"), "module example.com/demo\n\ngo 1.22\n");
        await File.WriteAllTextAsync(Path.Combine(root, "pyproject.toml"), "[project]\nname = \"sample\"\n");
        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), "{\"name\":\"sample\"}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"sample\"\nversion = \"0.1.0\"\nedition = \"2021\"\n");
        Directory.CreateDirectory(Path.Combine(root, "App"));
        await File.WriteAllTextAsync(Path.Combine(root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n");
        await File.WriteAllTextAsync(Path.Combine(root, "composer.json"), "{\"name\":\"sample/app\"}\n");
        await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(sample)\n");
        var fullProfile = WorkspaceProfile.Detect(root);
        Check(fullProfile.HasDart && fullProfile.HasGo && fullProfile.HasPython
            && fullProfile.HasTypeScript && fullProfile.HasRust && fullProfile.HasCSharp
            && fullProfile.HasPhp && fullProfile.HasCpp
            && string.Equals(fullProfile.GoModuleDirectory, Path.Combine(root, "go"), StringComparison.OrdinalIgnoreCase)
            && fullProfile.DotNetProjectPath != null
            && fullProfile.DotNetProjectPath.EndsWith("App.csproj", StringComparison.OrdinalIgnoreCase),
            "WorkspaceProfile missed one or more language project markers");
        Check(LspPaths.LanguageId(Path.Combine(root, "app.py")) == "python"
            && LspPaths.LanguageId(Path.Combine(root, "types.pyi")) == "python"
            && LspPaths.LanguageId(Path.Combine(root, "a.ts")) == "typescript"
            && LspPaths.LanguageId(Path.Combine(root, "a.tsx")) == "typescriptreact"
            && LspPaths.LanguageId(Path.Combine(root, "a.js")) == "javascript"
            && LspPaths.LanguageId(Path.Combine(root, "a.jsx")) == "javascriptreact"
            && LspPaths.LanguageId(Path.Combine(root, "a.rs")) == "rust"
            && LspPaths.LanguageId(Path.Combine(root, "a.cs")) == "csharp"
            && LspPaths.LanguageId(Path.Combine(root, "a.php")) == "php"
            && LspPaths.LanguageId(Path.Combine(root, "a.c")) == "c"
            && LspPaths.LanguageId(Path.Combine(root, "a.h")) == "c"
            && LspPaths.LanguageId(Path.Combine(root, "a.cpp")) == "cpp"
            && LspPaths.LanguageId(Path.Combine(root, "a.hpp")) == "cpp",
            "Language ids were not mapped for Python/TS/Rust/C#/PHP/C/C++");
        var ruffProblems = Diagnostics.ParseRuff(
            """[{"filename":"mod.py","code":"F401","message":"unused import","severity":"error","location":{"row":2,"column":1}}]""",
            root);
        Check(ruffProblems.Count == 1 && ruffProblems[0].Line == 2 && ruffProblems[0].Code == "F401",
            "Ruff JSON parsing failed");
        var tsProblems = Diagnostics.ParseTypeScript(
            "src/app.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.\nsrc/b.ts(2,1): warning TS6133: 'x' is declared but never used.",
            root);
        Check(tsProblems.Count == 2 && tsProblems[0].Code == "TS2322" && tsProblems[0].Line == 10 && tsProblems[1].Severity == "warning",
            "TypeScript diagnostic parsing failed");
        var cargoProblems = Diagnostics.ParseCargo(
            """{"reason":"compiler-message","message":{"level":"error","message":"cannot find value `x`","code":{"code":"E0425"},"spans":[{"file_name":"src/main.rs","is_primary":true,"line_start":4,"column_start":5}]}}""",
            root);
        Check(cargoProblems.Count == 1 && cargoProblems[0].Source == "Rust" && cargoProblems[0].Code == "E0425" && cargoProblems[0].Line == 4,
            "Cargo JSON diagnostic parsing failed");
        var csProblems = Diagnostics.ParseDotNet(
            @"App\Program.cs(12,17): error CS0103: The name 'x' does not exist in the current context
App\Program.cs(12,17): error CS0103: The name 'x' does not exist in the current context
C:\Program Files\dotnet\sdk\10.0.100\Sdks\Microsoft.NET.Sdk\targets\Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file not found",
            root);
        Check(csProblems.Count == 1 && csProblems[0].Source == "C#" && csProblems[0].Code == "CS0103" && csProblems[0].Column == 17,
            "dotnet build diagnostic parsing failed to dedupe or filter SDK noise");
        Check(Diagnostics.TypeScriptProjectArgs(root).Count == 0,
            "TypeScript args should be empty when package.json has no source files");
        await File.WriteAllTextAsync(Path.Combine(root, "broken.ts"), "const n: number = 'x';\n");
        var inferredArgs = Diagnostics.TypeScriptProjectArgs(root);
        Check(inferredArgs.Count == 1 && inferredArgs[0].Any(a => a.EndsWith("broken.ts", StringComparison.OrdinalIgnoreCase))
            && inferredArgs[0].All(a => a != "."),
            "package-only TypeScript snapshot should pass explicit sources, not '.'");
        File.Delete(Path.Combine(root, "broken.ts"));
        Directory.CreateDirectory(Path.Combine(root, "packages", "lib"));
        await File.WriteAllTextAsync(Path.Combine(root, "tsconfig.json"),
            """{"files":[],"references":[{"path":"./packages/lib"}]}""");
        await File.WriteAllTextAsync(Path.Combine(root, "packages", "lib", "tsconfig.json"),
            """{"compilerOptions":{"strict":true},"files":["index.ts"]}""");
        var solutionArgs = Diagnostics.TypeScriptProjectArgs(root);
        Check(solutionArgs.Count == 1 && solutionArgs[0].Any(a => a.Replace('\\', '/').Contains("packages/lib", StringComparison.OrdinalIgnoreCase)),
            "solution-style tsconfig should check referenced projects");
        Directory.Delete(Path.Combine(root, "packages"), true);
        File.Delete(Path.Combine(root, "tsconfig.json"));
        var phpStan = Diagnostics.ParsePhpStan(
            """{"files":{"src/App.php":{"messages":[{"message":"Undefined variable $x","line":8,"identifier":"variable.undefined"}]}}}""",
            root);
        Check(phpStan.Count == 1 && phpStan[0].Source == "PHP" && phpStan[0].Line == 8 && phpStan[0].Code == "variable.undefined",
            "PHPStan JSON diagnostic parsing failed");
        var phpLint = Diagnostics.ParsePhpLint(
            "Parse error: syntax error, unexpected token \"}\" in src/broken.php on line 3",
            root);
        Check(phpLint.Count == 1 && phpLint[0].Line == 3 && phpLint[0].Code == "syntax",
            "php -l diagnostic parsing failed");
        var tidy = Diagnostics.ParseClangTidy(
            "src/main.cpp:12:5: error: no matching function for call to 'foo' [clang-diagnostic-error]\nsrc/util.c:4:1: warning: unused variable 'n' [clang-analyzer-deadcode.DeadStores]",
            root);
        Check(tidy.Count == 2 && tidy[0].Source == "C/C++" && tidy[0].Line == 12 && tidy[1].Severity == "warning" && tidy[1].Code == "clang-analyzer-deadcode.DeadStores",
            "clang-tidy diagnostic parsing failed");
        var compileDb = Path.Combine(root, "compile_commands_test.json");
        await File.WriteAllTextAsync(compileDb,
            """
            [
              {"directory":"D:/build/out","file":"../src/main.cpp","command":"clang++ ../src/main.cpp"},
              {"directory":"D:/build/out","file":"D:/repo/src/util.c","command":"clang D:/repo/src/util.c"}
            ]
            """);
        var compileFiles = Diagnostics.ReadCompileCommandFiles(compileDb, root);
        Check(compileFiles.Count == 2
            && compileFiles[0].Replace('\\', '/').EndsWith("build/src/main.cpp", StringComparison.OrdinalIgnoreCase)
            && compileFiles[1].Replace('\\', '/').EndsWith("repo/src/util.c", StringComparison.OrdinalIgnoreCase),
            "compile_commands relative file paths must resolve against entry directory");
        File.Delete(compileDb);
        File.Delete(Path.Combine(root, "pubspec.yaml"));
        File.Delete(Path.Combine(root, "pyproject.toml"));
        File.Delete(Path.Combine(root, "package.json"));
        File.Delete(Path.Combine(root, "Cargo.toml"));
        File.Delete(Path.Combine(root, "composer.json"));
        File.Delete(Path.Combine(root, "CMakeLists.txt"));
        Directory.Delete(Path.Combine(root, "go"), true);
        Directory.Delete(Path.Combine(root, "App"), true);
        var language = new LanguageService(root, WorkspaceProfile.Detect(root));
        await language.StartAsync();
        Check(language.Status == "No language servers for this repo", "LanguageService should stay quiet without project markers");
        await language.DisposeAsync();
        await File.WriteAllTextAsync(Path.Combine(root, "requirements.txt"), "requests\n");
        Check(WorkspaceProfile.Detect(root).HasPython, "requirements.txt should enable the Python profile");
        File.Delete(Path.Combine(root, "requirements.txt"));
        var ruff = Diagnostics.ParseRuff(
            "[{\"filename\":\"app.py\",\"code\":\"E501\",\"message\":\"line too long\",\"severity\":1,\"location\":{\"row\":3,\"column\":88}}]",
            root);
        Check(ruff.Count == 1 && ruff[0].Source == "Python" && ruff[0].Line == 3 && ruff[0].Column == 88 && ruff[0].Code == "E501",
            "Ruff JSON diagnostic parsing failed");
        var dartMachine = Diagnostics.ParseDartMachine(
            "ERROR|COMPILE_TIME_ERROR|unused_import|lib/a.dart|12|1|10|Unused import\nWARNING|STATIC_WARNING|dead_code|D:\\repo\\b.dart|4|2|3|Dead code",
            root);
        Check(dartMachine.Count == 2 && dartMachine[0].Line == 12 && dartMachine[0].Code == "unused_import"
            && dartMachine[1].Severity == "warning" && dartMachine[1].Column == 2,
            "Dart machine-format diagnostic parsing failed");
        Check(App.TryGetRepositoryArgument(["--repository", "\"C:\\My Projects\\demo\""], out var spaced)
            && spaced.Contains("My Projects", StringComparison.OrdinalIgnoreCase),
            "Quoted repository paths with spaces were not normalized");
        Check(SearchService.TryParseHit("lib/a.dart:12:hello search token", out var parsedHit)
            && parsedHit.Path == "lib/a.dart" && parsedHit.Line == 12 && parsedHit.Preview.Contains("search token"),
            "Search hit parsing failed");
        Check(SearchService.EstimateColumn("  hello search token", "search", false, false) == 9, "Search column estimate failed");
        Check(SearchService.DecodeGitPath("\"de\\304\\237er.txt\"") == "değer.txt", "Git octal path decoding failed");
        Check(SearchService.TryParseHit("\"de\\304\\237er.txt\":3:token", out var unicodeHit)
            && unicodeHit.Path == "değer.txt" && unicodeHit.Line == 3,
            "Quoted Unicode search paths were not decoded");
        Check(SearchService.TryParseHit("\x1b[1;31mlib/a.dart\x1b[m:\x1b[1;32m4\x1b[m:match here", out var colorHit)
            && colorHit.Path == "lib/a.dart" && colorHit.Line == 4 && colorHit.Preview == "match here",
            "ANSI-colored grep lines were not parsed");
        var exactPage = string.Join('\n', Enumerable.Range(1, SearchService.MaxMatches)
            .Select(i => $"file.txt:{i}:hit"));
        var exactParse = SearchService.Parse(exactPage);
        Check(exactParse.MatchCount == SearchService.MaxMatches && !exactParse.Truncated,
            "An exact MaxMatches page was incorrectly marked truncated");
        var overPage = SearchService.Parse(exactPage + "\nfile.txt:2001:extra");
        Check(overPage.MatchCount == SearchService.MaxMatches && overPage.Truncated,
            "Overflow beyond MaxMatches did not set Truncated");
        var catastrophic = new string('a', 300);
        Check(SearchService.EstimateColumn(catastrophic, "(a+)+$", false, true) == 1,
            "Catastrophic regex estimate did not return safely");
        await File.WriteAllTextAsync(Path.Combine(root, "search-target.txt"), "alpha SEARCHMARK beta\nsecond line\n");
        await File.WriteAllTextAsync(Path.Combine(root, "değer.txt"), "unicode SEARCHMARK line\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "search seed");
        var search = new SearchService(root);
        var searchHits = await search.SearchAsync("SEARCHMARK", matchCase: true, wholeWord: false, useRegex: false, include: "*.txt", exclude: "", default);
        Check(searchHits.Error == null && searchHits.MatchCount >= 1
            && searchHits.Files.Any(file => file.Path == "search-target.txt" && file.Hits.Any(hit => hit.Line == 1)),
            "SearchService git grep did not find the seeded marker");
        Check(searchHits.Files.Any(file => file.Path == "değer.txt"),
            "SearchService lost Unicode file names from git grep");
        var emptySearch = await search.SearchAsync("", false, false, false, "", "", default);
        Check(emptySearch.MatchCount == 0 && emptySearch.Files.Count == 0, "Empty search query should clear results");
        var badRegex = await search.SearchAsync("(", false, false, true, "", "", default);
        Check(badRegex.Error != null && badRegex.Error.Contains("regular expression", StringComparison.OrdinalIgnoreCase),
            "Invalid regex did not surface a search error");
        LspChecks(root);
        Console.WriteLine("Git history, literal paths, previews, search and diagnostic parsing passed.");
    }

    private static async Task GitLockChecks(string root)
    {
        Directory.CreateDirectory(root);
        var git = new GitService(root);
        var messages = new List<string>();
        git.Log += messages.Add;
        await Git(git, "init", "-b", "main");
        await Git(git, "config", "user.name", "Workspace Test");
        await Git(git, "config", "user.email", "workspace@example.invalid");
        await Git(git, "config", "commit.gpgsign", "false");
        await Git(git, "config", "core.hooksPath", Path.Combine(root, "no-hooks"));
        var lockPath = Path.Combine(root, ".git", "index.lock");
        async Task WriteLock(string path, string content = "", bool old = true)
        {
            await File.WriteAllTextAsync(path, content);
            if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-2));
        }
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "a.txt"), "original\n");
            await WriteLock(lockPath);
            await Git(git, "add", "--all");
            Check(!File.Exists(lockPath) && (await git.StatusAsync()).Single().Staged, "Stage all did not recover a stale index lock");

            await WriteLock(lockPath, "partial index backup");
            (await git.UnstageAsync(await git.StatusAsync())).RequireSuccess();
            var backup = messages.Last(message => message.Contains("Backup: ", StringComparison.Ordinal)).Split("Backup: ")[1];
            Check(await File.ReadAllTextAsync(backup) == "partial index backup", "Recovery lost nonempty lock contents");
            Check((await git.StatusAsync()).Single().Untracked, "Unstage did not recover a stale index lock");

            await WriteLock(lockPath, old: false);
            Check((await git.RunAsync(["add", "--all"])).ExitCode != 0 && File.Exists(lockPath), "Recovery removed a recent lock");
            Check((await git.StatusAsync()).Single().Untracked, "Failed pre-check changed the index");
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            {
                try { await git.RunAsync(["add", "--all"], cancellation.Token); throw new Exception("Waiting pre-check ignored cancellation"); }
                catch (OperationCanceledException) { }
            }
            Check(File.Exists(lockPath), "Cancelled pre-check removed a recent lock");
            File.Delete(lockPath);

            await WriteLock(lockPath);
            using (var held = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check((await git.RunAsync(["add", "--all"])).ExitCode != 0, "Recovery ignored an open lock handle");
            Check(File.Exists(lockPath), "Recovery removed a lock owned by another handle");

            // Git can own a transaction lock without keeping its file handle open (for example during an editor).
            using (var active = new Process
            {
                StartInfo = new ProcessStartInfo(Processes.Find("git.exe")!)
                {
                    WorkingDirectory = root, Arguments = "hash-object --stdin", UseShellExecute = false,
                    CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                }
            })
            {
                active.Start();
                try
                {
                    Check((await git.RunAsync(["add", "--all"])).ExitCode != 0 && File.Exists(lockPath),
                        "Recovery removed a lock while a Git process was still active");
                }
                finally
                {
                    active.StandardInput.Close();
                    await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            (await git.StageAsync(await git.StatusAsync())).RequireSuccess();
            Check(!File.Exists(lockPath), "Stage selected did not recover after the active Git process exited");

            await WriteLock(Path.Combine(root, ".git", "HEAD.lock"));
            await WriteLock(Path.Combine(root, ".git", "refs", "heads", "main.lock"));
            await Git(git, "commit", "-m", "recovered locks");
            Check((await git.HistoryAsync(1)).Single().Subject == "recovered locks", "Commit did not recover HEAD/ref locks");

            await WriteLock(lockPath);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { await git.RunAsync(["add", "--all"], cancellation.Token); throw new Exception("Cancelled command ran"); }
                catch (OperationCanceledException) { }
            }
            Check(File.Exists(lockPath), "Cancelled command removed a lock");
            await Git(git, "add", "--all");

            var worktree = Path.Combine(root, "linked");
            await Git(git, "worktree", "add", "-b", "linked", worktree);
            var linkedGit = new GitService(worktree);
            linkedGit.Log += messages.Add;
            var linkedLock = (await Git(linkedGit, "rev-parse", "--path-format=absolute", "--git-path", "index.lock")).Output.Trim();
            await WriteLock(linkedLock);
            await File.WriteAllTextAsync(Path.Combine(worktree, "linked.txt"), "linked\n");
            await Git(linkedGit, "add", "--all");
            Check(!File.Exists(linkedLock) && (await linkedGit.StatusAsync()).Single().Staged, "Linked worktree lock recovery used the wrong Git directory");
            Console.WriteLine("Git stale/recent/open/active/cancelled/ref/worktree lock checks passed.");
        }
        finally
        {
            foreach (var message in messages.Where(message => message.Contains("Backup: ", StringComparison.Ordinal)))
                File.Delete(message.Split("Backup: ")[1]);
        }
    }

    private static void LspChecks(string root)
    {
        var body = Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"textDocument/publishDiagnostics","params":{"uri":"file:///D:/repo/a.dart","diagnostics":[]}}""");
        var framed = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();
        Check(JsonRpcConnection.TryConsume(framed, framed.Length, out var message, out var consumed)
            && message != null && consumed == framed.Length
            && message["method"]!.GetValue<string>() == "textDocument/publishDiagnostics",
            "LSP framing failed to parse a Content-Length message");
        var partial = framed.AsSpan(0, framed.Length - 5).ToArray();
        Check(!JsonRpcConnection.TryConsume(partial, partial.Length, out _, out _), "LSP framing accepted an incomplete frame");

        var store = new DiagnosticStore();
        var dartFile = Path.Combine(root, "lib", "sample.dart");
        Directory.CreateDirectory(Path.GetDirectoryName(dartFile)!);
        store.Replace("Dart", dartFile, [new Problem("Dart", "error", dartFile, 3, 1, "undefined name", "undefined_identifier")]);
        store.Replace("Go", Path.Combine(root, "go", "main.go"), [new Problem("Go", "warning", Path.Combine(root, "go", "main.go"), 1, 2, "unused", "unused")]);
        store.Replace("Dart", dartFile, [new Problem("Dart", "warning", dartFile, 4, 2, "dead code", "dead_code")]);
        var snapshot = store.Snapshot();
        Check(snapshot.Count == 2 && store.ForFile(dartFile).Single().Message == "dead code", "DiagnosticStore URI replace/merge failed");
        store.ClearSource("Go");
        Check(store.Snapshot().Count == 1 && store.Snapshot()[0].Source == "Dart", "DiagnosticStore ClearSource failed");

        var uri = LspPaths.PathToUri(dartFile);
        Check(uri.StartsWith("file:///", StringComparison.Ordinal) && LspPaths.UriToPath(uri) == Path.GetFullPath(dartFile), "LSP path URI round-trip failed");
        var encodedDrive = "file:///c%3A/Users/demo/app.ts";
        var encodedPath = LspPaths.UriToPath(encodedDrive);
        Check(encodedPath != null
            && encodedPath.StartsWith(@"c:\", StringComparison.OrdinalIgnoreCase)
            && !encodedPath.Contains(@"\c:\", StringComparison.OrdinalIgnoreCase)
            && encodedPath.EndsWith(@"Users\demo\app.ts", StringComparison.OrdinalIgnoreCase),
            "Encoded Windows drive letters in LSP URIs must not resolve against the current drive");
        var unicode = Path.Combine(root, "ışğü", "örnek.dart");
        var unicodeUri = LspPaths.PathToUri(unicode);
        Check(unicodeUri.Contains("%C4%B1", StringComparison.Ordinal) || unicodeUri.Contains("%c4%b1", StringComparison.OrdinalIgnoreCase),
            "LSP URI encoding must percent-encode UTF-8 bytes for Turkish characters");
        Check(LspPaths.UriToPath(unicodeUri) == Path.GetFullPath(unicode), "LSP Unicode path URI round-trip failed");
        Check(LspPaths.LanguageId(dartFile) == "dart" && LspPaths.IsWatched(Path.Combine(root, "pubspec.yaml")), "LSP watch filters failed");
        Check(LspPaths.IsIgnored(root, Path.Combine(root, "build", "a.dart")) && !LspPaths.IsIgnored(root, dartFile), "LSP ignore filters failed");
        _ = JsonNode.Parse("""{"ok":true}""");
        Console.WriteLine("LSP framing, DiagnosticStore and path helpers passed.");
    }

    private static async Task TerminalChecks(string root)
    {
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var cwdRoot = Path.Combine(root, "term-cwd");
        Directory.CreateDirectory(cwdRoot);
        var terminal = new PseudoTerminal(shell, cwdRoot);
        var pid = terminal.ProcessId;
        var text = new StringBuilder();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pump = Task.Run(async () => { await foreach (var chunk in terminal.Output.ReadAllAsync(timeout.Token)) lock (text) text.Append(chunk); });
        try
        {
            await terminal.WriteAsync("Write-Output ('WORKSPACE_' + (20+22)); Write-Output ('UTF_' + [char]0xFC); Write-Output ('CWD_' + (Get-Location).Path)\r");
            await Until(() =>
            {
                lock (text)
                {
                    var output = text.ToString();
                    return output.Contains("WORKSPACE_42") && output.Contains("UTF_ü")
                        && output.Contains("CWD_", StringComparison.Ordinal)
                        && output.Contains(cwdRoot, StringComparison.OrdinalIgnoreCase);
                }
            });
            terminal.Resize(75, 18);
            await terminal.WriteAsync("Write-Output ('SLEEP_' + 'START'); Start-Sleep -Seconds 30\r");
            await Until(() => { lock (text) return text.ToString().Contains("SLEEP_START"); });
            await terminal.WriteAsync("\x03"); await Task.Delay(300);
            await terminal.WriteAsync("Write-Output ('AFTER_' + 'INTERRUPT')\r");
            await Until(() => { lock (text) return text.ToString().Contains("AFTER_INTERRUPT"); });
        }
        finally { await terminal.DisposeAsync(); await pump.WaitAsync(TimeSpan.FromSeconds(5)); }
        var alive = true; try { using var process = Process.GetProcessById(pid); alive = !process.HasExited; } catch (ArgumentException) { alive = false; }
        Check(!alive, "PowerShell leaked after terminal disposal");
        Console.WriteLine("Real ConPTY input/output, Unicode, custom cwd, resize, Ctrl+C and cleanup passed.");
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    private static void Click(Window window, string text) => Children(window).OfType<Button>().Single(b => Equals(b.Content, text)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static async Task UiChecks(string root)
    {
        var git = new GitService(root);
        await File.WriteAllTextAsync(Path.Combine(root, "ui.txt"), "ui original\n");
        await Git(git, "add", "--all"); await Git(git, "commit", "-m", "UI baseline");
        await File.WriteAllTextAsync(Path.Combine(root, "ui.txt"), "ui modified\n");
        var delayNextStatusDiff = false;
        var statusDiffStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedStatusDiff = new TaskCompletionSource<RevisionDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        var window = new MainWindow(root, rememberLayout: false, (file, staged, cancellation) =>
        {
            if (delayNextStatusDiff)
            {
                delayNextStatusDiff = false;
                statusDiffStarted.TrySetResult(true);
                return delayedStatusDiff.Task;
            }
            return git.StatusDiffAsync(file, staged, cancellation);
        }, confirmAction: (_, _) => true);
        window.Show();
        var sidebar = (TabControl)window.FindName("Sidebar");
        var changes = (DataGrid)window.FindName("ChangesList"); var staged = (DataGrid)window.FindName("StagedList");
        var message = (TextBox)window.FindName("CommitMessage");
        var actions = (StackPanel)window.FindName("SourceActions");
        await Until(() => changes.Items.Count == 1
            && changes.Items.OfType<GitFile>().Single() is { Path: "ui.txt", Added: not null, Deleted: not null });
        var uiChange = changes.Items.OfType<GitFile>().Single();
        Check(uiChange.AddedText.StartsWith('+') && uiChange.DeletedText.StartsWith('-')
            && uiChange.Added > 0 && uiChange.Deleted > 0,
            "Changes list did not show +/- line counts for ui.txt");
        var changesAddedTotal = (TextBlock)window.FindName("ChangesAddedTotal");
        var changesDeletedTotal = (TextBlock)window.FindName("ChangesDeletedTotal");
        Check(changesAddedTotal.Text == uiChange.AddedText && changesAddedTotal.Visibility == Visibility.Visible
            && changesDeletedTotal.Text == uiChange.DeletedText && changesDeletedTotal.Visibility == Visibility.Visible,
            "Changes section totals did not match the single unstaged file line counts");
        var sourceControlLabel = (TextBlock)window.FindName("SourceControlLabel");
        var accent = window.FindResource("AccentBrush");
        var sourceControlTab = (TabItem)window.FindName("SourceControlTab");
        var sourceControlBadge = (Border)window.FindName("SourceControlBadge");
        var sourceControlBadgeText = (TextBlock)window.FindName("SourceControlBadgeText");
        void CheckBadge(string expectedText, string expectedDescription, Visibility expectedVisibility = Visibility.Visible)
        {
            Check(sourceControlBadge.Visibility == expectedVisibility && sourceControlBadgeText.Text == expectedText
                && Equals(sourceControlBadge.ToolTip, expectedDescription)
                && AutomationProperties.GetHelpText(sourceControlTab) == expectedDescription,
                $"Expected Source Control badge {expectedText} ({expectedDescription}, {expectedVisibility})");
        }
        CheckBadge("1", "1 changed file");
        Check(sidebar.SelectedIndex == 0 && Equals(sourceControlLabel.Foreground, accent) && sourceControlLabel.FontWeight == FontWeights.SemiBold,
            "Unstaged changes did not highlight the Source Control tab while Explorer was active");
        foreach (TabItem tab in sidebar.Items)
        {
            tab.ApplyTemplate();
            var tabFrame = tab.Template.FindName("Frame", tab) as Border
                ?? throw new InvalidOperationException("TabItem Frame template part is missing");
            Check(tabFrame.BorderThickness == new Thickness(1) && tabFrame.CornerRadius == new CornerRadius(3)
                && tabFrame.Margin == new Thickness(0),
                "Sidebar tab chrome is not a surrounding selection box");
            if (tab.IsSelected)
                Check(Equals(tabFrame.BorderBrush, window.FindResource("AccentBrush")),
                    "Selected sidebar tab is missing the accent selection box");
        }
        window.Width = 980;
        ((ColumnDefinition)window.FindName("SidebarColumn")).Width = new GridLength(240);
        window.UpdateLayout();
        IEnumerable<DependencyObject> TabVisuals(DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var nested in TabVisuals(child)) yield return nested;
            }
        }
        var headerPanel = TabVisuals(sidebar).OfType<TabPanel>().FirstOrDefault();
        Check(headerPanel != null && headerPanel.ActualHeight > 0 && headerPanel.ActualHeight < 48,
            "Narrow 240 DIP sidebar wrapped the three tabs onto a second row");
        var explorerTree = (TreeView)window.FindName("ExplorerTree");
        var menuFile = Path.Combine(root, "explorer-menu.txt");
        await File.WriteAllTextAsync(menuFile, "explorer menu\n");
        Click(window, "Refresh tree");
        TreeViewItem? FindNode(ItemsControl parent, string full)
        {
            foreach (var item in parent.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is string tag
                    && string.Equals(Path.GetFullPath(tag), Path.GetFullPath(full), StringComparison.OrdinalIgnoreCase))
                    return item;
                var nested = FindNode(item, full);
                if (nested != null) return nested;
            }
            return null;
        }
        await Until(() => FindNode(explorerTree, menuFile) != null);
        var explorerNode = FindNode(explorerTree, menuFile)!;
        explorerNode.IsSelected = true;
        await Until(() => tabsHaveExplorerMenuFile());
        bool tabsHaveExplorerMenuFile() =>
            ((TabControl)window.FindName("Documents")).Items.OfType<TabItem>()
                .Any(tab => tab.Content is DocumentView view
                    && view.RelativePath.Equals("explorer-menu.txt", StringComparison.OrdinalIgnoreCase));
        MenuItem ExplorerMenu(string header) =>
            explorerTree.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
        Check(explorerTree.ContextMenu != null
            && ExplorerMenu("Open With...") != null
            && ExplorerMenu("Reveal in File Explorer") != null
            && ExplorerMenu("Open in Integrated Terminal") != null
            && ExplorerMenu("Copy Path") != null
            && ExplorerMenu("Copy Relative Path") != null
            && ExplorerMenu("Rename...") != null
            && ExplorerMenu("Delete") != null
            && ExplorerMenu("Git: View File History") != null,
            "Explorer context menu is missing one or more planned actions");
        ExplorerMenu("Copy Path").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(string.Equals(System.Windows.Clipboard.GetText(), Path.GetFullPath(menuFile), StringComparison.OrdinalIgnoreCase),
            "Copy Path did not place the absolute explorer path on the clipboard");
        ExplorerMenu("Copy Relative Path").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(System.Windows.Clipboard.GetText().Replace('\\', '/') == "explorer-menu.txt",
            "Copy Relative Path did not place the repo-relative path on the clipboard");
        Click(window, "File history");
        var historyTitle = (TextBlock)window.FindName("HistoryTitle");
        await Until(() => historyTitle.Text.Contains("explorer-menu.txt", StringComparison.OrdinalIgnoreCase));
        sidebar.SelectedIndex = 0;
        var renamedFile = Path.Combine(root, "explorer-renamed.txt");
        var documentTabs = (TabControl)window.FindName("Documents");
        var sourceTab = documentTabs.Items.OfType<TabItem>().Single(tab =>
            tab.Content is DocumentView { RelativePath: "explorer-menu.txt" });
        await File.WriteAllTextAsync(renamedFile, "stale destination\n");
        Click(window, "Refresh tree");
        await Until(() => FindNode(explorerTree, renamedFile) != null);
        FindNode(explorerTree, renamedFile)!.IsSelected = true;
        var staleTab = documentTabs.Items.OfType<TabItem>().Single(tab =>
            tab.Content is DocumentView { RelativePath: "explorer-renamed.txt" });
        File.Delete(renamedFile);
        FindNode(explorerTree, menuFile)!.IsSelected = true;
        var completeRename = window.Dispatcher.InvokeAsync(() =>
        {
            var dialog = Application.Current.Windows.OfType<Window>().Single(w => w.Owner == window && w.Title == "Rename");
            Children(dialog).OfType<TextBox>().Single().Text = Path.GetFileName(renamedFile);
            Click(dialog, "OK");
        }, DispatcherPriority.Background);
        ExplorerMenu("Rename...").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await completeRename;
        await Until(() => changes.Items.OfType<GitFile>().Any(f => f.Path == "explorer-renamed.txt")
            && !changes.Items.OfType<GitFile>().Any(f => f.Path == "explorer-menu.txt"));
        Check(!documentTabs.Items.Contains(staleTab) && sourceTab.Content is DocumentView { RelativePath: "explorer-renamed.txt" },
            "Rename did not replace the stale destination preview with the source tab");
        await Until(() => FindNode(explorerTree, renamedFile) != null);
        FindNode(explorerTree, renamedFile)!.IsSelected = true;
        Check(ReferenceEquals(documentTabs.SelectedItem, sourceTab), "Reopening the renamed file did not reuse the retargeted tab");
        var closeUnder = typeof(MainWindow).GetMethod("CloseDocumentsUnder", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "CloseDocumentsUnder");
        closeUnder.Invoke(window, [renamedFile]);
        File.Delete(renamedFile);
        Click(window, "Refresh tree");
        await Until(() => FindNode(explorerTree, renamedFile) == null);
        Check(!((TabControl)window.FindName("Documents")).Items.OfType<TabItem>()
                .Any(tab => tab.Content is DocumentView view
                    && view.RelativePath.Equals("explorer-renamed.txt", StringComparison.OrdinalIgnoreCase)),
            "CloseDocumentsUnder left a deleted explorer file tab open");
        sidebar.SelectedIndex = 1;
        Click(window, "All history");
        await Until(() => ((TextBlock)window.FindName("HistoryTitle")).Text.StartsWith("Commit graph", StringComparison.Ordinal));
        Console.WriteLine("Explorer context menu actions, clipboard paths, file history, rename remap and delete cleanup passed.");
        sidebar.SelectedItem = (TabItem)window.FindName("SearchTab");
        window.UpdateLayout();
        await Until(() => ((TextBlock)window.FindName("SearchQueryLabel")).IsVisible);
        Check(sourceControlBadge.IsVisible, "Switching to Search hid the Source Control badge");
        await SearchUiChecks(window, root);
        await EditUiChecks(window, root);
        sidebar.SelectedIndex = 1;
        // Regress the screenshot's black labels and Windows light tab fallback.
        double Luminance(System.Windows.Media.Color color)
        {
            double Linear(byte channel) { var value = channel / 255.0; return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4); }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        void Readable(System.Windows.Media.Brush foreground, System.Windows.Media.Brush background, string label)
        {
            var fg = Luminance(((System.Windows.Media.SolidColorBrush)foreground).Color);
            var bg = Luminance(((System.Windows.Media.SolidColorBrush)background).Color);
            Check((Math.Max(fg, bg) + .05) / (Math.Min(fg, bg) + .05) >= 4.5, "Low contrast: " + label);
        }
        var dark = (System.Windows.Media.Brush)window.FindResource("BackgroundBrush");
        foreach (var name in new[] { "BranchText", "StagedTitle", "ChangesTitle", "HistoryTitle", "AnalysisStatus", "TerminalHint" })
            Readable(((TextBlock)window.FindName(name)).Foreground, dark, name);
        foreach (TabItem tab in sidebar.Items) { tab.ApplyTemplate(); Readable(tab.Foreground, tab.Background, "sidebar tab"); }
        var cancel = (Button)window.FindName("CancelAnalysisButton"); cancel.ApplyTemplate();
        var frame = (Border)cancel.Template.FindName("Frame", cancel);
        Readable(cancel.Foreground, frame.Background, "disabled cancel button");
        var bottom = (TabControl)window.FindName("BottomPanel");
        Check(bottom.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).Contains("Problems")
            && bottom.Items.Cast<TabItem>().Select(tab => tab.Header?.ToString()).Contains("Terminal"), "Problems and Terminal tabs are not directly available");
        Check(window.FindName("SidebarMenu") == null && window.FindName("PanelMenu") == null && window.FindName("StatusMenu") == null,
            "Removed View menu controls are still part of the window");
        var sourceChanges = (Grid)window.FindName("SourceChanges");
        var stagedChangesSplitter = (GridSplitter)window.FindName("StagedChangesSplitter");
        Check(stagedChangesSplitter.ResizeDirection == GridResizeDirection.Rows
            && stagedChangesSplitter.ResizeBehavior == GridResizeBehavior.PreviousAndNext,
            "Staged/Changes divider is not configured to resize both file lists");
        var historyBody = (Grid)window.FindName("HistoryBody");
        var historyVisibilityButton = (Button)window.FindName("HistoryVisibilityButton");
        historyVisibilityButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(historyBody.Visibility == Visibility.Collapsed && sourceChanges.Visibility == Visibility.Visible,
            "History visibility button did not collapse only the history list");
        historyVisibilityButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(historyBody.Visibility == Visibility.Visible, "History visibility button did not restore the history list");
        var historySizeButton = (Button)window.FindName("HistorySizeButton");
        historySizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(sourceChanges.Visibility == Visibility.Collapsed && historyBody.Visibility == Visibility.Visible,
            "Expand history area did not collapse the change lists while keeping history visible");
        historySizeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(sourceChanges.Visibility == Visibility.Visible, "Restore history did not bring the change lists back");
        var showTerminal = typeof(MainWindow).GetMethod("ShowTerminal", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "ShowTerminal");
        var setPanel = typeof(MainWindow).GetMethod("SetPanel", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "SetPanel");
        var ensureTerminal = typeof(MainWindow).GetMethod("EnsureTerminalAsync", BindingFlags.Instance | BindingFlags.NonPublic, binder: null, types: Type.EmptyTypes, modifiers: null)
            ?? throw new MissingMethodException(nameof(MainWindow), "EnsureTerminalAsync");
        var terminalField = typeof(MainWindow).GetField("terminal", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(MainWindow), "terminal");
        showTerminal.Invoke(window, [window, new RoutedEventArgs()]);
        await Until(() => terminalField.GetValue(window) is TerminalPanel ready && ready.SessionCount > 0);
        var terminalPanel = (TerminalPanel)terminalField.GetValue(window)!;
        var terminalBrowser = (WebView2)terminalPanel.Children[0];
        await UntilTerminalFocusedAsync(terminalBrowser);
        message.Focus(); Keyboard.Focus(message);
        Check(message.IsKeyboardFocusWithin, "Could not park keyboard focus on the commit message");
        showTerminal.Invoke(window, [window, new RoutedEventArgs()]);
        await UntilTerminalFocusedAsync(terminalBrowser);
        Check(terminalBrowser.IsKeyboardFocusWithin || terminalBrowser.IsFocused,
            "ShowTerminal while Terminal was already selected left focus on the commit field");
        message.Focus(); Keyboard.Focus(message);
        ((TabItem)window.FindName("TerminalTab")).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
        });
        await UntilTerminalFocusedAsync(terminalBrowser);
        bottom.SelectedItem = window.FindName("ProblemsTab");
        bottom.SelectedItem = window.FindName("TerminalTab");
        await UntilTerminalFocusedAsync(terminalBrowser);
        message.Focus(); Keyboard.Focus(message);
        setPanel.Invoke(window, [false]);
        setPanel.Invoke(window, [true]);
        await (Task)ensureTerminal.Invoke(window, null)!;
        await UntilTerminalFocusedAsync(terminalBrowser);
        Check(terminalBrowser.IsKeyboardFocusWithin || terminalBrowser.IsFocused,
            "Reopening the bottom panel with Terminal selected did not focus the WebView");
        Console.WriteLine("Dark-theme labels, tabs, compact history sizing, terminal refocus and menu removal checks passed.");
        var tabs = (TabControl)window.FindName("Documents");
        var otherTab = new TabItem { Header = "Other", Content = new TextBlock { Text = "Other document" } };
        tabs.Items.Add(otherTab);
        var refreshingField = typeof(MainWindow).GetField("refreshing", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(MainWindow), "refreshing");
        // Terminal focus churn can queue an Activated refresh; wait before status-diff timing tests.
        await Until(() => !(bool)refreshingField.GetValue(window)!);
        await Task.Delay(200);
        await Until(() => !(bool)refreshingField.GetValue(window)!);
        delayNextStatusDiff = true;
        changes.SelectedIndex = 0;
        await statusDiffStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tabs.SelectedItem = otherTab;
        delayedStatusDiff.SetResult(await git.StatusDiffAsync((GitFile)changes.SelectedItem, staged: false));
        await Task.Delay(150);
        Check(ReferenceEquals(tabs.SelectedItem, otherTab), "A late status diff stole focus from the selected document tab");
        changes.SelectedIndex = -1;
        changes.SelectedIndex = 0;
        await Until(() => tabs.SelectedItem is TabItem { Content: RevisionDiffView });
        var workingDiff = (RevisionDiffView)((TabItem)tabs.SelectedItem).Content;
        Check(workingDiff.Before.Text.Contains("ui original") && workingDiff.After.Text.Contains("ui modified"), "Working tree selection did not open a split diff");
        for (var i = 0; i < 40; i++)
            await File.WriteAllTextAsync(Path.Combine(root, $"scroll-{i:D2}.txt"), $"file {i}\n");
        var abandonedUiLock = Path.Combine(root, ".git", "index.lock");
        await File.WriteAllTextAsync(abandonedUiLock, "");
        File.SetLastWriteTimeUtc(abandonedUiLock, DateTime.UtcNow.AddMinutes(-2));
        Click(window, "Stage all");
        await Until(() => actions.IsEnabled && staged.Items.Count == 41 && changes.Items.Count == 0);
        CheckBadge("41", "41 changed files");
        Check(Equals(sourceControlLabel.Foreground, accent) && sourceControlLabel.FontWeight == FontWeights.SemiBold,
            "Staged changes alone did not highlight the Source Control tab");
        Check(!File.Exists(abandonedUiLock), "Stage all button did not recover an abandoned lock");
        var recoveryEntry = ((TextBox)window.FindName("OutputText")).Text.Split('\n')
            .Single(line => line.Contains("Recovered abandoned Git lock '", StringComparison.Ordinal));
        File.Delete(recoveryEntry.Split("Backup: ")[1].Trim());
        var uiStageIndex = staged.Items.Cast<GitFile>().Select((file, index) => (file, index)).Single(x => x.file.Path == "ui.txt").index;
        staged.SelectedIndex = uiStageIndex;
        await Until(() => tabs.SelectedItem is TabItem { Content: RevisionDiffView, ToolTip: string tip }
            && tip.EndsWith(" · staged changes", StringComparison.Ordinal));
        var stagedDiff = (RevisionDiffView)((TabItem)tabs.SelectedItem).Content;
        Check(stagedDiff.Before.Text.Contains("ui original") && stagedDiff.After.Text.Contains("ui modified"), "Staged selection did not open a split diff");
        await File.WriteAllTextAsync(Path.Combine(root, "ui.txt"), "ui edited after stage\n");
        var refresh = typeof(MainWindow).GetMethod("RefreshRepositoryAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "RefreshRepositoryAsync");
        await (Task)refresh.Invoke(window, [false])!;
        Check(staged.Items.Count == 41 && changes.Items.Count == 1
            && ((GitFile)changes.Items[0]).Path == "ui.txt",
            "Expected ui.txt in both Git status lists");
        CheckBadge("41", "41 changed files");
        var workingTab = tabs.Items.Cast<TabItem>().Single(t => t.ToolTip is string tip && tip.EndsWith(" · working tree changes", StringComparison.Ordinal));
        var refreshedWorkingDiff = (RevisionDiffView)workingTab.Content!;
        Check(refreshedWorkingDiff.Before.Text.Contains("ui modified") && refreshedWorkingDiff.After.Text.Contains("ui edited after stage")
            && refreshedWorkingDiff.Rows.Any(row => row.Changed), "Refresh left an open working-tree split diff stale");
        var stagedTab = tabs.Items.Cast<TabItem>().Single(t => t.ToolTip is string tip && tip.EndsWith(" · staged changes", StringComparison.Ordinal));
        var refreshedStagedDiff = (RevisionDiffView)stagedTab.Content!;
        Check(refreshedStagedDiff.Before.Text.Contains("ui original") && refreshedStagedDiff.After.Text.Contains("ui modified"),
            "Refresh changed the staged split diff to working-tree content");
        MenuItem StagedMenu(string header) =>
            staged.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
        MenuItem ChangesMenu(string header) =>
            changes.ContextMenu!.Items.OfType<MenuItem>().Single(item => Equals(item.Header, header));
        Check(staged.ContextMenu != null
            && StagedMenu("Unstage") != null
            && StagedMenu("Open With...") != null
            && StagedMenu("Reveal in File Explorer") != null
            && StagedMenu("Open in Integrated Terminal") != null
            && StagedMenu("Copy Path") != null
            && StagedMenu("Copy Relative Path") != null
            && StagedMenu("Rename...") != null
            && StagedMenu("Delete") != null
            && StagedMenu("Git: View File History") != null,
            "Staged context menu is missing one or more planned actions");
        StagedMenu("Copy Path").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(string.Equals(System.Windows.Clipboard.GetText(), Path.GetFullPath(Path.Combine(root, "ui.txt")), StringComparison.OrdinalIgnoreCase),
            "Staged Copy Path did not place the absolute file path on the clipboard");
        StagedMenu("Copy Relative Path").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(System.Windows.Clipboard.GetText().Replace('\\', '/') == "ui.txt",
            "Staged Copy Relative Path did not place the repo-relative path on the clipboard");
        var unstage = StagedMenu("Unstage");
        // Menu opening snapshots the staged selection; clear it and select Changes to mimic focus drift.
        unstage.Tag = staged.SelectedItems.Cast<GitFile>().ToArray();
        Check(((GitFile[])unstage.Tag!).Any(f => f.Path == "ui.txt"), "Staged Unstage snapshot missed ui.txt");
        staged.SelectedItems.Clear();
        changes.SelectedIndex = 0;
        Check(staged.SelectedItems.Count == 0 && changes.SelectedItems.Count == 1,
            "Could not move selection to Changes before menu Unstage");
        unstage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => actions.IsEnabled && staged.Items.Count == 40 && changes.Items.Count == 1
            && changes.Items.OfType<GitFile>().Any(f => f.Path == "ui.txt"));
        changes.SelectedIndex = 0;
        Check(changes.ContextMenu != null
            && ChangesMenu("Stage") != null
            && ChangesMenu("Discard") != null
            && ChangesMenu("Open With...") != null
            && ChangesMenu("Reveal in File Explorer") != null
            && ChangesMenu("Open in Integrated Terminal") != null
            && ChangesMenu("Copy Path") != null
            && ChangesMenu("Copy Relative Path") != null
            && ChangesMenu("Rename...") != null
            && ChangesMenu("Delete") != null
            && ChangesMenu("Git: View File History") != null,
            "Changes context menu is missing one or more planned actions");
        ChangesMenu("Copy Path").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(string.Equals(System.Windows.Clipboard.GetText(), Path.GetFullPath(Path.Combine(root, "ui.txt")), StringComparison.OrdinalIgnoreCase),
            "Changes Copy Path did not place the absolute file path on the clipboard");
        var discard = ChangesMenu("Discard");
        discard.Tag = changes.SelectedItems.Cast<GitFile>().ToArray();
        Check(((GitFile[])discard.Tag!).Any(f => f.Path == "ui.txt"), "Changes Discard snapshot missed ui.txt");
        // Move selection to Staged so Discard must use the menu snapshot, not click-time Changes selection.
        staged.SelectedIndex = staged.Items.Cast<GitFile>().Select((file, index) => (file, index))
            .First(x => x.file.Path != "ui.txt").index;
        changes.SelectedItems.Clear();
        Check(changes.SelectedItems.Count == 0 && staged.SelectedItems.Count == 1,
            "Could not move selection to Staged before menu Discard");
        discard.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => actions.IsEnabled && changes.Items.Count == 0
            && !changes.Items.OfType<GitFile>().Any(f => f.Path == "ui.txt"));
        Check((await File.ReadAllTextAsync(Path.Combine(root, "ui.txt"))).Replace("\r\n", "\n") == "ui original\n",
            "Discard did not restore ui.txt worktree content from the index");
        await File.WriteAllTextAsync(Path.Combine(root, "ui.txt"), "ui edited after discard\n");
        await (Task)refresh.Invoke(window, [false])!;
        await Until(() => changes.Items.OfType<GitFile>().Any(f => f.Path == "ui.txt"));
        changes.SelectedIndex = changes.Items.Cast<GitFile>().Select((file, index) => (file, index))
            .Single(x => x.file.Path == "ui.txt").index;
        var stage = ChangesMenu("Stage");
        stage.Tag = changes.SelectedItems.Cast<GitFile>().ToArray();
        Check(((GitFile[])stage.Tag!).Any(f => f.Path == "ui.txt"), "Changes Stage snapshot missed ui.txt");
        // Select a staged file so a hardcoded StagedList read would operate on the wrong list.
        staged.SelectedIndex = staged.Items.Cast<GitFile>().Select((file, index) => (file, index))
            .First(x => x.file.Path != "ui.txt").index;
        changes.SelectedItems.Clear();
        Check(changes.SelectedItems.Count == 0 && staged.SelectedItems.Count == 1,
            "Could not move selection to Staged before menu Stage");
        stage.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Until(() => actions.IsEnabled && staged.Items.Count == 41 && changes.Items.Count == 0
            && staged.Items.OfType<GitFile>().Any(f => f.Path == "ui.txt"));
        Console.WriteLine("Source Control staged/changes context menus, clipboard paths, Discard, and Stage/Unstage actions passed.");
        message.Text = "UI commit"; Click(window, "Commit staged");
        await Until(() => actions.IsEnabled && message.Text.Length == 0 && staged.Items.Count == 0);
        CheckBadge("0", "0 changed files", Visibility.Hidden);
        Check(Equals(sourceControlLabel.Foreground, window.FindResource("TextBrush")) && sourceControlLabel.FontWeight == FontWeights.Normal,
            "Committing the last changes did not clear the Source Control highlight");
        Check((await git.HistoryAsync(1))[0].Subject == "UI commit", "UI commit did not execute");

        // A staged deletion and an untracked replacement can produce two records for one path.
        await Git(git, "rm", "--cached", "--", "ui.txt");
        await (Task)refresh.Invoke(window, [false])!;
        Check(staged.Items.Count == 1 && changes.Items.Count == 1, "Expected a staged deletion and an untracked replacement");
        CheckBadge("1", "1 changed file");
        var badgeDirectory = Directory.CreateDirectory(Path.Combine(root, "badge-files")).FullName;
        var badgeFiles = Enumerable.Range(1, 99).Select(i => Path.Combine(badgeDirectory, $"{i}.txt")).ToArray();
        foreach (var file in badgeFiles) await File.WriteAllTextAsync(file, "");
        await (Task)refresh.Invoke(window, [false])!;
        CheckBadge("99+", "100 changed files");
        window.UpdateLayout();
        Check(headerPanel!.ActualHeight < 48, "The Source Control badge wrapped the 240 DIP sidebar");
        foreach (var file in badgeFiles) File.Delete(file);
        Directory.Delete(badgeDirectory);
        await Git(git, "add", "--", "ui.txt");
        await (Task)refresh.Invoke(window, [false])!;
        CheckBadge("0", "0 changed files", Visibility.Hidden);

        var historyGrid = (DataGrid)window.FindName("HistoryList");
        historyGrid.SelectedIndex = 0;
        var selectedCommit = (Commit)historyGrid.SelectedItem;
        await Until(() => selectedCommit.FilesLoaded);
        Check(selectedCommit.Files.Count == 41 && selectedCommit.Files.Any(file => file.Path == "ui.txt"), "Commit changed-file list did not load the large selection");
        var commitFilesList = (ListBox)window.FindName("CommitFilesList");
        await Until(() => ReferenceEquals(commitFilesList.ItemsSource, selectedCommit.Files));
        Check(commitFilesList.Items.Count == 41, "Selected commit files were not shown in their own list");
        historyGrid.SelectedIndex = 1;
        var previousCommit = (Commit)historyGrid.Items[1];
        await Until(() => previousCommit.FilesLoaded);
        Check(historyGrid.SelectedItems.Count == 1 && ReferenceEquals(commitFilesList.ItemsSource, previousCommit.Files),
            "Switching commits kept a stale selection and hid the changed files");
        historyGrid.SelectedIndex = 0;
        Check(historyGrid.SelectedItems.Count == 1 && ReferenceEquals(commitFilesList.ItemsSource, selectedCommit.Files),
            "Returning to a loaded commit did not restore its changed files");
        window.UpdateLayout();
        var selectedRow = (DataGridRow)historyGrid.ItemContainerGenerator.ContainerFromItem(selectedCommit);
        selectedRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        });
        window.UpdateLayout();
        Check(historyGrid.SelectedItems.Count == 0 && !selectedRow.IsSelected
            && commitFilesList.ItemsSource == null && !((Border)window.FindName("HistoryFilesPanel")).IsVisible,
            "Clicking the selected commit did not clear its highlight and close the changed-file list");
        historyGrid.SelectedIndex = 0;
        window.UpdateLayout();
        Check(ReferenceEquals(commitFilesList.ItemsSource, selectedCommit.Files)
            && ((Border)window.FindName("HistoryFilesPanel")).IsVisible,
            "Reselecting the commit did not reopen its cached changed files");
        var detailSubject = (TextBlock)window.FindName("CommitDetailSubject");
        var detailHash = (TextBlock)window.FindName("CommitDetailHash");
        Check(detailSubject.Text == selectedCommit.Subject && detailHash.Text == selectedCommit.Hash,
            "Commit detail header did not show subject and hash");
        var copyDetailMsg = (Button)window.FindName("CopyDetailSubjectButton");
        var copyDetailHash = (Button)window.FindName("CopyDetailHashButton");
        copyDetailMsg.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Clipboard.GetText() == selectedCommit.Subject, "CopyDetailSubjectButton did not copy commit message");
        copyDetailHash.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Check(Clipboard.GetText() == selectedCommit.Hash, "CopyDetailHashButton did not copy commit hash");
        Button? fileButton = null;
        IEnumerable<DependencyObject> Visuals(DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var nested in Visuals(child)) yield return nested;
            }
        }
        var filesScroll = Visuals(commitFilesList).OfType<ScrollViewer>().Single();
        await Until(() => filesScroll.ExtentHeight > filesScroll.ViewportHeight);
        var historyScroll = Visuals(historyGrid).OfType<ScrollViewer>().First();
        var historyOffset = historyScroll.VerticalOffset;
        await window.Dispatcher.InvokeAsync(() => filesScroll.ScrollToVerticalOffset(100));
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        Check(Math.Abs(historyScroll.VerticalOffset - historyOffset) < 1, "Scrolling changed files moved the commit history list");
        await window.Dispatcher.InvokeAsync(() => filesScroll.ScrollToTop());
        await Until(() => (fileButton = Visuals(commitFilesList).OfType<Button>().FirstOrDefault(b => b.DataContext is CommitFile { Path: "ui.txt" })) != null);
        fileButton!.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var revisionKey = "revision:" + selectedCommit.Hash + ":ui.txt";
        await Until(() => tabs.SelectedItem is TabItem { Content: RevisionDiffView, Tag: string key } && key == revisionKey);
        var revision = (RevisionDiffView)((TabItem)tabs.SelectedItem).Content;
        Check(revision.Before.Text.Contains("ui original") && revision.After.Text.Contains("ui edited after discard") && revision.Rows.Any(r => r.Changed), "Commit file click did not open the side-by-side historical diff");
        var filesPanel = (Border)window.FindName("HistoryFilesPanel");
        var filesColumn = (ColumnDefinition)window.FindName("HistoryFilesColumn");
        var filesSplitter = (GridSplitter)window.FindName("HistoryFilesSplitter");
        window.UpdateLayout();
        var filesOrigin = filesPanel.TranslatePoint(new Point(), window);
        var tabsOrigin = tabs.TranslatePoint(new Point(), window);
        Check(filesPanel.IsVisible && filesPanel.ActualWidth > 0 && tabsOrigin.X > filesOrigin.X
            && Math.Abs(tabsOrigin.X - filesOrigin.X - filesPanel.ActualWidth - filesSplitter.ActualWidth) < 1
            && Math.Abs(tabsOrigin.Y - filesOrigin.Y) < 1,
            "Changed files and the historical diff are not adjacent in the upper workspace");
        // Tall paired documents: absolute offset sync must keep both panes locked after layout.
        // After lines are much wider so horizontal scroll is possible only on that side.
        var tall = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"line-{i}-short"));
        var wide = new string('x', 160);
        var taller = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"line-{i}-{wide}"))
            + "\n" + string.Join('\n', Enumerable.Range(121, 40).Select(i => $"added-{i}-{wide}"));
        var paired = new RevisionDiffView(
            new CommitFile("abc", "def", "tall.txt", "tall.txt", "M", "1", "2"),
            new RevisionDiff(new(tall + "\n", true), new(taller + "\n", true),
                "@@ -1,120 +1,160 @@\n" + string.Concat(Enumerable.Range(1, 120).Select(i => $"-line-{i}-short\n+line-{i}-{wide}\n"))
                + "@@ -120,0 +121,40 @@\n" + string.Join('\n', Enumerable.Range(121, 40).Select(i => $"+added-{i}-{wide}")) + "\n"));
        var host = new Window { Width = 900, Height = 500, Content = paired, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        host.Show();
        await Until(() => paired.IsLoaded && paired.Before.ExtentHeight > 0 && paired.After.ExtentHeight > 0);
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(Math.Abs(paired.Before.ExtentHeight - paired.After.ExtentHeight) < 1, "Aligned panes had different scroll extents");
        await paired.Dispatcher.InvokeAsync(() => paired.Before.ScrollToVerticalOffset(240));
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Until(() => Math.Abs(paired.After.VerticalOffset - paired.Before.VerticalOffset) < 1);
        Check(Math.Abs(paired.After.VerticalOffset - 240) < 1, "Follower pane did not mirror vertical scroll");
        await paired.Dispatcher.InvokeAsync(() => paired.After.ScrollToVerticalOffset(80));
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Until(() => Math.Abs(paired.Before.VerticalOffset - paired.After.VerticalOffset) < 1);
        Check(Math.Abs(paired.Before.VerticalOffset - 80) < 1, "Leader pane did not mirror reverse vertical scroll");
        // Right-pane lead must stick; a Before←forced ReleaseSync would snap After back.
        await paired.Dispatcher.InvokeAsync(() => paired.After.ScrollToVerticalOffset(160));
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Until(() => Math.Abs(paired.Before.VerticalOffset - 160) < 1 && Math.Abs(paired.After.VerticalOffset - 160) < 1);
        double horizontal = 0;
        await paired.Dispatcher.InvokeAsync(() =>
        {
            horizontal = Math.Min(80, Math.Max(0, paired.After.ExtentWidth - paired.After.ViewportWidth));
            Check(horizontal > 20, "Test fixture was not wide enough to exercise independent horizontal scroll");
            paired.After.ScrollToHorizontalOffset(horizontal);
        });
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(Math.Abs(paired.After.HorizontalOffset - horizontal) < 1, "Vertical sync rubber-banded the longer pane's horizontal scroll");
        await paired.Dispatcher.InvokeAsync(() => paired.Before.ScrollToVerticalOffset(0));
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Until(() => Math.Abs(paired.Before.VerticalOffset) < 1 && Math.Abs(paired.After.VerticalOffset) < 1);
        Check(paired.JumpToChangeOverview(0.98), "Overview jump reported no changed row near the bottom");
        await paired.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Until(() => paired.After.VerticalOffset > paired.After.ExtentHeight * 0.4
            && Math.Abs(paired.Before.VerticalOffset - paired.After.VerticalOffset) < 1);
        Check(paired.After.VerticalOffset > paired.After.ExtentHeight * 0.4,
            "Overview marker jump did not scroll near the trailing changes");
        host.Close();

        var tabCount = tabs.Items.Count;
        fileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Task.Delay(200);
        Check(tabs.Items.Count == tabCount && tabs.SelectedItem is TabItem { Content: RevisionDiffView }, "Re-clicking an open revision created a duplicate tab");

        var anotherFile = Visuals(commitFilesList).OfType<Button>().First(b => b.DataContext is CommitFile { Path: "scroll-00.txt" });
        anotherFile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        fileButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var anotherRevisionKey = "revision:" + selectedCommit.Hash + ":scroll-00.txt";
        await Until(() => tabs.SelectedItem is TabItem { Content: RevisionDiffView, Tag: string key } && key == anotherRevisionKey);

        await (Task)refresh.Invoke(window, [false])!;
        Check(ReferenceEquals(historyGrid.SelectedItem, selectedCommit) && selectedCommit.FilesLoaded && selectedCommit.Files.Count == 41,
            "Status-only refresh replaced the selected commit or cleared its files");
        await (Task)refresh.Invoke(window, [true])!;
        var afterReload = (Commit)historyGrid.SelectedItem;
        Check(afterReload.Hash == selectedCommit.Hash && afterReload.FilesLoaded && afterReload.Files.Count == 41 && afterReload.Files.Any(file => file.Path == "ui.txt"),
            "History reload lost selection or previously loaded changed files");

        historyGrid.SelectedItems.Clear();
        historyGrid.SelectedItems.Add(historyGrid.Items[0]);
        historyGrid.SelectedItems.Add(historyGrid.Items[1]);
        await Until(() => ((Commit)historyGrid.Items[0]).FilesLoaded && ((Commit)historyGrid.Items[1]).FilesLoaded);
        Check(((Commit)historyGrid.Items[0]).Files.Count > 0 && ((Commit)historyGrid.Items[1]).Files.Count > 0,
            "Multi-select did not load changed files for both commits");
        window.UpdateLayout();
        Check(!filesPanel.IsVisible && filesColumn.ActualWidth == 0 && filesSplitter.ActualWidth == 0,
            "Selecting two commits did not return the full workspace width to documents");
        historyGrid.SelectedIndex = -1;
        historyGrid.SelectedIndex = 0;
        await Until(() => filesPanel.IsVisible);
        historyVisibilityButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Check(!filesPanel.IsVisible, "Collapsing the history did not hide its changed files");
        historyVisibilityButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await Until(() => filesPanel.IsVisible && ReferenceEquals(commitFilesList.ItemsSource, afterReload.Files));

        // Read-only viewer must show programmatic content beyond the old TextBox input limit.
        var longPreview = new DocumentView("test"); longPreview.Update(new(new string('x', 40000), true), "", "", false);
        Check(((TextEditor)((TabItem)longPreview.Items[0]).Content).Text.Length == 40000, "Viewer truncated a 40K document");
        // Drop live sessions first so CloseWindow does not block on the PowerShell confirm dialog.
        if (terminalField.GetValue(window) is TerminalPanel openTerminal)
        {
            await openTerminal.DisposeAsync();
            terminalField.SetValue(window, null);
        }
        window.Close();
        await Until(() => !window.IsVisible);
        Console.WriteLine("WPF Stage all / unstage / stage / commit and preview refresh passed.");
    }
    private static async Task SearchUiChecks(MainWindow window, string root)
    {
        const string marker = "unique-ui-search-marker";
        const string seed = "ui-search-seed.txt";
        string[] fixtureFiles = [seed, "ui-search-excluded.txt", "ui-search-other.md"];
        foreach (var file in fixtureFiles)
            await File.WriteAllTextAsync(Path.Combine(root, file), $"first line\n{marker} on line two\n");

        var query = (TextBox)window.FindName("SearchQuery");
        var include = (TextBox)window.FindName("SearchInclude");
        var exclude = (TextBox)window.FindName("SearchExclude");
        var status = (TextBlock)window.FindName("SearchStatus");
        var results = (TreeView)window.FindName("SearchResults");
        var regex = (ToggleButton)window.FindName("SearchUseRegex");
        foreach (var (input, label) in new[] { (query, "Search repository"), (include, "Files to include"), (exclude, "Files to exclude") })
        {
            Check(AutomationProperties.GetLabeledBy(input) is TextBlock { IsVisible: true } labeled && labeled.Text == label
                && UIElementAutomationPeer.CreatePeerForElement(input)?.GetName() == label,
                $"Search input is missing the visible and accessible label {label}");
            Check(input.SelectionOpacity is > 0 and < 1, $"{label} has an unreadable selection highlight");
        }
        foreach (var (id, glyph, name) in new[]
        {
            ("SearchMatchCase", "Aa", "Match Case"), ("SearchWholeWord", "ab", "Match Whole Word"),
            ("SearchUseRegex", ".*", "Use Regular Expression"),
        })
        {
            var toggle = (ToggleButton)window.FindName(id);
            Check(Equals(toggle.Content, glyph) && UIElementAutomationPeer.CreatePeerForElement(toggle)?.GetName() == name,
                $"Search toggle is missing the glyph or accessible name for {name}");
            toggle.ApplyTemplate();
            Check(toggle.Template.FindName("Frame", toggle) is Border,
                $"{name} fell back to a Windows light template");
        }

        query.Text = marker;
        await Until(() => status.Text == "3 results in 3 files");
        include.Text = "*.txt";
        Check(status.Text == "Searching…" && results.Items.Count == 0, "Previous results survived a filter change");
        await Until(() => status.Text == "2 results in 2 files");
        exclude.Text = "ui-search-excluded.txt";
        await Until(() => status.Text == "1 result in 1 file");
        var fileNode = (TreeViewItem)results.Items[0];
        Check(fileNode.Tag is SearchFileGroup group && group.Path == seed, "Search filters returned the wrong file");
        var hitNode = (TreeViewItem)fileNode.Items[0];
        Check(hitNode.Tag is SearchHit { Line: 2 }, "Search did not return the seeded line");
        hitNode.IsSelected = true;
        var documents = (TabControl)window.FindName("Documents");
        await Until(() => documents.SelectedItem is TabItem { Content: DocumentView view }
            && view.RelativePath == seed
            && view.SelectedItem is TabItem { Content: TextEditor editor }
            && !editor.IsReadOnly && editor.TextArea.Caret.Line == 2);
        Check(documents.SelectedItem is TabItem { Content: DocumentView opened }
            && opened.Items.OfType<TabItem>().Skip(1).All(tab => tab.Content is TextEditor diff && diff.IsReadOnly),
            "Staged/working diff tabs must stay read-only");

        query.Text = "not-present-in-the-fixture";
        Check(status.Text == "Searching…" && results.Items.Count == 0, "Previous results survived a query change");
        await Until(() => status.Text == "No matches in this repository.");
        query.Text = " ";
        Check(status.Text == "Type to search across the repository." && results.Items.Count == 0,
            "Clearing Search did not restore the empty state immediately");

        var bottom = (TabControl)window.FindName("BottomPanel");
        var priorBottomTab = bottom.SelectedItem;
        regex.IsChecked = true;
        query.Text = "(";
        await Until(() => status.Text.StartsWith("Invalid regular expression:", StringComparison.Ordinal));
        Check(ReferenceEquals(bottom.SelectedItem, priorBottomTab) && results.Items.Count == 0
            && ((TextBox)window.FindName("OutputText")).Text.Contains("Invalid regular expression", StringComparison.Ordinal),
            "The regex error did not stay in Search and log to Output without switching tabs");
        query.Clear();
        foreach (var file in fixtureFiles) File.Delete(Path.Combine(root, file));
        Console.WriteLine("Search control accessibility, git grep filters, search states and open-at-line passed.");
    }

    private static async Task EditUiChecks(MainWindow window, string root)
    {
        const string relative = "ui-edit-seed.txt";
        var absolute = Path.Combine(root, relative);
        await File.WriteAllTextAsync(absolute, "line one\nline two\n");
        var openFile = typeof(MainWindow).GetMethod("OpenFileAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "OpenFileAsync");
        var saveDocument = typeof(MainWindow).GetMethod("SaveDocumentAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "SaveDocumentAsync");
        await ((Task)openFile.Invoke(window, [relative, false, 0, 1])!);
        var documents = (TabControl)window.FindName("Documents");
        await Until(() => documents.SelectedItem is TabItem { Content: DocumentView { RelativePath: relative, CanEdit: true } });
        var tab = (TabItem)documents.SelectedItem!;
        var view = (DocumentView)tab.Content;
        Check(view.FileEditorControl is { IsReadOnly: false }, "Editable File tab stayed read-only");
        Check(view.Items.OfType<TabItem>().Skip(1).All(item => item.Content is TextEditor editor && editor.IsReadOnly),
            "Diff panes became editable");

        view.FileEditorControl.Text = "line one\nedited line two\n";
        await Until(() => view.IsDirty);
        Check(tab.Header is StackPanel header
            && header.Children.OfType<TextBlock>().Any(label => label.Text == relative + " *"),
            "Dirty File tab did not show * in the header");

        Check(await ((Task<bool>)saveDocument.Invoke(window, [view])!), "SaveDocumentAsync returned false");
        await Until(() => !view.IsDirty);
        Check(tab.Header is StackPanel cleanHeader
            && cleanHeader.Children.OfType<TextBlock>().Any(label => label.Text == relative),
            "Saved File tab kept the dirty marker");
        Check(File.ReadAllText(absolute).Replace("\r\n", "\n") == "line one\nedited line two\n",
            "Saved File tab did not write UTF-8 LF text to disk");

        view.FileEditorControl.Text = "discard me\n";
        await Until(() => view.IsDirty);
        // Closing with discard uses MessageBox; force-close after verifying dirty, then reopen to confirm disk unchanged.
        var forceClose = typeof(MainWindow).GetMethod("ForceCloseDocumentTab", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "ForceCloseDocumentTab");
        forceClose.Invoke(window, [tab]);
        Check(!documents.Items.OfType<TabItem>().Any(item => item.Content is DocumentView { RelativePath: relative }),
            "Force-close left the edited tab open");
        Check(File.ReadAllText(absolute).Replace("\r\n", "\n") == "line one\nedited line two\n",
            "Discarded buffer incorrectly wrote to disk");

        await ((Task)openFile.Invoke(window, [relative, false, 0, 1])!);
        await Until(() => documents.SelectedItem is TabItem { Content: DocumentView reopened }
            && reopened.RelativePath == relative && !reopened.IsDirty
            && reopened.CurrentText == "line one\nedited line two\n");
        tab = (TabItem)documents.SelectedItem!;
        var welcomeTab = documents.Items.OfType<TabItem>().Single(item => Equals(item.Header, "Welcome"));
        Check(welcomeTab.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Close All")) == true,
            "Welcome tab is missing Close All");
        Check(tab.ContextMenu?.Items.OfType<MenuItem>().Any(item => Equals(item.Header, "Close All")) == true,
            "Document tab is missing Close All");
        var closeAll = typeof(MainWindow).GetMethod("CloseAllDocumentTabsAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainWindow), "CloseAllDocumentTabsAsync");
        await ((Task)closeAll.Invoke(window, null)!);
        Check(documents.Items.Count == 0, "Close All left document tabs open");
        File.Delete(absolute);
        Console.WriteLine("File tab edit, dirty marker, save, discard and Close All passed.");
    }

    private static async Task SearchRaceChecks(string root)
    {
        var calls = new Dictionary<string, (CancellationToken Token, TaskCompletionSource<SearchResult> Completion)>();
        var window = new MainWindow(root, rememberLayout: false, runSearch: (request, token) =>
        {
            var completion = new TaskCompletionSource<SearchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            calls.Add(request.Query, (token, completion));
            return completion.Task;
        });
        window.Show();
        try
        {
            ((TabControl)window.FindName("Sidebar")).SelectedItem = window.FindName("SearchTab");
            var query = (TextBox)window.FindName("SearchQuery");
            var status = (TextBlock)window.FindName("SearchStatus");
            var results = (TreeView)window.FindName("SearchResults");
            var output = (TextBox)window.FindName("OutputText");

            query.Text = "stale result";
            await Until(() => calls.ContainsKey("stale result"));
            query.Text = "stale error";
            await Until(() => calls.ContainsKey("stale error"));
            query.Text = "latest";
            Check(!calls.ContainsKey("latest"), "Typing bypassed the search debounce");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(query), Environment.TickCount, Key.Enter)
            {
                RoutedEvent = Keyboard.KeyDownEvent,
            };
            query.RaiseEvent(enter);
            Check(enter.Handled && calls.ContainsKey("latest"), "Enter did not run the search immediately");
            Check(calls["stale result"].Token.IsCancellationRequested && calls["stale error"].Token.IsCancellationRequested,
                "New requests did not cancel superseded searches");

            calls["latest"].Completion.SetResult(new([new("latest.txt", [new("latest.txt", 2, 1, "latest")])], 1, true, null));
            await Until(() => status.Text == "1 result in 1 file · limit reached");
            calls["stale result"].Completion.SetResult(new([], 0, false, null));
            calls["stale error"].Completion.SetException(new IOException("superseded search failure"));
            // Hold the latest UI across several dispatcher idle cycles so a late
            // superseded continuation cannot sneak in between a single Yield and Check.
            var hold = Stopwatch.StartNew();
            while (hold.Elapsed < TimeSpan.FromMilliseconds(300))
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check(status.Text == "1 result in 1 file · limit reached"
                    && results.Items.Count == 1 && results.Items[0] is TreeViewItem { Tag: SearchFileGroup { Path: "latest.txt" } }
                    && !output.Text.Contains("superseded search failure", StringComparison.Ordinal),
                    "A superseded search published its result or logged an error");
            }

            query.Text = "command failure";
            await Until(() => calls.ContainsKey("command failure"));
            var bottom = (TabControl)window.FindName("BottomPanel");
            var priorBottomTab = bottom.SelectedItem;
            calls["command failure"].Completion.SetException(new IOException("Unable to start git.exe."));
            await Until(() => status.Text == "Unable to start git.exe.");
            Check(ReferenceEquals(bottom.SelectedItem, priorBottomTab) && results.Items.Count == 0
                && output.Text.Contains("Unable to start git.exe.", StringComparison.Ordinal),
                "The current command failure did not stay in Search and log to Output without switching tabs");

            query.Text = "pending at close";
            await Until(() => calls.ContainsKey("pending at close"));
            window.Close();
            Check(calls["pending at close"].Token.IsCancellationRequested, "Closing did not cancel the active search");
            calls["pending at close"].Completion.SetResult(new([], 0, false, null));
            await Until(() => !window.IsVisible);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(status.Text == "Searching…", "Search updated its status after the window began closing");
        }
        finally
        {
            foreach (var (_, completion) in calls.Values) completion.TrySetCanceled();
            if (window.IsVisible) window.Close();
        }
        Console.WriteLine("Search cancellation, latest results and error isolation passed.");
    }

    private static async Task Until(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Smoke check timed out."); await Task.Delay(40); }
        // Complete the event's refresh sequence before sending the next user action.
        await Task.Delay(150);
    }
    private static async Task UntilTerminalFocusedAsync(WebView2 browser)
    {
        // WPF IsFocused alone can pass with native focus on a WebView toolbar button.
        var timer = Stopwatch.StartNew();
        while (await browser.CoreWebView2.ExecuteScriptAsync(
            "document.hasFocus() && document.activeElement === sessions.get(active)?.terminal.textarea") != "true")
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(15))
                throw new TimeoutException("Keyboard focus did not reach the active xterm textarea.");
            await Task.Delay(40);
        }
        await Task.Delay(150);
        Check(await browser.CoreWebView2.ExecuteScriptAsync(
            "document.hasFocus() && document.activeElement === sessions.get(active)?.terminal.textarea") == "true",
            "Terminal focus was replaced after pending tab/keyboard processing");
    }

    private static async Task WebTerminalChecks(string root)
    {
        var panel = new TerminalPanel(root);
        var window = new Window { Width = 800, Height = 350, Content = panel };
        var errors = new List<string>(); panel.Error += errors.Add;
        var shortcuts = new List<string>(); panel.Shortcut += shortcuts.Add;
        window.Show();
        try
        {
            var firstDirectory = Directory.CreateDirectory(Path.Combine(root, "startup-first")).FullName;
            var secondDirectory = Directory.CreateDirectory(Path.Combine(root, "startup-second")).FullName;
            await Task.WhenAll(panel.AddSessionAsync(firstDirectory), panel.AddSessionAsync(secondDirectory));
            await Until(() => panel.SessionCount == 2 || errors.Count > 0);
            Check(errors.Count == 0, "Terminal initialization failed: " + string.Join("; ", errors));
            var browser = (WebView2)panel.Children[0];
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.input(\"Write-Output ('WEB_' + '42')\\r\",true)");
            var timer = Stopwatch.StartNew();
            string screen = "";
            do
            {
                screen = await browser.CoreWebView2.ExecuteScriptAsync("Array.from({length:sessions.get(active).terminal.buffer.active.length},(_,i)=>sessions.get(active).terminal.buffer.active.getLine(i).translateToString()).join('\\n')");
                if (screen.Contains("WEB_42")) break;
                await Task.Delay(100);
            } while (timer.Elapsed < TimeSpan.FromSeconds(15));
            Check(screen.Contains("WEB_42"), "WebView/xterm terminal did not render PowerShell output. " + screen);
            async Task<string> ScreenAsync() =>
                await browser.CoreWebView2.ExecuteScriptAsync("Array.from({length:sessions.get(active).terminal.buffer.active.length},(_,i)=>sessions.get(active).terminal.buffer.active.getLine(i).translateToString()).join('\\n')");
            // Cursor line (plus one wrapped continuation) only — full scrollback would false-pass.
            async Task<string> CursorInputAsync()
            {
                var json = await browser.CoreWebView2.ExecuteScriptAsync(
                    "(()=>{const b=sessions.get(active).terminal.buffer.active;const y=b.baseY+b.cursorY;" +
                    "const cur=b.getLine(y)?.translateToString(true)??'';" +
                    "const prev=y>b.baseY?(b.getLine(y-1)?.translateToString(true)??''):'';" +
                    "return prev.includes('>')&&!cur.includes('>')?prev+cur:cur;})()");
                return JsonSerializer.Deserialize<string>(json) ?? "";
            }
            async Task WaitForScreenAsync(Func<string, bool> match, string failure)
            {
                timer.Restart();
                do
                {
                    screen = await ScreenAsync();
                    if (match(screen)) return;
                    await Task.Delay(100);
                } while (timer.Elapsed < TimeSpan.FromSeconds(15));
                throw new TimeoutException(failure + " " + screen);
            }
            async Task WaitForCursorInputAsync(Func<string, bool> match, string failure)
            {
                timer.Restart();
                string input;
                do
                {
                    input = await CursorInputAsync();
                    if (match(input)) return;
                    await Task.Delay(100);
                } while (timer.Elapsed < TimeSpan.FromSeconds(15));
                throw new TimeoutException(failure + " cursorInput=" + await CursorInputAsync() + " screen=" + await ScreenAsync());
            }
            const string historyMarker = "HISTOK42";
            const string historySeed = "Write-Output " + historyMarker;
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.input(\"Clear-Host\\r\",true)");
            await WaitForCursorInputAsync(text => text.Contains('>', StringComparison.Ordinal) && !text.Contains("WEB_42", StringComparison.Ordinal),
                "Clear-Host did not settle on a fresh prompt.");
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.input(" + JsonSerializer.Serialize(historySeed + "\r") + ",true)");
            await WaitForScreenAsync(text => text.Contains(historyMarker, StringComparison.Ordinal), "PowerShell did not run the history seed command.");
            await WaitForCursorInputAsync(text => text.Contains('>', StringComparison.Ordinal) && !text.Contains(historyMarker, StringComparison.Ordinal),
                "Prompt did not settle after the history seed command.");
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.textarea.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowUp',code:'ArrowUp',keyCode:38,which:38,bubbles:true,cancelable:true}))");
            await WaitForCursorInputAsync(text => text.Contains(historyMarker, StringComparison.Ordinal),
                "Arrow-up did not recall the previous PowerShell command onto the prompt line.");
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.textarea.dispatchEvent(new KeyboardEvent('keydown',{key:'ArrowDown',code:'ArrowDown',keyCode:40,which:40,bubbles:true,cancelable:true}))");
            await WaitForCursorInputAsync(text => text.Contains('>', StringComparison.Ordinal) && !text.Contains(historyMarker, StringComparison.Ordinal),
                "Arrow-down did not restore the empty prompt.");
            panel.FocusTerminal();
            await UntilTerminalFocusedAsync(browser);
            System.Windows.Clipboard.SetDataObject("RIGHT_CLICK_PASTE_OK", true);
            await browser.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('#terminals .session.active').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,button:2}))");
            await WaitForCursorInputAsync(text => text.Contains("RIGHT_CLICK_PASTE_OK", StringComparison.Ordinal),
                "Right-click did not paste clipboard text onto the prompt line.");
            System.Windows.Clipboard.SetDataObject("KEYBOARD_PASTE_OK", true);
            await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.textarea.dispatchEvent(new KeyboardEvent('keydown',{key:'V',code:'KeyV',ctrlKey:true,shiftKey:true,bubbles:true,cancelable:true}))");
            await WaitForCursorInputAsync(text => text.Contains("KEYBOARD_PASTE_OK", StringComparison.Ordinal),
                "Ctrl+Shift+V did not paste through the host clipboard.");
            await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('.xterm-helper-textarea').dispatchEvent(new KeyboardEvent('keydown',{key:'j',code:'KeyJ',ctrlKey:true,bubbles:true}))");
            await Until(() => shortcuts.Contains("panel"));
            var savePath = Path.Combine(root, "terminal-save.txt");
            if (File.Exists(savePath)) File.Delete(savePath);
            panel.ResolveSavePath = _ => savePath;
            await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('save').click()");
            await Until(() => File.Exists(savePath) || errors.Count > 0);
            Check(errors.Count == 0, "Save terminal output failed: " + string.Join("; ", errors));
            Check(File.Exists(savePath), "Save button did not write a transcript file");
            var savedText = await File.ReadAllTextAsync(savePath);
            Check(savedText.Contains("KEYBOARD_PASTE_OK", StringComparison.Ordinal) ||
                  savedText.Contains(historyMarker, StringComparison.Ordinal) ||
                  savedText.Contains("RIGHT_CLICK_PASTE_OK", StringComparison.Ordinal),
                "Saved transcript did not include recent terminal output. " + savedText);

            async Task<string> SaveAndReadAsync(string path)
            {
                if (File.Exists(path)) File.Delete(path);
                panel.ResolveSavePath = _ => path;
                await browser.CoreWebView2.ExecuteScriptAsync("document.getElementById('save').click()");
                await Until(() => File.Exists(path) || errors.Count > 0);
                Check(errors.Count == 0, "Save terminal output failed: " + string.Join("; ", errors));
                return await File.ReadAllTextAsync(path);
            }

            const string wrapMarker = "WRAPMARKER";
            var wrapPayload = wrapMarker + new string('X', 200);
            await browser.CoreWebView2.ExecuteScriptAsync(
                "(()=>new Promise(r=>sessions.get(active).terminal.write(" +
                JsonSerializer.Serialize("\r\n" + wrapPayload + "\r\n") + ",r)))()");
            await WaitForScreenAsync(text => text.Contains(wrapMarker, StringComparison.Ordinal),
                "Wrapped save marker did not render.");
            var wrapPath = Path.Combine(root, "terminal-save-wrap.txt");
            var wrapSaved = (await SaveAndReadAsync(wrapPath)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Check(wrapSaved.Contains(wrapPayload, StringComparison.Ordinal),
                "Save split a soft-wrapped line. " + wrapSaved);

            await browser.CoreWebView2.ExecuteScriptAsync(
                "(()=>new Promise(r=>sessions.get(active).terminal.write('\\r\\nNORMAL_SCROLL_SAVE\\r\\n',()=>" +
                "sessions.get(active).terminal.write('\\x1b[?1049h',()=>" +
                "sessions.get(active).terminal.write('ALT_SCREEN_SAVE\\r\\n',r)))))()");
            timer.Restart();
            while (true)
            {
                var typeJson = await browser.CoreWebView2.ExecuteScriptAsync("sessions.get(active).terminal.buffer.active.type");
                if ((JsonSerializer.Deserialize<string>(typeJson) ?? "") == "alternate") break;
                if (timer.Elapsed > TimeSpan.FromSeconds(15))
                    throw new TimeoutException("Alternate screen did not become active.");
                await Task.Delay(100);
            }
            var altPath = Path.Combine(root, "terminal-save-alt.txt");
            var altSaved = await SaveAndReadAsync(altPath);
            Check(altSaved.Contains("NORMAL_SCROLL_SAVE", StringComparison.Ordinal),
                "Save while on alternate screen omitted normal scrollback. " + altSaved);
            Check(altSaved.Contains("ALT_SCREEN_SAVE", StringComparison.Ordinal),
                "Save while on alternate screen omitted alternate contents. " + altSaved);
            await browser.CoreWebView2.ExecuteScriptAsync(
                "(()=>new Promise(r=>sessions.get(active).terminal.write('\\x1b[?1049l',r)))()");
            await panel.AddSessionAsync(); await Until(() => panel.SessionCount == 3);
            var panelCwd = Path.Combine(root, "panel-cwd");
            Directory.CreateDirectory(panelCwd);
            await panel.AddSessionAsync(panelCwd);
            await Until(() => panel.SessionCount == 4);
            window.Hide(); await Task.Delay(250); window.Show(); panel.Fit();
            Check(panel.SessionCount == 4, "Hiding panel lost terminal sessions");
            await browser.CoreWebView2.ExecuteScriptAsync("send({type:'close',id:active})");
            await Until(() => panel.SessionCount == 3);
            Check(errors.Count == 0, "Terminal bridge reported errors: " + string.Join("; ", errors));
        }
        finally { await panel.DisposeAsync(); window.Close(); }
        Console.WriteLine("WebView2/xterm rendering, history arrows, right-click paste, Save transcript (wrap + alternate), interactive input, multiple sessions (including custom cwd) and hide/show passed.");
    }
}
