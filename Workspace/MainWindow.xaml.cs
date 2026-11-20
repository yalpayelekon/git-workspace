using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitWorkspace.Core;
using GitWorkspace.Lsp;
using GitWorkspace.Terminal;

namespace GitWorkspace;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, DiffLineCount> EmptyLineCounts =
        new Dictionary<string, DiffLineCount>(StringComparer.Ordinal);

    private readonly string root;
    private readonly GitService git;
    private readonly LanguageService languages;
    private readonly Func<GitFile, bool, CancellationToken, Task<RevisionDiff>> loadStatusDiff;
    private readonly Func<string, string, bool>? confirmAction;
    private readonly bool rememberLayout;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? analysisCancellation;
    private readonly Dictionary<string, (TabItem Tab, DocumentView View)> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DocumentView, CancellationTokenSource> documentLspDebounce = [];
    private bool showingCliSnapshot;
    private IReadOnlyList<GitFile> status = [];
    private TerminalPanel? terminal;
    private string? selectedExplorerFile, historyPath;
    private int historyCount = 150, refreshGeneration, historyGeneration, detailGeneration;
    private bool gitBusy, refreshing, closing, disposed, sidebarVisible = true, panelVisible = true, historyCollapsed, changesCollapsed;
    private double panelHeight = 240, sidebarWidth = 400;
    private double historyRestoreRatio = 1 / 0.9;
    private readonly WorkspaceProfile profile;
    private readonly string settingsPath = AppPaths.LayoutFile;

    public MainWindow(string root, bool rememberLayout = true,
        Func<GitFile, bool, CancellationToken, Task<RevisionDiff>>? loadStatusDiff = null,
        Func<SearchRequest, CancellationToken, Task<SearchResult>>? runSearch = null,
        Func<string, string, bool>? confirmAction = null)
    {
        this.root = root; profile = WorkspaceProfile.Detect(root);
        git = new(root); languages = new(root, profile); this.rememberLayout = rememberLayout;
        this.loadStatusDiff = loadStatusDiff ?? git.StatusDiffAsync;
        this.runSearch = runSearch ?? new SearchService(root).SearchAsync;
        this.confirmAction = confirmAction;
        InitializeComponent();
        // TabItem content namescopes can leave ElementName LabeledBy bindings unresolved.
        LabelSearchField(SearchQuery, SearchQueryLabel);
        LabelSearchField(SearchInclude, SearchIncludeLabel);
        LabelSearchField(SearchExclude, SearchExcludeLabel);
        HistoryList.IsVisibleChanged += (_, _) => UpdateCommitFilesPanel();
        Title = $"{Path.GetFileName(root)} - {AppInfo.ProductName}";
        if (rememberLayout)
        {
            LoadLayout();
            RecentRepos.Remember(root);
        }
        git.Log += message => Dispatcher.BeginInvoke(() => { if (!disposed) Log(message); });
        languages.Log += message => Dispatcher.BeginInvoke(() => { if (!disposed) Log(message); });
        languages.ShouldSkipDiskReconcile = absolutePath =>
        {
            var relative = Path.GetRelativePath(root, absolutePath).Replace('\\', '/');
            return documents.TryGetValue(relative, out var document) && document.View.IsDirty;
        };
        // After a finished CLI snapshot, the next publishDiagnostics returns to live view.
        languages.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (analysisCancellation != null) return;
            ResumeLiveProblems();
        });
        languages.FilesChanged += changes => _ = Dispatcher.InvokeAsync(() => ReloadChangedDocumentsAsync(changes));
        Loaded += async (_, _) =>
        {
            RefreshTree();
            await RefreshRepositoryAsync(reloadHistory: true);
            // Start LSP after the first Git refresh so Explorer/Source Control stay responsive.
            _ = Guard(() => languages.StartAsync(lifetime.Token));
        };
        Closing += CloseWindow;
        PreviewKeyDown += KeyPressed;
        // Status can drift while unfocused; keep the commit graph and its loaded details intact.
        Activated += (_, _) =>
        {
            if (!IsLoaded || closing || disposed || gitBusy || refreshing) return;
            _ = Guard(() => RefreshRepositoryAsync(reloadHistory: false));
        };
        var welcome = new TabItem
        {
            Header = "Welcome",
            Content = DocumentView.Editor(WelcomeText(profile))
        };
        AttachDocumentTabContextMenu(welcome);
        Documents.Items.Add(welcome);
        AnalyzeButton.ToolTip = AnalyzeTooltip(profile);
        AnalyzeButton.IsEnabled = profile.HasAnyLanguage;
        RestartLspButton.IsEnabled = profile.HasAnyLanguage;
        PopulateRecentReposMenu();
    }

    private void RecentReposMenuOpening(object sender, RoutedEventArgs e) => PopulateRecentReposMenu();

    private void PopulateRecentReposMenu()
    {
        RecentReposMenu.Items.Clear();
        var recent = RecentRepos.Load();
        if (recent.Count == 0)
        {
            RecentReposMenu.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var path in recent)
        {
            var item = new MenuItem { Header = path, Tag = path };
            item.Click += RecentRepositoryClick;
            item.IsEnabled = !string.Equals(path, root, StringComparison.OrdinalIgnoreCase);
            RecentReposMenu.Items.Add(item);
        }
    }

    private async void RecentRepositoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path }) return;
        await OpenRepositoryAsync(path);
    }

    private static string WelcomeText(WorkspaceProfile profile)
    {
        var problems = profile.HasAnyLanguage
            ? "Problems: language servers start for detected stacks (Dart, Go, Python, TypeScript/JS, Rust, C#, PHP, C/C++). Use Restart language servers if a tool was missing at startup; CLI snapshot runs the matching analyzer for each detected stack."
            : "Problems: no supported project markers detected, so language servers and CLI analyze stay off.";
        return $"{AppInfo.ProductName}\n\nExplorer: browse files and edit text in the File tab (Ctrl+S saves, Ctrl+Shift+R reverts).\nSearch: find text across the repository with case / word / regex and include/exclude globs.\nSource Control: stage, commit, pull, push and inspect history.\n{problems}\nTerminal: open interactive PowerShell sessions.\n\nCtrl+B  sidebar\nCtrl+J  bottom panel\nCtrl+`  terminal\nCtrl+Shift+F  search\nCtrl+Shift+M  problems\nCtrl+S  save file\nCtrl+Shift+R  revert file\nF5  refresh Git\n\nFile → Open Repository… switches checkouts. Language servers start with the window when applicable; terminals still start when opened.";
    }

    private static string AnalyzeTooltip(WorkspaceProfile profile)
    {
        var parts = new List<string>();
        if (profile.HasDart) parts.Add("flutter/dart analyze");
        if (profile.HasGo) parts.Add("go vet");
        if (profile.HasPython) parts.Add("ruff check");
        if (profile.HasTypeScript) parts.Add("tsc --noEmit");
        if (profile.HasRust) parts.Add("cargo check");
        if (profile.HasCSharp) parts.Add("dotnet build");
        if (profile.HasPhp) parts.Add("phpstan/php -l");
        if (profile.HasCpp) parts.Add("clang-tidy");
        return parts.Count == 0
            ? "No supported language project detected in this repository."
            : $"Force {string.Join(" + ", parts)} once (does not replace live LSP diagnostics).";
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Log(ex.Message, true); }
    }
    private void Log(string text, bool show = false)
    {
        var next = OutputText.Text + $"[{DateTime.Now:HH:mm:ss}] {text}\n\n";
        OutputText.Text = next.Length > Files.PreviewLimit ? next[^Files.PreviewLimit..] : next;
        OutputText.ScrollToEnd();
        if (show) { SetPanel(true); BottomPanel.SelectedItem = OutputTab; }
    }

    private static void SetDiffTotal(TextBlock added, TextBlock deleted, DiffLineCount total)
    {
        added.Text = total.Added > 0 ? $"+{total.Added}" : "";
        added.Visibility = total.Added > 0 ? Visibility.Visible : Visibility.Collapsed;
        deleted.Text = total.Deleted > 0 ? $"-{total.Deleted}" : "";
        deleted.Visibility = total.Deleted > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RefreshRepositoryAsync(bool reloadHistory = true)
    {
        var generation = ++refreshGeneration;
        refreshing = true;
        SourceChanges.IsEnabled = false;
        try
        {
            var next = await git.StatusAsync(lifetime.Token);
            if (generation != refreshGeneration || disposed) return;
            status = next;
            var (stagedCounts, unstagedCounts) = next.Count == 0
                ? (EmptyLineCounts, EmptyLineCounts)
                : await git.LineCountsAsync(next, lifetime.Token);
            if (generation != refreshGeneration || disposed) return;
            var stagedPaths = StagedList.SelectedItems.Cast<GitFile>().Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var changedPaths = ChangesList.SelectedItems.Cast<GitFile>().Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
            var stagedFiles = next.Where(f => f.Staged).Select(f => GitService.WithLineCount(f, stagedCounts)).ToArray();
            var changedFiles = next.Where(f => f.Unstaged).Select(f => GitService.WithLineCount(f, unstagedCounts)).ToArray();
            StagedList.ItemsSource = stagedFiles;
            ChangesList.ItemsSource = changedFiles;
            foreach (GitFile file in StagedList.Items) if (stagedPaths.Contains(file.Path)) StagedList.SelectedItems.Add(file);
            foreach (GitFile file in ChangesList.Items) if (changedPaths.Contains(file.Path)) ChangesList.SelectedItems.Add(file);
            StagedTitle.Text = $"Staged ({stagedFiles.Length})";
            ChangesTitle.Text = $"Changes ({changedFiles.Length})";
            SetDiffTotal(StagedAddedTotal, StagedDeletedTotal, GitService.SumLineCounts(stagedFiles));
            SetDiffTotal(ChangesAddedTotal, ChangesDeletedTotal, GitService.SumLineCounts(changedFiles));
            var changedFileCount = next.Where(file => file.Staged || file.Unstaged)
                .Select(file => file.Path).Distinct(StringComparer.Ordinal).Count();
            var changeDescription = changedFileCount == 1 ? "1 changed file" : $"{changedFileCount} changed files";
            SourceControlBadgeText.Text = changedFileCount > 99 ? "99+" : changedFileCount.ToString();
            SourceControlBadge.Visibility = changedFileCount == 0 ? Visibility.Hidden : Visibility.Visible;
            SourceControlBadge.ToolTip = changeDescription;
            AutomationProperties.SetHelpText(SourceControlTab, changeDescription);
            SourceChanges.IsEnabled = true;
            var branch = await git.RunAsync(["status", "--short", "--branch", "--untracked-files=no"], lifetime.Token);
            if (generation != refreshGeneration || disposed) return;
            BranchText.Text = branch.Output.Split('\n')[0].TrimStart('#', ' ', '\r');
            StatusText.Text = $"{root} | {BranchText.Text} | {StagedList.Items.Count} staged · {ChangesList.Items.Count} changes";
            foreach (var tab in Documents.Items.Cast<TabItem>().Where(t => t.Tag is StatusDiffTabKey).ToArray())
            {
                if (tab.Tag is not StatusDiffTabKey key) continue;
                var file = next.FirstOrDefault(f => f.Path.Equals(key.Path, StringComparison.Ordinal));
                var diff = file == null
                    ? NoStatusDiff(key.Staged ? "No staged changes for this file." : "No working tree changes for this file.")
                    : await loadStatusDiff(file, key.Staged, lifetime.Token);
                if (disposed || generation != refreshGeneration) return;
                var beforeLabel = key.Staged ? file?.Index == 'A' ? "Empty tree" : "HEAD"
                    : file?.Untracked == true ? "Empty file" : file?.Staged == true ? "Index" : "HEAD";
                tab.Content = new RevisionDiffView(diff, beforeLabel, key.Staged ? "Staged" : "Working tree");
            }
            if (Documents.SelectedItem is TabItem { Content: DocumentView active }) await UpdateDocumentAsync(active);
            if (reloadHistory) await LoadHistoryAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Log("Refresh failed: " + ex.Message, true); }
        finally
        {
            if (generation == refreshGeneration)
            {
                refreshing = false;
                SourceChanges.IsEnabled = true;
            }
        }
    }

    private void RefreshTree()
    {
        ExplorerTree.Items.Clear();
        var node = CreateNode(new DirectoryInfo(root));
        ExplorerTree.Items.Add(node); node.IsExpanded = true;
    }
    private TreeViewItem CreateNode(FileSystemInfo entry)
    {
        var directory = (entry.Attributes & FileAttributes.Directory) != 0;
        var linked = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        var item = new TreeViewItem { Header = entry.Name, Tag = entry.FullName, ToolTip = entry.FullName };
        if (directory && !linked)
        {
            item.Items.Add(new TreeViewItem { Header = "Loading..." });
            item.Expanded += async (sender, args) =>
            {
                if (args.OriginalSource != item || item.Items.Count != 1 || ((TreeViewItem)item.Items[0]).Tag != null) return;
                args.Handled = true;
                try
                {
                    var entries = await Task.Run(() => new DirectoryInfo(entry.FullName).EnumerateFileSystemInfos()
                        .Where(f => !f.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => (f.Attributes & FileAttributes.Directory) != 0)
                        .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToArray(), lifetime.Token);
                    item.Items.Clear();
                    foreach (var child in entries) item.Items.Add(CreateNode(child));
                }
                catch (Exception ex) { item.Items.Clear(); item.Items.Add(new TreeViewItem { Header = "Unable to list: " + ex.Message, Tag = "error" }); }
            };
        }
        else if (linked) item.ToolTip = "Linked folders are shown without traversal: " + entry.FullName;
        return item;
    }
    private async void TreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not TreeViewItem { Tag: string path } || !File.Exists(path)) return;
        selectedExplorerFile = ToRelativePath(path);
        await Guard(() => OpenFileAsync(selectedExplorerFile));
    }

    private void ExplorerRightClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not { Tag: string } item) return;
        item.IsSelected = true;
        item.Focus();
        if (item.Tag is string path && File.Exists(path)) selectedExplorerFile = ToRelativePath(path);
    }

    private void ExplorerMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var path = SelectedExplorerPath();
        var hasPath = path != null;
        var isFile = hasPath && File.Exists(path);
        var isDirectory = hasPath && Directory.Exists(path);
        var isRoot = hasPath && PathsEqual(path!, root);
        ExplorerOpenWith.IsEnabled = isFile;
        ExplorerReveal.IsEnabled = hasPath;
        ExplorerOpenTerminal.IsEnabled = hasPath;
        ExplorerCopyPath.IsEnabled = hasPath;
        ExplorerCopyRelativePath.IsEnabled = hasPath;
        ExplorerRename.IsEnabled = hasPath && !isRoot && (isFile || isDirectory);
        ExplorerDelete.IsEnabled = hasPath && !isRoot && (isFile || isDirectory);
        ExplorerFileHistory.IsEnabled = isFile;
        if (!hasPath) e.Handled = true;
    }

    private void ExplorerOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) OpenWithPath(path);
    }

    private void ExplorerRevealClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) RevealPath(path);
    }

    private async void ExplorerOpenTerminalClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) await OpenTerminalAtPathAsync(path);
    }

    private void ExplorerCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) CopyText(path, "Copied path: " + path);
    }

    private void ExplorerCopyRelativePathClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is not string path) return;
        var relative = ToRelativePath(path);
        CopyText(relative, "Copied relative path: " + relative);
    }

    private async void ExplorerRenameClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) await RenamePathAsync(path);
    }

    private async void ExplorerDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is string path) await DeletePathAsync(path);
    }

    private async void ExplorerFileHistoryClick(object sender, RoutedEventArgs e)
    {
        if (SelectedExplorerPath() is not string path || !File.Exists(path)) return;
        await ShowFileHistoryAsync(ToRelativePath(path));
    }

    private void OpenWithPath(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true, Verb = "openas" });
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "rundll32.exe",
                    Arguments = $"shell32.dll,OpenAs_RunDLL {path}",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Log("Open With failed: " + ex.Message, true);
                StatusText.Text = "Open With failed.";
            }
        }
    }

    private void RevealPath(string path)
    {
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"/select,\"{path}\"", UseShellExecute = true });
            else if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            else
            {
                var parent = Path.GetDirectoryName(path);
                if (parent != null && Directory.Exists(parent))
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{parent}\"", UseShellExecute = true });
                else StatusText.Text = "Path no longer exists.";
            }
        }
        catch (Exception ex)
        {
            Log("Reveal failed: " + ex.Message, true);
            StatusText.Text = "Reveal in File Explorer failed.";
        }
    }

    private async Task OpenTerminalAtPathAsync(string path)
    {
        var cwd = Directory.Exists(path) ? path
            : File.Exists(path) ? Path.GetDirectoryName(path) ?? root
            : Path.GetDirectoryName(path) is string parent && Directory.Exists(parent) ? parent
            : root;
        SetPanel(true);
        BottomPanel.SelectedItem = TerminalTab;
        await Guard(() => OpenTerminalAsync(cwd));
    }

    private async Task RenamePathAsync(string path)
    {
        if (PathsEqual(path, root)) return;
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) { StatusText.Text = "Path no longer exists."; return; }
        var currentName = Path.GetFileName(path);
        var nextName = PromptName(this, "Rename", currentName);
        if (nextName == null) return;
        nextName = nextName.Trim();
        if (nextName.Length == 0 || nextName is "." or ".."
            || nextName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || nextName.Contains('\\') || nextName.Contains('/'))
        {
            StatusText.Text = "Enter a valid file name.";
            return;
        }
        if (string.Equals(nextName, currentName, StringComparison.Ordinal)) return;
        var destination = Path.Combine(Path.GetDirectoryName(path)!, nextName);
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            StatusText.Text = "A file or folder with that name already exists.";
            return;
        }
        try
        {
            if (isDirectory) Directory.Move(path, destination);
            else File.Move(path, destination);
            RemapDocumentsAfterMove(path, destination);
            if (selectedExplorerFile != null && PathsEqual(Path.GetFullPath(selectedExplorerFile, root), path))
                selectedExplorerFile = ToRelativePath(destination);
            else if (isDirectory && selectedExplorerFile != null)
            {
                var absolute = Path.GetFullPath(selectedExplorerFile, root);
                if (absolute.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    selectedExplorerFile = ToRelativePath(destination + absolute[path.Length..]);
            }
            RefreshTree();
            await RefreshRepositoryAsync(reloadHistory: false);
            if (!disposed) StatusText.Text = "Renamed to " + nextName;
        }
        catch (Exception ex)
        {
            Log("Rename failed: " + ex.Message, true);
            StatusText.Text = "Rename failed.";
        }
    }

    private async Task DeletePathAsync(string path)
    {
        if (PathsEqual(path, root)) return;
        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path)) { StatusText.Text = "Path no longer exists."; return; }
        var name = Path.GetFileName(path);
        var prompt = isDirectory
            ? $"Delete folder '{name}' and all of its contents?"
            : $"Delete '{name}'?";
        if (MessageBox.Show(this, prompt, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            if (isDirectory) Directory.Delete(path, true);
            else File.Delete(path);
            CloseDocumentsUnder(path);
            if (selectedExplorerFile != null)
            {
                var absolute = Path.GetFullPath(selectedExplorerFile, root);
                if (PathsEqual(absolute, path)
                    || absolute.StartsWith(path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    selectedExplorerFile = null;
            }
            RefreshTree();
            await RefreshRepositoryAsync(reloadHistory: false);
            if (!disposed) StatusText.Text = "Deleted " + name;
        }
        catch (Exception ex)
        {
            // Recursive deletion can remove entries before it encounters an error.
            await RefreshRepositoryAsync(reloadHistory: false);
            if (disposed) return;
            Log("Delete failed: " + ex.Message, true);
            StatusText.Text = "Delete failed.";
        }
    }

    private static void LabelSearchField(TextBox field, TextBlock label)
    {
        AutomationProperties.SetLabeledBy(field, label);
        AutomationProperties.SetName(field, label.Text);
    }

    private string? SelectedExplorerPath() =>
        ExplorerTree.SelectedItem is TreeViewItem { Tag: string path } && path is not "error" ? path : null;

    private string ToRelativePath(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private static string? PromptName(Window owner, string title, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new(12), MinWidth = 280 };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 80, Margin = new(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(12) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        panel.Children.Add(buttons);
        panel.Children.Add(box);
        var dialog = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = owner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
        };
        string? result = null;
        ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
        box.SelectAll();
        box.Focus();
        return dialog.ShowDialog() == true ? result : null;
    }

    private void RemapDocumentsAfterMove(string fromPath, string toPath)
    {
        // A preview can outlive a file that was removed outside Workspace.
        if (!PathsEqual(fromPath, toPath)) CloseDocumentsUnder(toPath);
        var fromRelative = ToRelativePath(fromPath);
        var toRelative = ToRelativePath(toPath);
        var fromPrefix = fromRelative.EndsWith('/') ? fromRelative : fromRelative + "/";
        foreach (var entry in documents.ToArray())
        {
            string? next = null;
            if (entry.Key.Equals(fromRelative, StringComparison.OrdinalIgnoreCase)) next = toRelative;
            else if (entry.Key.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase))
                next = toRelative + entry.Key[fromRelative.Length..];
            if (next == null) continue;
            documents.Remove(entry.Key);
            previewVersions[entry.Value.View] = previewVersions.GetValueOrDefault(entry.Value.View) + 1;
            entry.Value.View.Retarget(next);
            documents[next] = entry.Value;
            RefreshDocumentTabHeader(entry.Value.Tab, entry.Value.View);
            _ = languages.CloseDocumentAsync(entry.Key);
            _ = languages.OpenDocumentAsync(next);
        }
    }

    private void CloseDocumentsUnder(string path)
    {
        var relative = ToRelativePath(path);
        var prefix = relative.EndsWith('/') ? relative : relative + "/";
        foreach (var entry in documents.ToArray())
        {
            if (!entry.Key.Equals(relative, StringComparison.OrdinalIgnoreCase)
                && !entry.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            ForceCloseDocumentTab(entry.Value.Tab);
        }
    }

    private async Task OpenFileAsync(string path, bool diff = false, int line = 0, int column = 1)
    {
        if (!documents.TryGetValue(path, out var document))
        {
            var view = new DocumentView(path);
            var tab = CreateDocumentTab(path, view);
            document = (tab, view); documents.Add(path, document);
            _ = languages.OpenDocumentAsync(path);
        }
        Documents.SelectedItem = document.Tab;
        await UpdateDocumentAsync(document.View, diff);
        if (line > 0) document.View.GoTo(line, column);
    }
    private async void DocumentSelection(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Documents || disposed) return;
        detailGeneration++;
        if (!refreshing && Documents.SelectedItem is TabItem { Content: DocumentView view })
            await Guard(() => UpdateDocumentAsync(view));
    }
    private readonly Dictionary<DocumentView, int> previewVersions = [];
    private async Task UpdateDocumentAsync(DocumentView view, bool diff = false, bool diskChanged = false)
    {
        var version = previewVersions.GetValueOrDefault(view) + 1;
        previewVersions[view] = version;
        var absolute = Path.GetFullPath(view.RelativePath, root);
        var file = status.FirstOrDefault(f => f.Path.Equals(view.RelativePath, StringComparison.Ordinal));
        var preview = await Files.ReadAsync(absolute);
        var writeTime = TryGetWriteTimeUtc(absolute);
        var staged = file?.Untracked == true ? "Untracked: stage this file to create a diff." : "No staged changes.";
        var working = file?.Untracked == true ? "Untracked file. See File for its contents." : "No working tree changes.";
        if (!preview.IsText && preview.Text.Contains("2 MB")) staged = working = "Diff omitted: file exceeds the 2 MB preview limit.";
        else if (file != null)
        {
            if (file.Staged) staged = DiffText(await git.DiffAsync(file, true, lifetime.Token));
            if (file.Unstaged && !file.Untracked) working = DiffText(await git.DiffAsync(file, false, lifetime.Token));
        }
        if (!disposed && previewVersions.GetValueOrDefault(view) == version)
        {
            if (view.IsDirty)
            {
                view.UpdateDiffsOnly(staged, working);
                if (diskChanged) view.NoteDiskChangedWhileDirty();
            }
            else view.Update(preview, staged, working, diff, writeTime);
            view.SetDiagnostics(languages.Store.ForFile(absolute));
            if (documents.TryGetValue(view.RelativePath, out var document))
                RefreshDocumentTabHeader(document.Tab, view);
        }
    }
    private static DateTime TryGetWriteTimeUtc(string absolute)
    {
        try { return File.Exists(absolute) ? File.GetLastWriteTimeUtc(absolute) : DateTime.MinValue; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return DateTime.MinValue; }
    }
    private static string DiffText(CommandResult result) => result.ExitCode == 0 ? result.Display : "Unable to load diff:\n" + result.Display;
    private TabItem CreateDocumentTab(string title, object content)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = title, MaxWidth = 440, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        var close = new Button { Content = "×", Padding = new(3, 0, 3, 0), Margin = new(8, 0, 0, 0) };
        header.Children.Add(close);
        var tab = new TabItem { Header = header, Content = content, ToolTip = title };
        if (content is DocumentView view)
        {
            view.DirtyChanged += () => Dispatcher.BeginInvoke(() =>
            {
                if (!disposed) RefreshDocumentTabHeader(tab, view);
            });
            view.ContentChanged += () => ScheduleDocumentLspChange(view);
            var menu = new ContextMenu();
            var saveItem = new MenuItem { Header = "Save", InputGestureText = "Ctrl+S" };
            saveItem.Click += (_, _) => _ = Guard(() => SaveDocumentAsync(view));
            var revertItem = new MenuItem { Header = "Revert File", InputGestureText = "Ctrl+Shift+R" };
            revertItem.Click += (_, _) => _ = Guard(() => RevertDocumentAsync(view));
            menu.Items.Add(saveItem);
            menu.Items.Add(revertItem);
            menu.Opened += (_, _) =>
            {
                saveItem.IsEnabled = view.CanEdit && view.IsDirty;
                revertItem.IsEnabled = view.CanEdit && view.IsDirty;
            };
            AttachDocumentTabContextMenu(tab, menu);
        }
        else AttachDocumentTabContextMenu(tab);
        close.Click += (_, _) => _ = Guard(() => TryCloseDocumentTabAsync(tab));
        Documents.Items.Add(tab); return tab;
    }
    private void AttachDocumentTabContextMenu(TabItem tab, ContextMenu? menu = null)
    {
        menu ??= new ContextMenu();
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var closeAll = new MenuItem { Header = "Close All" };
        closeAll.Click += (_, _) => _ = Guard(CloseAllDocumentTabsAsync);
        menu.Items.Add(closeAll);
        tab.ContextMenu = menu;
    }
    private async Task CloseAllDocumentTabsAsync()
    {
        foreach (var tab in Documents.Items.Cast<TabItem>().ToArray())
        {
            if (!await TryCloseDocumentTabAsync(tab)) return;
        }
    }
    private void RefreshDocumentTabHeader(TabItem tab, DocumentView view)
    {
        if (tab.Header is StackPanel header && header.Children.OfType<TextBlock>().FirstOrDefault() is { } label)
            label.Text = view.IsDirty ? view.RelativePath + " *" : view.RelativePath;
        var tip = view.RelativePath;
        if (view.IsDirty) tip += " (unsaved · Ctrl+S)";
        if (view.DiskChangedWhileDirty) tip += " — changed on disk";
        tab.ToolTip = tip;
        if (view.DiskChangedWhileDirty && ReferenceEquals(Documents.SelectedItem, tab) && !disposed)
            StatusText.Text = $"{view.RelativePath} changed on disk while editing. Ctrl+S keeps your buffer; Ctrl+Shift+R reverts.";
    }
    private void ScheduleDocumentLspChange(DocumentView view)
    {
        if (disposed || closing || LspPaths.LanguageId(Path.GetFullPath(view.RelativePath, root)) == null) return;
        if (documentLspDebounce.TryGetValue(view, out var prior))
        {
            prior.Cancel();
            prior.Dispose();
        }
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        documentLspDebounce[view] = cancellation;
        _ = Guard(async () =>
        {
            try
            {
                await Task.Delay(300, cancellation.Token);
                if (disposed || closing || !documents.ContainsKey(view.RelativePath)) return;
                await languages.ChangeDocumentAsync(view.RelativePath, view.CurrentText);
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(documentLspDebounce.GetValueOrDefault(view), cancellation))
                    documentLspDebounce.Remove(view);
                cancellation.Dispose();
            }
        });
    }
    private async Task<bool> SaveDocumentAsync(DocumentView view)
    {
        if (disposed || closing || !view.CanEdit) return false;
        var absolute = Path.GetFullPath(view.RelativePath, root);
        if (File.Exists(absolute))
        {
            var writeTime = TryGetWriteTimeUtc(absolute);
            if (writeTime != view.CleanWriteTimeUtc)
            {
                var disk = await Files.ReadAsync(absolute);
                if (disk.IsText && DocumentView.HashText(disk.Text) != view.CleanHash)
                {
                    var choice = MessageBox.Show(this,
                        $"{view.RelativePath} changed on disk. Overwrite with your editor buffer?",
                        "Save conflict", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
                    if (choice != MessageBoxResult.Yes) return false;
                }
            }
        }
        var text = view.CurrentText;
        try { await Files.WriteAsync(absolute, text); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log("Save failed: " + ex.Message, true);
            StatusText.Text = "Save failed.";
            return false;
        }
        view.MarkClean(text, TryGetWriteTimeUtc(absolute));
        if (documents.TryGetValue(view.RelativePath, out var document))
            RefreshDocumentTabHeader(document.Tab, view);
        await languages.SaveDocumentAsync(view.RelativePath, text);
        StatusText.Text = "Saved " + view.RelativePath;
        if (!refreshing) _ = Guard(() => RefreshRepositoryAsync(reloadHistory: false));
        return true;
    }
    private async Task RevertDocumentAsync(DocumentView view)
    {
        if (disposed || closing || !view.CanEdit || !view.IsDirty) return;
        if (MessageBox.Show(this, $"Discard unsaved changes to {view.RelativePath}?", "Revert file",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var absolute = Path.GetFullPath(view.RelativePath, root);
        var preview = await Files.ReadAsync(absolute);
        view.RevertToDisk(preview, TryGetWriteTimeUtc(absolute));
        if (documents.TryGetValue(view.RelativePath, out var document))
            RefreshDocumentTabHeader(document.Tab, view);
        if (preview.IsText) await languages.ChangeDocumentAsync(view.RelativePath, view.CurrentText);
        StatusText.Text = "Reverted " + view.RelativePath;
        await UpdateDocumentAsync(view);
    }
    private async Task<bool> TryCloseDocumentTabAsync(TabItem tab)
    {
        if (tab.Content is DocumentView view && view.IsDirty)
        {
            var choice = MessageBox.Show(this, $"Save changes to {view.RelativePath}?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel) return false;
            if (choice == MessageBoxResult.Yes && !await SaveDocumentAsync(view)) return false;
        }
        ForceCloseDocumentTab(tab);
        return true;
    }
    private void ForceCloseDocumentTab(TabItem tab)
    {
        if (tab.Content is DocumentView view
            && documents.TryGetValue(view.RelativePath, out var document)
            && ReferenceEquals(document.Tab, tab))
        {
            documents.Remove(view.RelativePath);
            previewVersions.Remove(view);
            if (documentLspDebounce.Remove(view, out var debounce))
            {
                debounce.Cancel();
                debounce.Dispose();
            }
            _ = languages.CloseDocumentAsync(view.RelativePath);
        }
        Documents.Items.Remove(tab);
    }
    private void CloseDocumentTab(TabItem tab) => ForceCloseDocumentTab(tab);

    private async void StatusSelection(object sender, SelectionChangedEventArgs e)
    {
        if (refreshing || e.AddedItems.Count == 0 || sender is not DataGrid grid || grid.SelectedItem is not GitFile file) return;
        var staged = ReferenceEquals(grid, StagedList);
        var version = ++detailGeneration;
        await Guard(async () =>
        {
            var diff = await loadStatusDiff(file, staged, lifetime.Token);
            if (disposed || version != detailGeneration) return;
            var key = new StatusDiffTabKey(file.Path, staged);
            var tab = Documents.Items.Cast<TabItem>().FirstOrDefault(t => Equals(t.Tag, key));
            var beforeLabel = staged ? file.Index == 'A' ? "Empty tree" : "HEAD"
                : file.Untracked ? "Empty file" : file.Staged ? "Index" : "HEAD";
            var view = new RevisionDiffView(diff,
                beforeLabel,
                staged ? "Staged" : "Working tree");
            if (tab == null) { tab = CreateDocumentTab(file.Path, view); tab.Tag = key; }
            else tab.Content = view;
            tab.ToolTip = file.Path + (staged ? " · staged changes" : " · working tree changes");
            Documents.SelectedItem = tab;
        });
    }
    private static RevisionDiff NoStatusDiff(string message) => new(new(message, true), new(message, true), "");
    private async Task GitActionAsync(string label, Func<Task<CommandResult>> action, bool commit = false)
    {
        if (gitBusy) return;
        gitBusy = true; SourceActions.IsEnabled = false; StatusText.Text = label + "...";
        try
        {
            var result = await action();
            Log($"{label} (exit {result.ExitCode})\n" + result.Display, result.ExitCode != 0);
            if (commit && result.ExitCode == 0) CommitMessage.Clear();
            await RefreshRepositoryAsync();
            StatusText.Text = result.ExitCode == 0 ? label + " completed. | " + BranchText.Text : label + $" failed (exit {result.ExitCode}). See Output.";
            if (label == "Pull") RefreshTree();
        }
        catch (Exception ex) { Log(ex.Message, true); StatusText.Text = label + " failed."; }
        finally { gitBusy = false; SourceActions.IsEnabled = true; }
    }
    private void StatusListRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not { Item: GitFile file } row) return;
        if (!grid.SelectedItems.Contains(file))
        {
            grid.SelectedItems.Clear();
            grid.SelectedItem = file;
        }
        row.Focus();
    }

    private void StagedMenuOpening(object sender, ContextMenuEventArgs e) =>
        StatusMenuOpening(StagedList, [StagedUnstage], StagedOpenWith, StagedReveal, StagedOpenTerminal,
            StagedCopyPath, StagedCopyRelativePath, StagedRename, StagedDelete, StagedFileHistory, e);

    private void ChangesMenuOpening(object sender, ContextMenuEventArgs e) =>
        StatusMenuOpening(ChangesList, [ChangesStage, ChangesDiscard], ChangesOpenWith, ChangesReveal, ChangesOpenTerminal,
            ChangesCopyPath, ChangesCopyRelativePath, ChangesRename, ChangesDelete, ChangesFileHistory, e);

    private void StatusMenuOpening(DataGrid grid, MenuItem[] gitActions, MenuItem openWith, MenuItem reveal,
        MenuItem openTerminal, MenuItem copyPath, MenuItem copyRelativePath, MenuItem rename, MenuItem delete,
        MenuItem fileHistory, ContextMenuEventArgs e)
    {
        if (grid.SelectedItem is not GitFile file)
        {
            e.Handled = true;
            return;
        }
        var path = Path.GetFullPath(file.Path, root);
        var isFile = File.Exists(path);
        var isDirectory = Directory.Exists(path);
        var parentExists = Path.GetDirectoryName(path) is string parent && Directory.Exists(parent);
        var single = grid.SelectedItems.Count == 1;
        // Snapshot the menu's grid selection so Stage/Unstage/Discard stay correct if focus moves.
        var selected = grid.SelectedItems.Cast<GitFile>().ToArray();
        foreach (var action in gitActions)
        {
            action.Tag = selected;
            action.IsEnabled = selected.Length > 0;
        }
        openWith.IsEnabled = isFile;
        reveal.IsEnabled = isFile || isDirectory || parentExists;
        openTerminal.IsEnabled = isFile || isDirectory || parentExists;
        copyPath.IsEnabled = true;
        copyRelativePath.IsEnabled = true;
        rename.IsEnabled = single && (isFile || isDirectory);
        delete.IsEnabled = single && (isFile || isDirectory);
        fileHistory.IsEnabled = true;
    }

    private DataGrid? StatusGridFromMenu(object sender)
    {
        if (sender is not MenuItem item) return null;
        if (item.Parent is ContextMenu { PlacementTarget: DataGrid grid }) return grid;
        // Smoke tests raise Click without opening the menu, so PlacementTarget may be unset.
        if (ReferenceEquals(item.Parent, StagedList.ContextMenu)) return StagedList;
        if (ReferenceEquals(item.Parent, ChangesList.ContextMenu)) return ChangesList;
        return null;
    }

    private string? SelectedStatusAbsolutePath(object sender) =>
        StatusGridFromMenu(sender)?.SelectedItem is GitFile file ? Path.GetFullPath(file.Path, root) : null;

    private void StatusOpenWithClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) OpenWithPath(path);
    }

    private void StatusRevealClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) RevealPath(path);
    }

    private async void StatusOpenTerminalClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) await OpenTerminalAtPathAsync(path);
    }

    private void StatusCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) CopyText(path, "Copied path: " + path);
    }

    private void StatusCopyRelativePathClick(object sender, RoutedEventArgs e)
    {
        if (StatusGridFromMenu(sender)?.SelectedItem is not GitFile file) return;
        CopyText(file.Path, "Copied relative path: " + file.Path);
    }

    private async void StatusRenameClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) await RenamePathAsync(path);
    }

    private async void StatusDeleteClick(object sender, RoutedEventArgs e)
    {
        if (SelectedStatusAbsolutePath(sender) is string path) await DeletePathAsync(path);
    }

    private async void StatusFileHistoryClick(object sender, RoutedEventArgs e)
    {
        if (StatusGridFromMenu(sender)?.SelectedItem is not GitFile file) return;
        await ShowFileHistoryAsync(file.Path);
    }

    private async void StageClick(object sender, RoutedEventArgs e)
    {
        var files = StatusGitActionFiles(sender, ChangesList);
        if (files.Length > 0) await GitActionAsync("Stage", () => git.StageAsync(files, lifetime.Token));
    }
    private async void UnstageClick(object sender, RoutedEventArgs e)
    {
        var files = StatusGitActionFiles(sender, StagedList);
        if (files.Length > 0) await GitActionAsync("Unstage", () => git.UnstageAsync(files, lifetime.Token));
    }

    private async void DiscardClick(object sender, RoutedEventArgs e)
    {
        var files = StatusGitActionFiles(sender, ChangesList);
        if (files.Length == 0) return;
        var label = files.Length == 1 ? $"'{files[0].Path}'" : $"{files.Length} files";
        if (!Confirm($"Discard unstaged changes in {label}? This cannot be undone.", "Discard"))
            return;
        await GitActionAsync("Discard", () => git.DiscardAsync(files, lifetime.Token));
    }

    private bool Confirm(string message, string title) =>
        confirmAction?.Invoke(message, title)
        ?? MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    private GitFile[] StatusGitActionFiles(object sender, DataGrid buttonFallback)
    {
        if (sender is MenuItem item)
        {
            if (item.Tag is GitFile[] snapshot) return snapshot;
            if (StatusGridFromMenu(item) is { } grid)
                return grid.SelectedItems.Cast<GitFile>().ToArray();
        }
        return buttonFallback.SelectedItems.Cast<GitFile>().ToArray();
    }
    private async void StageAllClick(object sender, RoutedEventArgs e) => await GitActionAsync("Stage all", () => git.RunAsync(["add", "--all"], lifetime.Token));
    private async void CommitClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CommitMessage.Text) || StagedList.Items.Count == 0) { StatusText.Text = "Stage files and enter a commit message first."; return; }
        if (status.Any(f => f.Conflicted)) { StatusText.Text = "Resolve and stage all conflicts before committing."; return; }
        var message = CommitMessage.Text;
        await GitActionAsync("Commit", () => git.RunAsync(["commit", "-m", message], lifetime.Token), true);
    }
    private async void PullClick(object sender, RoutedEventArgs e) => await GitActionAsync("Pull", () => git.RunAsync(["pull"], lifetime.Token));
    private async void PushClick(object sender, RoutedEventArgs e) => await GitActionAsync("Push", () => git.RunAsync(["push"], lifetime.Token));
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (!gitBusy) await RefreshRepositoryAsync(reloadHistory: true); }
    private void RefreshTreeClick(object sender, RoutedEventArgs e) => RefreshTree();

    private async Task LoadHistoryAsync()
    {
        var version = ++historyGeneration;
        var prior = (HistoryList.ItemsSource as IEnumerable<Commit> ?? []).ToDictionary(c => c.Hash, StringComparer.Ordinal);
        var selected = HistoryList.SelectedItems.Cast<Commit>().Select(c => c.Hash).ToArray();
        var commits = await git.HistoryAsync(historyCount, historyPath, lifetime.Token);
        if (disposed || version != historyGeneration) return;
        foreach (var commit in commits)
        {
            if (!prior.TryGetValue(commit.Hash, out var previous) || !previous.FilesLoaded) continue;
            foreach (var file in previous.Files) commit.Files.Add(file);
            commit.FilesLoaded = true;
        }
        HistoryList.ItemsSource = commits;
        foreach (var commit in commits)
            if (selected.Contains(commit.Hash, StringComparer.Ordinal)) HistoryList.SelectedItems.Add(commit);
        HistoryTitle.Text = historyPath == null ? "Commit graph · all local refs" : "File history · " + historyPath;
    }
    private async void AllHistoryClick(object sender, RoutedEventArgs e) { historyPath = null; historyCount = 150; await Guard(LoadHistoryAsync); }
    private async void MoreHistoryClick(object sender, RoutedEventArgs e) { historyCount += 150; await Guard(LoadHistoryAsync); }
    private async void FileHistoryClick(object sender, RoutedEventArgs e)
    {
        var path = selectedExplorerFile ?? documents.FirstOrDefault(d => d.Value.Tab == Documents.SelectedItem).Key;
        if (string.IsNullOrEmpty(path)) { StatusText.Text = "Select a file in Explorer first."; return; }
        await ShowFileHistoryAsync(path);
    }

    private async Task ShowFileHistoryAsync(string relativePath)
    {
        historyPath = relativePath;
        historyCount = 150;
        Sidebar.SelectedIndex = 1;
        await Guard(LoadHistoryAsync);
    }
    private void HistoryMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || Keyboard.Modifiers != ModifierKeys.None || HistoryList.SelectedItems.Count != 1
            || e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(HistoryList, source) is not DataGridRow { IsSelected: true }) return;
        HistoryList.SelectedItems.Clear();
        e.Handled = true;
    }
    private void CopySelectedCommitMessageClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (HistoryList.SelectedItem is Commit commit)
            CopyText(commit.Subject, $"Copied commit message: {commit.Subject}");
    }
    private void CopySelectedCommitHashClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (HistoryList.SelectedItem is Commit commit)
            CopyText(commit.Hash, $"Copied commit hash: {commit.ShortHash}");
    }
    private void CopyText(string text, string statusMessage)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            StatusText.Text = statusMessage;
        }
        catch (Exception ex)
        {
            Log($"Failed to copy to clipboard: {ex.Message}", true);
            StatusText.Text = "Failed to copy to clipboard.";
        }
    }
    private async void HistorySelection(object sender, SelectionChangedEventArgs e)
    {
        UpdateCommitFilesPanel();
        var pending = e.AddedItems.OfType<Commit>().Where(c => !c.FilesLoaded).ToArray();
        if (pending.Length == 0) return;
        await Guard(async () =>
        {
            foreach (var commit in pending)
            {
                if (commit.FilesLoaded) continue;
                var files = await git.ChangedFilesAsync(commit, lifetime.Token);
                if (disposed || commit.FilesLoaded) continue;
                foreach (var file in files) commit.Files.Add(file);
                commit.FilesLoaded = true;
            }
            UpdateCommitFilesPanel();
            if (HistoryList.SelectedItems.Count == 1 && HistoryList.SelectedItem is Commit selected && selected.FilesLoaded)
                StatusText.Text = $"{selected.ShortHash} · {selected.Subject} · {selected.Files.Count} changed files";
        });
    }
    private void UpdateCommitFilesPanel()
    {
        var selected = HistoryList.SelectedItems.Count == 1 ? HistoryList.SelectedItem as Commit : null;
        CommitFilesList.ItemsSource = selected?.Files;
        if (selected != null)
        {
            CommitDetailSubject.Text = selected.Subject;
            CommitDetailSubject.ToolTip = selected.Subject;
            CommitDetailHash.Text = selected.Hash;
            CommitDetailHash.ToolTip = selected.Hash;
            CommitFilesTitle.Text = $"{selected.ShortHash} · {selected.Files.Count} changed files · compared to first parent";
        }
        else
        {
            CommitDetailSubject.Text = "";
            CommitDetailSubject.ToolTip = null;
            CommitDetailHash.Text = "";
            CommitDetailHash.ToolTip = null;
            CommitFilesTitle.Text = "";
        }

        var show = selected != null && HistoryList.IsVisible;
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (HistoryFilesPanel.Visibility == visibility) return;
        HistoryFilesPanel.Visibility = visibility;
        HistoryFilesSplitter.Visibility = visibility;
        HistoryFilesColumn.Width = show ? new(0.4, GridUnitType.Star) : new(0);
        HistoryFilesSplitterColumn.Width = new(show ? 5 : 0);
        DocumentsColumn.Width = new(1, GridUnitType.Star);
    }
    private async void CommitFileClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: CommitFile file }) return;
        var key = "revision:" + file.CommitHash + ":" + file.Path;
        var existing = Documents.Items.Cast<TabItem>().FirstOrDefault(t => Equals(t.Tag, key));
        if (existing != null) { Documents.SelectedItem = existing; return; }
        var version = ++detailGeneration;
        await Guard(async () =>
        {
            var diff = await git.RevisionDiffAsync(file, lifetime.Token);
            if (disposed || version != detailGeneration) return;
            existing = Documents.Items.Cast<TabItem>().FirstOrDefault(t => Equals(t.Tag, key));
            if (existing != null) { Documents.SelectedItem = existing; return; }
            var tab = CreateDocumentTab(file.Path + " · " + file.CommitHash[..Math.Min(8, file.CommitHash.Length)], new RevisionDiffView(file, diff));
            tab.Tag = key; Documents.SelectedItem = tab;
        });
    }
    private async void CompareClick(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItems.Count != 2) { StatusText.Text = "Ctrl+click two commits to compare."; return; }
        var selected = HistoryList.SelectedItems.Cast<Commit>().OrderBy(c => HistoryList.Items.IndexOf(c)).ToArray();
        var version = ++detailGeneration;
        await Guard(async () =>
        {
            var diff = await git.CompareAsync(selected[1].Hash, selected[0].Hash, historyPath, lifetime.Token);
            if (disposed || version != detailGeneration) return;
            Documents.SelectedItem = CreateDocumentTab($"{selected[1].ShortHash} → {selected[0].ShortHash}", DocumentView.DiffEditor(diff.Display));
        });
    }

    private void ResumeLiveProblems()
    {
        showingCliSnapshot = false;
        RefreshLiveProblems();
    }
    private void RefreshLiveProblems()
    {
        if (disposed || showingCliSnapshot) return;
        var problems = languages.Store.Snapshot();
        ProblemsList.ItemsSource = problems;
        AnalysisStatus.Text = $"{languages.Status} · {problems.Count} problems · {DateTime.Now:HH:mm:ss}";
        foreach (var document in documents.Values)
            document.View.SetDiagnostics(languages.Store.ForFile(Path.GetFullPath(document.View.RelativePath, root)));
    }
    private async Task ReloadChangedDocumentsAsync(IReadOnlyList<WorkspaceWatcher.Change> changes)
    {
        if (disposed) return;
        foreach (var change in changes)
        {
            var relative = Path.GetRelativePath(root, change.Path).Replace('\\', '/');
            if (!documents.TryGetValue(relative, out var document)) continue;
            if (change.Type == 3)
            {
                document.View.SetDiagnostics([]);
                continue;
            }
            await UpdateDocumentAsync(document.View, diskChanged: true);
        }
    }
    private async void RestartLspClick(object sender, RoutedEventArgs e)
    {
        RestartLspButton.IsEnabled = false;
        AnalysisStatus.Text = "Restarting language servers...";
        try
        {
            await languages.RestartAsync(lifetime.Token);
            ResumeLiveProblems();
        }
        catch (Exception ex) { Log("Language server restart failed: " + ex.Message, true); }
        finally { if (!disposed) RestartLspButton.IsEnabled = profile.HasAnyLanguage; }
    }
    private async void AnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (analysisCancellation != null) return;
        if (!profile.HasAnyLanguage)
        {
            AnalysisStatus.Text = "No supported language project detected in this repository.";
            return;
        }
        analysisCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = analysisCancellation.Token;
        AnalyzeButton.IsEnabled = false; CancelAnalysisButton.IsEnabled = true;
        showingCliSnapshot = true;
        ProblemsList.ItemsSource = Array.Empty<Problem>();
        AnalysisStatus.Text = "Running CLI snapshot...";
        var findings = new List<Problem>(); var failed = false;
        var sources = new List<string>();
        if (profile.HasDart) sources.Add("Dart");
        if (profile.HasGo) sources.Add("Go");
        if (profile.HasPython) sources.Add("Python");
        if (profile.HasTypeScript) sources.Add("TypeScript");
        if (profile.HasRust) sources.Add("Rust");
        if (profile.HasCSharp) sources.Add("C#");
        if (profile.HasPhp) sources.Add("PHP");
        if (profile.HasCpp) sources.Add("C/C++");
        try
        {
            foreach (var source in sources)
            {
                try
                {
                    AnalysisStatus.Text = "CLI analyzing " + source + "...";
                    var result = source switch
                    {
                        "Dart" => await Diagnostics.DartStackAsync(root, token),
                        "Go" => await Diagnostics.GoAsync(profile.GoModuleDirectory ?? root, token),
                        "Python" => await Diagnostics.PythonAsync(root, token),
                        "TypeScript" => await Diagnostics.TypeScriptAsync(root, token),
                        "Rust" => await Diagnostics.RustAsync(root, token),
                        "C#" => await Diagnostics.CSharpAsync(root, token, profile.DotNetProjectPath),
                        "PHP" => await Diagnostics.PhpAsync(root, token),
                        _ => await Diagnostics.CppAsync(root, token)
                    };
                    findings.AddRange(result.Problems); failed |= result.Failed;
                    ProblemsList.ItemsSource = findings.ToArray();
                    Log(source + " CLI analysis\n" + result.Log);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { failed = true; Log(source + " CLI analysis failed: " + ex.Message); }
            }
            // Keep showingCliSnapshot true so live RefreshLiveProblems cannot wipe this overlay
            // until the next LSP publishDiagnostics or Restart language servers.
            AnalysisStatus.Text = $"CLI snapshot · {findings.Count} problems · {DateTime.Now:HH:mm:ss}" + (failed ? " · incomplete; see Output" : "") + " · live LSP resumes on next diagnostic update or Restart";
        }
        catch (OperationCanceledException)
        {
            AnalysisStatus.Text = "CLI snapshot cancelled; live LSP still active.";
            ResumeLiveProblems();
        }
        finally
        {
            analysisCancellation.Dispose(); analysisCancellation = null;
            if (!disposed) { AnalyzeButton.IsEnabled = profile.HasAnyLanguage; CancelAnalysisButton.IsEnabled = false; }
        }
    }

    private async void OpenRepositoryClick(object sender, RoutedEventArgs e)
    {
        if (closing || disposed) return;
        if (gitBusy) { StatusText.Text = "Wait for the Git operation to finish before opening another repository."; return; }
        string? path;
        try { path = App.PickRepositoryFolder(this); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, AppInfo.ProductName);
            return;
        }
        if (path == null) return;
        await OpenRepositoryAsync(path);
    }

    private async Task OpenRepositoryAsync(string path)
    {
        if (closing || disposed) return;
        if (gitBusy) { StatusText.Text = "Wait for the Git operation to finish before opening another repository."; return; }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) return;

        // Ask about terminals first so cancel does not leave document tabs already closed.
        if (terminal?.SessionCount > 0 &&
            MessageBox.Show(this, "Close PowerShell sessions and open another repository?", "Open Repository", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        foreach (var document in documents.Values.ToArray())
        {
            if (!document.View.IsDirty) continue;
            Documents.SelectedItem = document.Tab;
            if (!await TryCloseDocumentTabAsync(document.Tab)) return;
        }

        MainWindow next;
        try { next = new MainWindow(path, rememberLayout); }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not open repository:\n" + ex.Message, AppInfo.ProductName);
            return;
        }

        closing = true;
        lifetime.Cancel();
        analysisCancellation?.Cancel();
        foreach (var debounce in documentLspDebounce.Values) { debounce.Cancel(); debounce.Dispose(); }
        documentLspDebounce.Clear();
        try
        {
            if (rememberLayout)
            {
                if (panelVisible) panelHeight = PanelRow.ActualHeight;
                if (sidebarVisible) sidebarWidth = SidebarColumn.ActualWidth;
                AppPaths.EnsureRoot();
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Layout(Width, Height, panelHeight, sidebarWidth, true, sidebarVisible, true)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var dispose = languages.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(5000)) != dispose)
                Log("Language server shutdown timed out.");
        }
        catch (Exception ex) { Log("Language server dispose failed: " + ex.Message); }
        if (terminal != null)
        {
            try { await terminal.DisposeAsync(); }
            catch (Exception ex) { Log("Terminal dispose failed: " + ex.Message); }
        }
        disposed = true;
        Closing -= CloseWindow;
        Application.Current.MainWindow = next;
        next.Show();
        Close();
    }
    private void CancelAnalysisClick(object sender, RoutedEventArgs e) => analysisCancellation?.Cancel();
    private async void ProblemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProblemsList.SelectedItem is not Problem problem) return;
        await Guard(() => OpenFileAsync(Path.GetRelativePath(root, problem.File).Replace('\\', '/'), false, problem.Line, problem.Column));
    }

    private void SetPanel(bool visible)
    {
        if (!visible && panelVisible) panelHeight = Math.Max(100, PanelRow.ActualHeight);
        panelVisible = visible;
        BottomPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PanelRow.MinHeight = visible ? 100 : 0; PanelRow.Height = new(visible ? panelHeight : 0);
        PanelDivider.Height = new(visible ? 5 : 0);
        terminal?.Fit();
    }
    private Task EnsureTerminalAsync() => OpenTerminalAsync(null);

    private async Task OpenTerminalAsync(string? workingDirectory)
    {
        if (disposed || closing) return;
        if (terminal == null)
        {
            terminal = new(root); terminal.Error += message => Log(message, true);
            terminal.Shortcut += key =>
            {
                if (key == "panel") SetPanel(false);
                else if (key == "sidebar") SetSidebar(!sidebarVisible);
                else if (key == "problems") ShowProblems(this, new());
                else if (key == "terminal") ShowTerminal(this, new());
            };
            TerminalHost.Children.Clear(); TerminalHost.Children.Add(terminal);
        }
        if (workingDirectory == null) await terminal.InitializeAsync();
        else await terminal.AddSessionAsync(workingDirectory);
        // SelectionChanged runs before TabControl finishes moving focus and attaching its
        // content. Focus after that work, and recheck selection after initialization/yield.
        await Dispatcher.InvokeAsync(() =>
        {
            if (disposed || closing || !panelVisible || !TerminalTab.IsSelected || !IsActive) return;
            terminal.Fit();
            terminal.FocusTerminal();
        }, DispatcherPriority.Input);
    }
    private void SetSidebar(bool visible)
    {
        if (!visible && sidebarVisible) sidebarWidth = Math.Max(220, SidebarColumn.ActualWidth);
        sidebarVisible = visible;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.MinWidth = visible ? 220 : 0; SidebarColumn.Width = new(visible ? sidebarWidth : 0);
        SidebarDivider.Width = new(visible ? 5 : 0);
    }
    private void ToggleHistoryVisibility(object sender, RoutedEventArgs e)
    {
        if (!historyCollapsed)
        {
            if (changesCollapsed) ToggleHistorySize(this, new());
            var changesHeight = SourceChangesRow.ActualHeight;
            var historyHeight = SourceHistoryRow.ActualHeight;
            if (changesHeight > 0 && historyHeight > 0) historyRestoreRatio = changesHeight / historyHeight;
            historyCollapsed = true;
            HistoryBody.Visibility = Visibility.Collapsed;
            SourceHistorySplitter.Visibility = Visibility.Collapsed;
            SourceHistoryDividerRow.Height = new(0);
            SourceHistoryRow.Height = GridLength.Auto;
            HistoryVisibilityButton.Content = "+";
            HistoryVisibilityButton.ToolTip = "Show history list";
            AutomationProperties.SetName(HistoryVisibilityButton, "Show history list");
        }
        else
        {
            historyCollapsed = false;
            HistoryBody.Visibility = Visibility.Visible;
            SourceHistoryDividerRow.Height = new(5);
            SourceHistorySplitter.Visibility = Visibility.Visible;
            SourceChangesRow.Height = new(historyRestoreRatio, GridUnitType.Star);
            SourceHistoryRow.Height = new(1, GridUnitType.Star);
            HistoryVisibilityButton.Content = "−";
            HistoryVisibilityButton.ToolTip = "Collapse history list";
            AutomationProperties.SetName(HistoryVisibilityButton, "Collapse history list");
        }
    }
    private void ToggleHistorySize(object sender, RoutedEventArgs e)
    {
        if (!changesCollapsed)
        {
            if (historyCollapsed) ToggleHistoryVisibility(this, new());
            var changesHeight = SourceChangesRow.ActualHeight;
            var historyHeight = SourceHistoryRow.ActualHeight;
            if (changesHeight > 0 && historyHeight > 0) historyRestoreRatio = changesHeight / historyHeight;
            changesCollapsed = true;
            SourceChanges.Visibility = Visibility.Collapsed;
            SourceChangesRow.Height = new(0);
            SourceHistorySplitter.Visibility = Visibility.Collapsed;
            SourceHistoryDividerRow.Height = new(0);
            SourceHistoryRow.Height = new(1, GridUnitType.Star);
            HistorySizeButton.Content = "⤡";
            HistorySizeButton.ToolTip = "Restore changes and history layout";
            AutomationProperties.SetName(HistorySizeButton, "Restore changes and history layout");
        }
        else
        {
            changesCollapsed = false;
            SourceChanges.Visibility = Visibility.Visible;
            SourceChangesRow.Height = new(historyRestoreRatio, GridUnitType.Star);
            SourceHistorySplitter.Visibility = Visibility.Visible;
            SourceHistoryDividerRow.Height = new(5);
            SourceHistoryRow.Height = new(1, GridUnitType.Star);
            HistorySizeButton.Content = "⤢";
            HistorySizeButton.ToolTip = "Expand history area";
            AutomationProperties.SetName(HistorySizeButton, "Expand history area");
        }
    }
    private void ShowProblems(object sender, RoutedEventArgs e) { SetPanel(true); BottomPanel.SelectedItem = ProblemsTab; }
    private void ShowTerminal(object sender, RoutedEventArgs e)
    {
        SetPanel(true);
        // SelectionChanged only runs when the tab actually changes; Ctrl+` while Terminal is
        // already selected must still move keyboard focus out of Source Control / commit fields.
        var alreadySelected = ReferenceEquals(BottomPanel.SelectedItem, TerminalTab);
        BottomPanel.SelectedItem = TerminalTab;
        if (alreadySelected) _ = Guard(EnsureTerminalAsync);
    }
    private async void PanelSelection(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != BottomPanel || BottomPanel.SelectedItem != TerminalTab || disposed) return;
        await Guard(EnsureTerminalAsync);
    }
    private async void TerminalTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Clicking an already selected tab does not raise SelectionChanged.
        if (TerminalTab.IsSelected) await Guard(EnsureTerminalAsync);
    }
    private void KeyPressed(object sender, KeyEventArgs e)
    {
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.F5) { RefreshClick(this, new()); e.Handled = true; }
        else if (control && e.Key == Key.J)
        {
            var show = !panelVisible;
            SetPanel(show);
            if (show && ReferenceEquals(BottomPanel.SelectedItem, TerminalTab)) _ = Guard(EnsureTerminalAsync);
            e.Handled = true;
        }
        else if (control && e.Key == Key.B) { SetSidebar(!sidebarVisible); e.Handled = true; }
        else if (control && e.Key == Key.Oem3) { ShowTerminal(this, new()); e.Handled = true; }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.F) { ShowSearch(); e.Handled = true; }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.M) { ShowProblems(this, new()); e.Handled = true; }
        else if (control && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && e.Key == Key.R)
        {
            if (Documents.SelectedItem is TabItem { Content: DocumentView revertView } && revertView.CanEdit && revertView.IsDirty)
            {
                _ = Guard(() => RevertDocumentAsync(revertView));
                e.Handled = true;
            }
        }
        else if (control && e.Key == Key.S)
        {
            if (Documents.SelectedItem is TabItem { Content: DocumentView saveView } && saveView.CanEdit)
            {
                _ = Guard(() => SaveDocumentAsync(saveView));
                e.Handled = true;
            }
        }
        else if (control && e.Key == Key.Enter && CommitMessage.IsKeyboardFocusWithin) { CommitClick(this, new()); e.Handled = true; }
    }
    private void LoadLayout()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var settings = JsonSerializer.Deserialize<Layout>(File.ReadAllText(settingsPath));
            if (settings == null) return;
            Width = Math.Clamp(settings.Width, 980, 3000); Height = Math.Clamp(settings.Height, 640, 2000);
            panelHeight = Math.Clamp(settings.PanelHeight, 100, 600); sidebarWidth = Math.Clamp(settings.SidebarWidth, 220, 700);
            SidebarColumn.Width = new(sidebarWidth); SetSidebar(settings.Sidebar);
            SetPanel(true); StatusBar.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private async void CloseWindow(object? sender, CancelEventArgs e)
    {
        if (disposed) return;
        e.Cancel = true;
        if (closing) return;
        if (gitBusy) { StatusText.Text = "Wait for the Git operation to finish before closing."; return; }
        foreach (var document in documents.Values.ToArray())
        {
            if (!document.View.IsDirty) continue;
            Documents.SelectedItem = document.Tab;
            if (!await TryCloseDocumentTabAsync(document.Tab)) return;
        }
        if (terminal?.SessionCount > 0 && MessageBox.Show(this, "Close PowerShell sessions and their child processes?", "Close workspace", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        closing = true; lifetime.Cancel(); analysisCancellation?.Cancel();
        foreach (var debounce in documentLspDebounce.Values) { debounce.Cancel(); debounce.Dispose(); }
        documentLspDebounce.Clear();
        try
        {
            if (rememberLayout)
            {
                if (panelVisible) panelHeight = PanelRow.ActualHeight;
                if (sidebarVisible) sidebarWidth = SidebarColumn.ActualWidth;
                AppPaths.EnsureRoot();
                File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Layout(Width, Height, panelHeight, sidebarWidth, true, sidebarVisible, true)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            var dispose = languages.DisposeAsync().AsTask();
            if (await Task.WhenAny(dispose, Task.Delay(5000)) != dispose)
                Log("Language server shutdown timed out.");
        }
        catch (Exception ex) { Log("Language server dispose failed: " + ex.Message); }
        if (terminal != null) await terminal.DisposeAsync();
        disposed = true;
        _ = Dispatcher.BeginInvoke(Close);
    }
    private sealed record StatusDiffTabKey(string Path, bool Staged);
    private sealed record Layout(double Width, double Height, double PanelHeight, double SidebarWidth, bool Panel, bool Sidebar, bool Status);
}
