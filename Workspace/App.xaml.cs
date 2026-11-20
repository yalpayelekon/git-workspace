using System.IO;
using System.Windows;
using GitWorkspace.Core;
using Microsoft.Win32;

namespace GitWorkspace;

public partial class App : Application
{
    public bool UseProvidedWindow { get; init; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UseProvidedWindow) return;
        try
        {
            var root = ResolveRepository(e.Args);
            if (root == null)
            {
                Shutdown(0);
                return;
            }
            MainWindow = new MainWindow(root);
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, AppInfo.ProductName);
            Shutdown(1);
        }
    }

    public static string? ResolveRepository(string[] args)
    {
        if (TryGetRepositoryArgument(args, out var fromArgs))
        {
            if (!RecentRepos.IsGitRepository(fromArgs))
                throw new InvalidOperationException($"Repository metadata was not found at '{fromArgs}'.");
            return RecentRepos.Normalize(fromArgs);
        }

        var recent = RecentRepos.LatestValid();
        if (recent != null) return recent;

        try { return FindRepository(AppContext.BaseDirectory); }
        catch (DirectoryNotFoundException) { }

        return PickRepositoryFolder();
    }

    public static bool TryGetRepositoryArgument(string[] args, out string path)
    {
        path = "";
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--repository", StringComparison.OrdinalIgnoreCase)) continue;
            if (i + 1 >= args.Length) throw new InvalidOperationException("Missing path after --repository.");
            path = RecentRepos.Normalize(args[i + 1]);
            return path.Length > 0;
        }
        return false;
    }

    public static string? PickRepositoryFolder(Window? owner = null)
    {
        while (true)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Open Git Repository",
                Multiselect = false
            };
            var ok = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
            if (!ok || string.IsNullOrWhiteSpace(dialog.FolderName)) return null;

            var path = RecentRepos.Normalize(dialog.FolderName);
            if (RecentRepos.IsGitRepository(path)) return path;

            var enclosing = RecentRepos.FindEnclosingRepository(path);
            if (enclosing != null) return enclosing;

            var message = "The selected folder is not a Git repository (.git missing).\n\nChoose another folder?";
            var retry = owner == null
                ? MessageBox.Show(message, AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                : MessageBox.Show(owner, message, AppInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (retry != MessageBoxResult.Yes) return null;
        }
    }

    private static string FindRepository(string directory)
    {
        for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
            if (RecentRepos.IsGitRepository(current.FullName))
                return current.FullName;
        throw new DirectoryNotFoundException("Cannot locate this checkout.");
    }
}
