using System.IO;

namespace GitWorkspace.Core;

public static class AppInfo
{
    public const string ProductName = "GitWorkspace";
    public const string AssemblyName = "GitWorkspace";
    public const string TerminalHostName = "terminal.gitworkspace.local";
    public const string TerminalOrigin = "https://terminal.gitworkspace.local/";
    public const string BatchEnvPrefix = "GITWORKSPACE_BATCH_";
}

public static class AppPaths
{
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.ProductName);

    public static string LayoutFile => Path.Combine(RootDirectory, "layout.json");
    public static string RecentReposFile => Path.Combine(RootDirectory, "recent-repos.json");
    public static string WebView2UserData => Path.Combine(RootDirectory, "WebView2");
    public static string GitLockRecoveryDirectory => Path.Combine(RootDirectory, "recovered-git-locks");

    public static void EnsureRoot() => Directory.CreateDirectory(RootDirectory);
}
