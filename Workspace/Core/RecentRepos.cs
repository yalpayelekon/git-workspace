using System.IO;
using System.Text.Json;

namespace GitWorkspace.Core;

public static class RecentRepos
{
    private const int MaxEntries = 10;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(AppPaths.RecentReposFile)) return [];
            var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(AppPaths.RecentReposFile)) ?? [];
            return paths
                .Select(Normalize)
                .Where(path => path.Length > 0 && IsGitRepository(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static void Remember(string repository)
    {
        var path = Normalize(repository);
        if (path.Length == 0 || !IsGitRepository(path)) return;
        try
        {
            AppPaths.EnsureRoot();
            var next = new List<string> { path };
            foreach (var existing in Load())
            {
                if (!string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                    next.Add(existing);
                if (next.Count >= MaxEntries) break;
            }
            File.WriteAllText(AppPaths.RecentReposFile, JsonSerializer.Serialize(next, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Layout/recent persistence must not crash startup (AV locks, concurrent instances).
        }
    }

    public static string? LatestValid() => Load().FirstOrDefault();

    public static bool IsGitRepository(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var root = Normalize(path);
        return Directory.Exists(Path.Combine(root, ".git")) || File.Exists(Path.Combine(root, ".git"));
    }

    /// <summary>If <paramref name="path"/> is inside a Git work tree, return that root; otherwise null.</summary>
    public static string? FindEnclosingRepository(string path)
    {
        var current = Normalize(path);
        if (current.Length == 0) return null;
        for (var dir = new DirectoryInfo(File.Exists(current) ? Path.GetDirectoryName(current)! : current);
             dir != null;
             dir = dir.Parent)
        {
            if (IsGitRepository(dir.FullName)) return dir.FullName;
        }
        return null;
    }

    public static string Normalize(string path)
    {
        var trimmed = path.Trim().Trim('"');
        if (trimmed.Length == 0) return "";
        try { return Path.GetFullPath(trimmed); }
        catch { return ""; }
    }
}
