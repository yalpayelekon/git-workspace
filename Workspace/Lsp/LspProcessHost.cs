using System.Diagnostics;
using System.IO;
using System.Text;
using GitWorkspace.Core;

namespace GitWorkspace.Lsp;

public sealed class LspProcessHost : IAsyncDisposable
{
    private readonly string source;
    private readonly string executable;
    private readonly string[] arguments;
    private readonly string workingDirectory;
    private Process? process;
    private LspClient? client;
    private bool restarted;
    private bool stopping;
    private bool disposed;

    public DiagnosticStore Store { get; }
    public event Action<string>? Log;
    public event Action? DiagnosticsChanged;
    public bool IsRunning => process is { HasExited: false };

    public LspProcessHost(string source, string executable, string[] arguments, string workingDirectory, DiagnosticStore store)
    {
        this.source = source;
        this.executable = executable;
        this.arguments = arguments;
        this.workingDirectory = workingDirectory;
        Store = store;
    }

    public async Task StartAsync(string root, CancellationToken cancellation = default, bool resetRestartBudget = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (resetRestartBudget) restarted = false;
        await StopAsync().ConfigureAwait(false);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        process = new Process { StartInfo = start, EnableRaisingEvents = true };
        if (!process.Start()) throw new IOException($"Unable to start {source} language server ({Path.GetFileName(executable)}).");
        _ = DrainErrorsAsync(process.StandardError);
        var connection = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        client = new LspClient(connection, source, root);
        client.Log += message => Log?.Invoke(message);
        client.Faulted += ex =>
        {
            Log?.Invoke($"{source}: connection fault: {ex.Message}");
            _ = TryRestartAsync(root);
        };
        client.Notification += (method, parameters) =>
        {
            if (method == "textDocument/publishDiagnostics")
                ApplyDiagnostics(parameters);
        };
        process.Exited += (_, _) =>
        {
            if (!disposed && !stopping) _ = TryRestartAsync(root);
        };
        client.Start();
        await client.InitializeAsync(root, cancellation).ConfigureAwait(false);
        ready = true;
        Log?.Invoke($"{source}: language server ready");
    }

    private readonly HashSet<string> open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> versions = new(StringComparer.OrdinalIgnoreCase);

    private bool ready;

    public async Task OpenDocumentAsync(string path, string text, string languageId)
    {
        if (client == null || !ready) return;
        var full = Path.GetFullPath(path);
        if (open.Contains(full))
        {
            versions[full] = versions.GetValueOrDefault(full) + 1;
            await client.DidChangeAsync(full, text, versions[full]).ConfigureAwait(false);
            return;
        }
        open.Add(full);
        versions[full] = 1;
        await client.DidOpenAsync(full, text, languageId, 1).ConfigureAwait(false);
    }

    public async Task CloseDocumentAsync(string path)
    {
        if (client == null || !ready) return;
        var full = Path.GetFullPath(path);
        open.Remove(full);
        versions.Remove(full);
        await client.DidCloseAsync(full).ConfigureAwait(false);
    }

    public async Task ChangeDocumentAsync(string path, string text, string languageId)
    {
        if (client == null || !ready) return;
        var full = Path.GetFullPath(path);
        if (!open.Contains(full))
        {
            await OpenDocumentAsync(full, text, languageId).ConfigureAwait(false);
            return;
        }
        versions[full] = versions.GetValueOrDefault(full) + 1;
        await client.DidChangeAsync(full, text, versions[full]).ConfigureAwait(false);
    }

    public async Task SaveDocumentAsync(string path, string text, string languageId)
    {
        if (client == null || !ready) return;
        var full = Path.GetFullPath(path);
        if (!open.Contains(full))
            await OpenDocumentAsync(full, text, languageId).ConfigureAwait(false);
        else
        {
            versions[full] = versions.GetValueOrDefault(full) + 1;
            await client.DidChangeAsync(full, text, versions[full]).ConfigureAwait(false);
        }
        await client.DidSaveAsync(full, text).ConfigureAwait(false);
    }

    public async Task NotifyWatchedAsync(IEnumerable<(string Path, int Type)> changes)
    {
        if (client == null || !ready) return;
        await client.DidChangeWatchedFilesAsync(changes).ConfigureAwait(false);
    }

