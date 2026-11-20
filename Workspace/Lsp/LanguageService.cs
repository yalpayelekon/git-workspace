using System.Collections.Concurrent;
using System.IO;
using GitWorkspace.Core;

namespace GitWorkspace.Lsp;

public sealed class LanguageService : IAsyncDisposable
{
    private readonly string root;
    private readonly WorkspaceProfile profile;
    private readonly ConcurrentDictionary<string, byte> openDocuments = new(StringComparer.OrdinalIgnoreCase);
    private WorkspaceWatcher? watcher;
    private LspProcessHost? dart;
    private LspProcessHost? go;
    private LspProcessHost? python;
    private bool disposed;

    public DiagnosticStore Store { get; } = new();
    public event Action<string>? Log;
    public event Action? Changed;
    public event Action<IReadOnlyList<WorkspaceWatcher.Change>>? FilesChanged;
    /// <summary>When true for a watched path, skip re-opening the LSP buffer from disk (dirty editors).</summary>
    public Func<string, bool>? ShouldSkipDiskReconcile { get; set; }
    public string Status { get; private set; } = "Language servers not started";

    public LanguageService(string root, WorkspaceProfile? profile = null)
    {
        this.root = Path.GetFullPath(root);
        this.profile = profile ?? WorkspaceProfile.Detect(this.root);
    }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await StopServersAsync().ConfigureAwait(false);
        if (!profile.HasAnyLanguage)
        {
            Status = "No language servers for this repo";
            Changed?.Invoke();
            return;
        }

        Status = "Starting language servers...";
        Changed?.Invoke();
        var errors = new List<string>();

        if (profile.HasDart)
        {
            var dartExe = LspProcessHost.FindDart();
            if (dartExe != null)
            {
                dart = new LspProcessHost("Dart", dartExe, ["language-server", "--protocol=lsp"], root, Store);
                Wire(dart);
                try { await dart.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("Dart: " + ex.Message); Log?.Invoke("Dart start failed: " + ex.Message); dart = null; }
            }
            else errors.Add("Dart SDK not found");
        }

        if (profile.HasGo)
        {
            var gopls = LspProcessHost.FindGopls();
            if (gopls != null)
            {
                var goRoot = profile.GoModuleDirectory ?? root;
                go = new LspProcessHost("Go", gopls, ["serve"], goRoot, Store);
                Wire(go);
                try { await go.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("Go: " + ex.Message); Log?.Invoke("gopls start failed: " + ex.Message); go = null; }
            }
            else errors.Add("gopls not found");
        }

        if (profile.HasPython)
        {
            var server = LspProcessHost.FindPythonLanguageServer();
            if (server != null)
            {
                python = new LspProcessHost("Python", server.Value.Executable, server.Value.Arguments, root, Store);
                Wire(python);
                try { await python.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("Python: " + ex.Message); Log?.Invoke("Python LSP start failed: " + ex.Message); python = null; }
            }
            else errors.Add("Python language server not found (basedpyright/pyright/pylsp)");
        }

        watcher?.Dispose();
        watcher = new WorkspaceWatcher(root);
        watcher.Changed += OnWatched;
        watcher.Start();

        foreach (var path in openDocuments.Keys.ToArray())
            await OpenDocumentAsync(path).ConfigureAwait(false);

        Status = errors.Count == 0
            ? "Live"
            : "Live incomplete · " + string.Join("; ", errors);
        Changed?.Invoke();
    }

    public async Task RestartAsync(CancellationToken cancellation = default) => await StartAsync(cancellation).ConfigureAwait(false);

    public async Task OpenDocumentAsync(string relativeOrAbsolutePath)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        openDocuments[path] = 0;
        var preview = await Files.ReadAsync(path).ConfigureAwait(false);
        if (!preview.IsText) return;
        if (language == "dart" && dart != null) await dart.OpenDocumentAsync(path, preview.Text, language).ConfigureAwait(false);
        if (language == "go" && go != null) await go.OpenDocumentAsync(path, preview.Text, language).ConfigureAwait(false);
        if (language == "python" && python != null) await python.OpenDocumentAsync(path, preview.Text, language).ConfigureAwait(false);
    }

    public async Task CloseDocumentAsync(string relativeOrAbsolutePath)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        openDocuments.TryRemove(path, out _);
        if (LspPaths.LanguageId(path) == "dart" && dart != null) await dart.CloseDocumentAsync(path).ConfigureAwait(false);
        if (LspPaths.LanguageId(path) == "go" && go != null) await go.CloseDocumentAsync(path).ConfigureAwait(false);
        if (LspPaths.LanguageId(path) == "python" && python != null) await python.CloseDocumentAsync(path).ConfigureAwait(false);
    }

    public async Task ChangeDocumentAsync(string relativeOrAbsolutePath, string text)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        openDocuments[path] = 0;
        if (language == "dart" && dart != null) await dart.ChangeDocumentAsync(path, text, language).ConfigureAwait(false);
        if (language == "go" && go != null) await go.ChangeDocumentAsync(path, text, language).ConfigureAwait(false);
        if (language == "python" && python != null) await python.ChangeDocumentAsync(path, text, language).ConfigureAwait(false);
    }

    public async Task SaveDocumentAsync(string relativeOrAbsolutePath, string text)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        openDocuments[path] = 0;
        if (language == "dart" && dart != null) await dart.SaveDocumentAsync(path, text, language).ConfigureAwait(false);
        if (language == "go" && go != null) await go.SaveDocumentAsync(path, text, language).ConfigureAwait(false);
        if (language == "python" && python != null) await python.SaveDocumentAsync(path, text, language).ConfigureAwait(false);
    }

    private void Wire(LspProcessHost host)
    {
        host.Log += message => Log?.Invoke(message);
        host.DiagnosticsChanged += () => Changed?.Invoke();
    }

    private void OnWatched(IReadOnlyList<WorkspaceWatcher.Change> changes)
    {
        FilesChanged?.Invoke(changes);
        _ = NotifyServersAsync(changes);
    }

    private async Task NotifyServersAsync(IReadOnlyList<WorkspaceWatcher.Change> changes)
    {
        try
        {
            var payload = changes.Select(c => (c.Path, c.Type)).ToArray();
            if (dart != null) await dart.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (go != null) await go.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (python != null) await python.NotifyWatchedAsync(payload).ConfigureAwait(false);
            foreach (var change in changes)
            {
                if (change.Type == 3 || !openDocuments.ContainsKey(change.Path)) continue;
                if (ShouldSkipDiskReconcile?.Invoke(change.Path) == true) continue;
                await OpenDocumentAsync(change.Path).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log?.Invoke("Watch notify failed: " + ex.Message); }
    }

    private async Task StopServersAsync()
    {
        if (dart != null) { await dart.DisposeAsync().ConfigureAwait(false); dart = null; }
        if (go != null) { await go.DisposeAsync().ConfigureAwait(false); go = null; }
        if (python != null) { await python.DisposeAsync().ConfigureAwait(false); python = null; }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        watcher?.Dispose();
        watcher = null;
        await StopServersAsync().ConfigureAwait(false);
        Store.Clear();
    }
}
