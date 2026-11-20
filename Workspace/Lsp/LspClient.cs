using System.Collections.Concurrent;
using System.IO;
using System.Text.Json.Nodes;
using GitWorkspace.Core;

namespace GitWorkspace.Lsp;

public sealed class LspClient : IAsyncDisposable
{
    private readonly JsonRpcConnection connection;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pending = new();
    private readonly string source;
    private readonly string rootUri;
    private int nextId;
    private bool disposed;

    public event Action<string, JsonObject>? Notification;
    public event Action<string>? Log;
    public event Action<Exception>? Faulted;

    public LspClient(JsonRpcConnection connection, string source, string root)
    {
        this.connection = connection;
        this.source = source;
        rootUri = LspPaths.PathToUri(root);
        connection.Message += OnMessage;
        connection.Faulted += ex => Faulted?.Invoke(ex);
    }

    public void Start() => connection.Start();

    public async Task InitializeAsync(string root, CancellationToken cancellation = default)
    {
        var rootUri = this.rootUri;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["processId"] = Environment.ProcessId,
            ["clientInfo"] = new JsonObject { ["name"] = AppInfo.ProductName, ["version"] = "1.0" },
            ["rootUri"] = rootUri,
            ["rootPath"] = root,
            ["capabilities"] = new JsonObject
            {
                ["textDocument"] = new JsonObject
                {
                    ["synchronization"] = new JsonObject
                    {
                        ["didSave"] = true,
                        ["willSave"] = false,
                        ["willSaveWaitUntil"] = false
                    },
                    ["publishDiagnostics"] = new JsonObject
                    {
                        ["relatedInformation"] = false,
                        ["versionSupport"] = false,
                        ["codeDescriptionSupport"] = true
                    }
                },
                ["workspace"] = new JsonObject
                {
                    ["applyEdit"] = false,
                    ["workspaceFolders"] = true,
                    ["configuration"] = true,
                    ["didChangeWatchedFiles"] = new JsonObject
                    {
                        ["dynamicRegistration"] = true,
                        ["relativePatternSupport"] = false
                    }
                }
            },
            ["workspaceFolders"] = new JsonArray
            {
                new JsonObject { ["uri"] = rootUri, ["name"] = Path.GetFileName(root.TrimEnd('\\', '/')) }
            },
            ["initializationOptions"] = new JsonObject()
        }, timeout.Token).ConfigureAwait(false);
        _ = result;
        await NotifyAsync("initialized", new JsonObject()).ConfigureAwait(false);
    }

    public Task NotifyAsync(string method, JsonObject? parameters = null) =>
        connection.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        });

    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters = null, CancellationToken cancellation = default)
    {
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        using var registration = cancellation.Register(() =>
        {
            if (pending.TryRemove(id, out var source)) source.TrySetCanceled(cancellation);
        });
        await connection.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        }, cancellation).ConfigureAwait(false);
        return await completion.Task.ConfigureAwait(false);
    }

    public Task DidOpenAsync(string path, string text, string languageId, int version = 1) =>
        NotifyAsync("textDocument/didOpen", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = LspPaths.PathToUri(path),
                ["languageId"] = languageId,
                ["version"] = version,
                ["text"] = text
            }
        });

    public Task DidCloseAsync(string path) =>
        NotifyAsync("textDocument/didClose", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspPaths.PathToUri(path) }
        });

    public Task DidChangeAsync(string path, string text, int version) =>
        NotifyAsync("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["uri"] = LspPaths.PathToUri(path),
                ["version"] = version
            },
            ["contentChanges"] = new JsonArray
            {
                new JsonObject { ["text"] = text }
            }
        });

    public Task DidSaveAsync(string path, string text) =>
        NotifyAsync("textDocument/didSave", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = LspPaths.PathToUri(path) },
            ["text"] = text
        });

    public Task DidChangeWatchedFilesAsync(IEnumerable<(string Path, int Type)> changes) =>
        NotifyAsync("workspace/didChangeWatchedFiles", new JsonObject
        {
            ["changes"] = new JsonArray(changes.Select(change => (JsonNode)new JsonObject
            {
                ["uri"] = LspPaths.PathToUri(change.Path),
                ["type"] = change.Type
            }).ToArray())
        });

    public async Task ShutdownAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await RequestAsync("shutdown", cancellation: timeout.Token).ConfigureAwait(false);
            await NotifyAsync("exit").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log?.Invoke($"{source}: shutdown skipped ({ex.Message})");
        }
    }

    private void OnMessage(JsonObject message)
    {
        try
        {
            if (message.TryGetPropertyValue("id", out var idNode) && message.ContainsKey("method"))
            {
                _ = HandleServerRequestAsync(message);
                return;
            }
            if (message.TryGetPropertyValue("id", out idNode) && idNode != null)
            {
                var id = idNode.GetValue<int>();
                if (!pending.TryRemove(id, out var completion)) return;
                if (message.TryGetPropertyValue("error", out var error) && error != null)
                    completion.TrySetException(new InvalidOperationException($"{source} LSP error: {error}"));
                else
                    completion.TrySetResult(message.TryGetPropertyValue("result", out var result) ? result : null);
                return;
            }
            if (message.TryGetPropertyValue("method", out var methodNode) && methodNode != null)
            {
                var method = methodNode.GetValue<string>();
                var parameters = message.TryGetPropertyValue("params", out var p) && p is JsonObject obj ? obj : new JsonObject();
                if (method is "window/logMessage" or "window/showMessage")
                {
                    var text = parameters["message"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text)) Log?.Invoke($"{source}: {text}");
                }
                Notification?.Invoke(method, parameters);
            }
        }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    private async Task HandleServerRequestAsync(JsonObject message)
    {
        var id = message["id"]!.DeepClone();
        var method = message["method"]!.GetValue<string>();
        JsonNode? result = method switch
        {
            "workspace/configuration" => new JsonArray(new JsonObject()),
            "workspace/workspaceFolders" => new JsonArray
            {
                new JsonObject
                {
                    ["uri"] = rootUri,
                    ["name"] = Path.GetFileName(UriToLocalName(rootUri))
                }
            },
            "client/registerCapability" => null,
            "client/unregisterCapability" => null,
            "window/workDoneProgress/create" => null,
            "window/showMessageRequest" => null,
            _ => null
        };
        if (method is not ("workspace/configuration" or "workspace/workspaceFolders" or "client/registerCapability"
            or "client/unregisterCapability" or "window/workDoneProgress/create" or "window/showMessageRequest"))
            Log?.Invoke($"{source}: ignored server request {method}");
        await connection.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        foreach (var item in pending)
            item.Value.TrySetCanceled();
        pending.Clear();
        connection.Message -= OnMessage;
        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static string UriToLocalName(string uri) =>
        LspPaths.UriToPath(uri) is { } path ? path : uri;
}