    private void ApplyDiagnostics(System.Text.Json.Nodes.JsonObject parameters)
    {
        var uri = parameters["uri"]?.GetValue<string>();
        if (uri == null || LspPaths.UriToPath(uri) is not { } path) return;
        var diagnostics = parameters["diagnostics"]?.AsArray() ?? [];
        var problems = new List<Problem>();
        foreach (var item in diagnostics)
        {
            if (item is not System.Text.Json.Nodes.JsonObject diagnostic) continue;
            var range = diagnostic["range"]?.AsObject();
            var start = range?["start"]?.AsObject();
            var line = (start?["line"]?.GetValue<int>() ?? 0) + 1;
            var column = (start?["character"]?.GetValue<int>() ?? 0) + 1;
            var severity = (diagnostic["severity"]?.GetValue<int>() ?? 1) switch
            {
                1 => "error",
                2 => "warning",
                3 => "info",
                _ => "hint"
            };
            var message = diagnostic["message"]?.GetValue<string>() ?? "";
            var code = diagnostic["code"]?.ToString()?.Trim('"') ?? "";
            problems.Add(new Problem(source, severity, path, line, column, message, code));
        }
        Store.Replace(source, path, problems);
        DiagnosticsChanged?.Invoke();
    }

    private async Task TryRestartAsync(string root)
    {
        if (disposed || stopping || restarted) return;
        restarted = true;
        Log?.Invoke($"{source}: restarting language server once after exit");
        try { await StartAsync(root, resetRestartBudget: false).ConfigureAwait(false); }
        catch (Exception ex) { Log?.Invoke($"{source}: restart failed: {ex.Message}"); }
    }

    private async Task DrainErrorsAsync(StreamReader reader)
    {
        try
        {
            while (process is { HasExited: false })
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (!string.IsNullOrWhiteSpace(line)) Log?.Invoke($"{source} stderr: {line}");
            }
        }
        catch (Exception) { }
    }

    public async Task StopAsync()
    {
        stopping = true;
        ready = false;
        open.Clear();
        versions.Clear();
        Store.ClearSource(source);
        var active = process;
        if (client != null)
        {
            try
            {
                var shutdown = client.ShutdownAsync();
                var completed = await Task.WhenAny(shutdown, Task.Delay(1500)).ConfigureAwait(false);
                if (completed != shutdown) Log?.Invoke($"{source}: shutdown timed out; killing process");
            }
            catch (Exception) { }
            await client.DisposeAsync().ConfigureAwait(false);
            client = null;
        }
        if (active != null)
        {
            try
            {
                if (!active.HasExited) active.Kill(entireProcessTree: true);
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await active.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            catch (Exception) { }
            active.Dispose();
            if (ReferenceEquals(process, active)) process = null;
        }
        stopping = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    public static string? FindDart()
    {
        var dart = Processes.Find("dart") ?? Processes.Find("dart.exe");
        if (dart != null) return dart;
        foreach (var flutter in new[]
                 {
                     Processes.Find("flutter"),
                     Processes.Find("flutter.bat"),
                     @"C:\src\flutter\bin\flutter.bat",
                     @"C:\flutter\bin\flutter.bat"
                 }.Where(f => f != null && File.Exists(f)))
        {
            var candidate = Path.Combine(Path.GetDirectoryName(flutter)!, "cache", "dart-sdk", "bin", "dart.exe");
            if (File.Exists(candidate)) return candidate;
        }
        foreach (var candidate in new[]
                 {
                     @"C:\src\flutter\bin\cache\dart-sdk\bin\dart.exe",
                     @"C:\flutter\bin\cache\dart-sdk\bin\dart.exe"
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static string? FindGopls() => Processes.Find("gopls") ?? Processes.Find("gopls.exe");

    /// <summary>Resolve a Python language server: basedpyright, pyright, then pylsp.</summary>
    public static (string Executable, string[] Arguments)? FindPythonLanguageServer()
    {
        foreach (var name in new[] { "basedpyright-langserver", "basedpyright-langserver.exe" })
        {
            var path = FindOnPathOrCommonDirs(name);
            if (path != null) return (path, ["--stdio"]);
        }
        foreach (var name in new[] { "pyright-langserver", "pyright-langserver.exe" })
        {
            var path = FindOnPathOrCommonDirs(name);
            if (path != null) return (path, ["--stdio"]);
        }
        foreach (var name in new[] { "pylsp", "pylsp.exe" })
        {
            var path = FindOnPathOrCommonDirs(name);
            if (path != null) return (path, []);
        }
        return null;
    }

    private static string? FindOnPathOrCommonDirs(string name)
    {
        var found = Processes.Find(name);
        if (found != null) return found;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var candidates = new List<string>
        {
            Path.Combine(roaming, "npm", name),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", name)
        };
        var pythonRoot = Path.Combine(localAppData, "Programs", "Python");
        if (Directory.Exists(pythonRoot))
        {
            try
            {
                foreach (var scripts in Directory.EnumerateDirectories(pythonRoot, "Scripts", SearchOption.AllDirectories).Take(8))
                    candidates.Add(Path.Combine(scripts, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}
