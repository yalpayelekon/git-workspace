using System.Collections.Concurrent;
using System.IO;

namespace GitWorkspace.Lsp;

public sealed class WorkspaceWatcher : IDisposable
{
    // Created = 1, Changed = 2, Deleted = 3 (LSP FileChangeType)
    public readonly record struct Change(string Path, int Type);

    private readonly string root;
    private readonly FileSystemWatcher watcher;
    private readonly ConcurrentDictionary<string, int> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object flushGate = new();
    private CancellationTokenSource? debounce;
    private bool disposed;

    public event Action<IReadOnlyList<Change>>? Changed;

    public WorkspaceWatcher(string root)
    {
        this.root = Path.GetFullPath(root);
        watcher = new FileSystemWatcher(this.root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };
        watcher.Created += (_, e) => Queue(e.FullPath, 1);
        watcher.Changed += (_, e) => Queue(e.FullPath, 2);
        watcher.Deleted += (_, e) => Queue(e.FullPath, 3);
        watcher.Renamed += (_, e) =>
        {
            Queue(e.OldFullPath, 3);
            Queue(e.FullPath, 1);
        };
        watcher.Error += (_, e) =>
        {
            // Buffer overflow: force a broad refresh signal via root markers.
            Queue(Path.Combine(this.root, "pubspec.yaml"), 2);
            Queue(Path.Combine(this.root, "go.mod"), 2);
            Queue(Path.Combine(this.root, "go", "go.mod"), 2);
            Queue(Path.Combine(this.root, "pyproject.toml"), 2);
            Queue(Path.Combine(this.root, "requirements.txt"), 2);
            _ = e;
        };
    }

    public void Start() => watcher.EnableRaisingEvents = true;

    private void Queue(string path, int type)
    {
        if (disposed || LspPaths.IsIgnored(root, path) || !LspPaths.IsWatched(path)) return;
        pending[Path.GetFullPath(path)] = type;
        lock (flushGate)
        {
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = new CancellationTokenSource();
            var token = debounce.Token;
            _ = FlushLaterAsync(token);
        }
    }

    private async Task FlushLaterAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(400, token).ConfigureAwait(false);
            // Drain with TryRemove so a concurrent Queue() cannot be wiped by Clear().
            var batch = new List<Change>();
            foreach (var key in pending.Keys.ToArray())
                if (pending.TryRemove(key, out var type))
                    batch.Add(new Change(key, type));
            if (batch.Count > 0) Changed?.Invoke(batch);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        lock (flushGate)
        {
            debounce?.Cancel();
            debounce?.Dispose();
            debounce = null;
        }
        pending.Clear();
    }
}
