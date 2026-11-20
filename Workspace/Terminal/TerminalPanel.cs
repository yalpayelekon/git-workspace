using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using GitWorkspace.Core;

namespace GitWorkspace.Terminal;

public sealed class TerminalPanel : Grid, IAsyncDisposable
{
    private static readonly string Origin = AppInfo.TerminalOrigin;
    private readonly WebView2 browser = new();
    private readonly string root;
    private readonly Dictionary<int, PseudoTerminal> sessions = [];
    private readonly Dictionary<int, (int SessionId, TaskCompletionSource Completion)> pending = [];
    private readonly Queue<string?> startupDirectories = [];
    private readonly CancellationTokenSource lifetime = new();
    private int nextSession, nextMessage;
    private bool ready, initializing;
    public int SessionCount => sessions.Count;
    /// <summary>Test hook: return a path to write, or null to cancel. When null, shows SaveFileDialog.</summary>
    public Func<string?, string?>? ResolveSavePath { get; set; }
    public event Action<string>? Error;
    public event Action<string>? Shortcut;

    public TerminalPanel(string root) { this.root = root; Children.Add(browser); }
    public async Task InitializeAsync()
    {
        if (initializing) return;
        initializing = true;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, AppPaths.WebView2UserData);
            await browser.EnsureCoreWebView2Async(environment);
            var core = browser.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(AppInfo.TerminalHostName, Path.Combine(AppContext.BaseDirectory, "Assets", "terminal"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.NavigationStarting += (_, e) => e.Cancel = e.Uri != Origin + "index.html";
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => e.State = e.Uri.StartsWith(Origin, StringComparison.Ordinal) &&
                e.PermissionKind == CoreWebView2PermissionKind.ClipboardRead && e.IsUserInitiated
                ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            core.WebResourceRequested += (_, e) =>
            {
                if (!e.Request.Uri.StartsWith(Origin, StringComparison.Ordinal))
                    e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Blocked", "");
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebMessageReceived += Receive;
            core.ProcessFailed += (_, _) => Error?.Invoke("Terminal renderer stopped. Reopen the app to restore terminal sessions.");
            core.Navigate(Origin + "index.html");
        }
        catch
        {
            startupDirectories.Clear();
            initializing = false;
            throw;
        }
    }

    private async void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (e.Source != Origin + "index.html" || lifetime.IsCancellationRequested) return;
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var message = document.RootElement;
            var type = message.GetProperty("type").GetString();
            if (type == "ready")
            {
                if (ready) return;
                ready = true;
                if (startupDirectories.Count == 0) startupDirectories.Enqueue(null);
                while (!lifetime.IsCancellationRequested && startupDirectories.TryDequeue(out var directory))
                {
                    try { await AddSessionAsync(directory); }
                    catch (Exception ex) { if (!lifetime.IsCancellationRequested) Error?.Invoke(ex.Message); }
                }
                return;
            }
            if (type == "new") { await AddSessionAsync(); return; }
            if (type == "save")
            {
                var text = message.GetProperty("data").GetString() ?? "";
                var title = message.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                await Dispatcher.InvokeAsync(() => SaveTranscript(text, title));
                return;
            }
            if (type == "shortcut") { Shortcut?.Invoke(message.GetProperty("key").GetString() ?? ""); return; }
            if (type == "ack")
            {
                if (pending.Remove(message.GetProperty("messageId").GetInt32(), out var completion)) completion.Completion.TrySetResult();
                return;
            }
            var id = message.GetProperty("id").GetInt32();
            if (!sessions.TryGetValue(id, out var session)) return;
            if (type == "input")
            {
                var data = message.GetProperty("data").GetString() ?? "";
                if (data.Length <= 65536) await session.WriteAsync(data);
            }
            else if (type == "paste-request")
            {
                // WebView clipboard APIs can fail; fall back to the WPF clipboard for right-click / Ctrl+Shift+V.
                var text = "";
                try
                {
                    text = await Dispatcher.InvokeAsync(() =>
                    {
                        try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                        catch (Exception) { return ""; }
                    });
                }
                catch (Exception) { text = ""; }
                if (text.Length > 65536) text = text[..65536];
                if (text.Length > 0) Send(new { type = "paste", id, data = text });
            }
            else if (type == "resize") session.Resize(message.GetProperty("columns").GetInt32(), message.GetProperty("rows").GetInt32());
            else if (type == "close")
            {
                sessions.Remove(id);
                foreach (var entry in pending.Where(p => p.Value.SessionId == id).ToArray())
                {
                    pending.Remove(entry.Key); entry.Value.Completion.TrySetCanceled();
                }
                Send(new { type = "closed", id });
                await session.DisposeAsync();
            }
        }
        catch (Exception ex) when (!lifetime.IsCancellationRequested) { Error?.Invoke(ex.Message); }
    }

    public async Task AddSessionAsync(string? workingDirectory = null)
    {
        if (lifetime.IsCancellationRequested) return;
        if (!ready)
        {
            startupDirectories.Enqueue(workingDirectory);
            await InitializeAsync();
            return;
        }
        var cwd = Directory.Exists(workingDirectory) ? workingDirectory! : root;
        var id = ++nextSession;
        // Prefer a user-installed pwsh; Windows PowerShell remains available on supported Windows.
        var shell = Processes.Find("pwsh.exe") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var terminal = await Task.Run(() => new PseudoTerminal(shell, cwd));
        if (lifetime.IsCancellationRequested) { await terminal.DisposeAsync(); return; }
        sessions.Add(id, terminal);
        var title = Path.GetFileName(cwd.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(title) || string.Equals(Path.GetFullPath(cwd), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
            title = $"PowerShell {id}";
        else title = $"{title} · PowerShell {id}";
        Send(new { type = "created", id, title });
        _ = PumpAsync(id, terminal);
    }

    private async Task PumpAsync(int id, PseudoTerminal terminal)
    {
        try
        {
            await foreach (var data in terminal.Output.ReadAllAsync(lifetime.Token))
            {
                if (!sessions.ContainsKey(id)) break;
                var messageId = ++nextMessage;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                pending.Add(messageId, (id, completion));
                Send(new { type = "output", id, data, messageId });
                // The bounded ConPTY queue applies backpressure until xterm has rendered this chunk.
                await completion.Task.WaitAsync(lifetime.Token);
            }
            if (sessions.ContainsKey(id)) Send(new { type = "exit", id });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!lifetime.IsCancellationRequested) Error?.Invoke(ex.Message); }
    }
    private void Send(object message)
    {
        if (!lifetime.IsCancellationRequested && browser.CoreWebView2?.Source == Origin + "index.html")
            browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }
    private void SaveTranscript(string text, string? title)
    {
        if (lifetime.IsCancellationRequested) return;
        var path = ResolveSavePath?.Invoke(title);
        if (ResolveSavePath == null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save terminal output",
                Filter = "Text files (*.txt)|*.txt|Log files (*.log)|*.log|All files (*.*)|*.*",
                DefaultExt = ".txt",
                AddExtension = true,
                FileName = $"{SanitizeFileName(title)}_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };
            if (dialog.ShowDialog() != true) return;
            path = dialog.FileName;
        }
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) { Error?.Invoke($"Could not save terminal output: {ex.Message}"); }
    }

    private static string SanitizeFileName(string? title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "terminal" : title.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        name = name.Replace('·', '-').Replace(' ', '_');
        while (name.Contains("__", StringComparison.Ordinal)) name = name.Replace("__", "_", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(name) ? "terminal" : name.Trim('_');
    }

    public void Fit() { if (ready) Send(new { type = "fit" }); }
    public void FocusTerminal()
    {
        if (lifetime.IsCancellationRequested || !browser.IsVisible || browser.CoreWebView2 == null) return;
        // WPF's logical focus can already name this control while its native HWND has lost
        // focus to a TabItem. TabInto explicitly transfers keyboard input into WebView2.
        browser.Focus();
        Keyboard.Focus(browser);
        // The toolbar precedes the active xterm textarea in DOM tab order. Enter at the
        // last stop so WebView2's native tab traversal cannot leave focus on a session button.
        ((IKeyboardInputSink)browser).TabInto(new TraversalRequest(FocusNavigationDirection.Last));
        if (ready) Send(new { type = "focus" });
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel();
        startupDirectories.Clear();
        foreach (var completion in pending.Values) completion.Completion.TrySetCanceled();
        pending.Clear();
        var active = sessions.Values.ToArray(); sessions.Clear();
        await Task.WhenAll(active.Select(async s => await s.DisposeAsync()));
        browser.Dispose(); lifetime.Dispose();
    }
}
