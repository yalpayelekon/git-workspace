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
    private LspProcessHost? typescript;
    private LspProcessHost? rust;
    private LspProcessHost? csharp;
    private LspProcessHost? php;
    private LspProcessHost? cpp;
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

        if (profile.HasTypeScript)
        {
            var server = LspProcessHost.FindTypeScriptLanguageServer();
            if (server != null)
            {
                typescript = new LspProcessHost("TypeScript", server.Value.Executable, server.Value.Arguments, root, Store);
                Wire(typescript);
                try { await typescript.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("TypeScript: " + ex.Message); Log?.Invoke("TypeScript LSP start failed: " + ex.Message); typescript = null; }
            }
            else errors.Add("typescript-language-server not found");
        }

        if (profile.HasRust)
        {
            var analyzer = LspProcessHost.FindRustAnalyzer();
            if (analyzer != null)
            {
                rust = new LspProcessHost("Rust", analyzer, [], root, Store);
                Wire(rust);
                try { await rust.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("Rust: " + ex.Message); Log?.Invoke("rust-analyzer start failed: " + ex.Message); rust = null; }
            }
            else errors.Add("rust-analyzer not found");
        }

        if (profile.HasCSharp)
        {
            var server = LspProcessHost.FindCSharpLanguageServer();
            if (server != null)
            {
                csharp = new LspProcessHost("C#", server.Value.Executable, server.Value.Arguments, root, Store);
                Wire(csharp);
                try { await csharp.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("C#: " + ex.Message); Log?.Invoke("C# LSP start failed: " + ex.Message); csharp = null; }
            }
            else errors.Add("C# language server not found (csharp-ls/OmniSharp)");
        }

        if (profile.HasPhp)
        {
            var server = LspProcessHost.FindPhpLanguageServer();
            if (server != null)
            {
                php = new LspProcessHost("PHP", server.Value.Executable, server.Value.Arguments, root, Store);
                Wire(php);
                try { await php.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("PHP: " + ex.Message); Log?.Invoke("PHP LSP start failed: " + ex.Message); php = null; }
            }
            else errors.Add("PHP language server not found (intelephense/phpactor)");
        }

        if (profile.HasCpp)
        {
            var clangd = LspProcessHost.FindClangd();
            if (clangd != null)
            {
                cpp = new LspProcessHost("C/C++", clangd, [], root, Store);
                Wire(cpp);
                try { await cpp.StartAsync(root, cancellation).ConfigureAwait(false); }
                catch (Exception ex) { errors.Add("C/C++: " + ex.Message); Log?.Invoke("clangd start failed: " + ex.Message); cpp = null; }
            }
            else errors.Add("clangd not found");
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
        var host = HostFor(language);
        if (host != null) await host.OpenDocumentAsync(path, preview.Text, language).ConfigureAwait(false);
    }

    public async Task CloseDocumentAsync(string relativeOrAbsolutePath)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        openDocuments.TryRemove(path, out _);
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        var host = HostFor(language);
        if (host != null) await host.CloseDocumentAsync(path).ConfigureAwait(false);
    }

    public async Task ChangeDocumentAsync(string relativeOrAbsolutePath, string text)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        openDocuments[path] = 0;
        var host = HostFor(language);
        if (host != null) await host.ChangeDocumentAsync(path, text, language).ConfigureAwait(false);
    }

    public async Task SaveDocumentAsync(string relativeOrAbsolutePath, string text)
    {
        var path = Path.GetFullPath(Path.IsPathRooted(relativeOrAbsolutePath)
            ? relativeOrAbsolutePath
            : Path.Combine(root, relativeOrAbsolutePath));
        var language = LspPaths.LanguageId(path);
        if (language == null) return;
        openDocuments[path] = 0;
        var host = HostFor(language);
        if (host != null) await host.SaveDocumentAsync(path, text, language).ConfigureAwait(false);
    }

    private LspProcessHost? HostFor(string language) => language switch
    {
        "dart" => dart,
        "go" => go,
        "python" => python,
        "typescript" or "javascript" or "typescriptreact" or "javascriptreact" => typescript,
        "rust" => rust,
        "csharp" => csharp,
        "php" => php,
        "c" or "cpp" => cpp,
        _ => null
    };

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
            if (typescript != null) await typescript.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (rust != null) await rust.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (csharp != null) await csharp.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (php != null) await php.NotifyWatchedAsync(payload).ConfigureAwait(false);
            if (cpp != null) await cpp.NotifyWatchedAsync(payload).ConfigureAwait(false);
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
        if (typescript != null) { await typescript.DisposeAsync().ConfigureAwait(false); typescript = null; }
        if (rust != null) { await rust.DisposeAsync().ConfigureAwait(false); rust = null; }
        if (csharp != null) { await csharp.DisposeAsync().ConfigureAwait(false); csharp = null; }
        if (php != null) { await php.DisposeAsync().ConfigureAwait(false); php = null; }
        if (cpp != null) { await cpp.DisposeAsync().ConfigureAwait(false); cpp = null; }
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
