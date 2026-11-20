using System.IO;

namespace GitWorkspace.Core;

public sealed record WorkspaceProfile(
    bool HasDart,
    bool HasGo,
    bool HasPython,
    bool HasTypeScript,
    bool HasRust,
    bool HasCSharp,
    string? GoModuleDirectory)
{
    public bool HasAnyLanguage =>
        HasDart || HasGo || HasPython || HasTypeScript || HasRust || HasCSharp;

    public static WorkspaceProfile Detect(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var hasDart = File.Exists(Path.Combine(fullRoot, "pubspec.yaml"));
        string? goModule = null;
        if (File.Exists(Path.Combine(fullRoot, "go.mod")))
            goModule = fullRoot;
        else
        {
            var nested = Path.Combine(fullRoot, "go");
            if (File.Exists(Path.Combine(nested, "go.mod")))
                goModule = nested;
        }
        var hasPython =
            File.Exists(Path.Combine(fullRoot, "pyproject.toml")) ||
            File.Exists(Path.Combine(fullRoot, "requirements.txt")) ||
            File.Exists(Path.Combine(fullRoot, "setup.py")) ||
            File.Exists(Path.Combine(fullRoot, "setup.cfg")) ||
            File.Exists(Path.Combine(fullRoot, "Pipfile"));
        var hasTypeScript =
            File.Exists(Path.Combine(fullRoot, "package.json")) ||
            File.Exists(Path.Combine(fullRoot, "tsconfig.json")) ||
            File.Exists(Path.Combine(fullRoot, "jsconfig.json"));
        var hasRust = File.Exists(Path.Combine(fullRoot, "Cargo.toml"));
        var hasCSharp = HasDotNetProject(fullRoot);
        return new(hasDart, goModule != null, hasPython, hasTypeScript, hasRust, hasCSharp, goModule);
    }

    /// <summary>Root .sln/.csproj, or one nested project folder (e.g. Workspace/Workspace.csproj).</summary>
    private static bool HasDotNetProject(string root)
    {
        try
        {
            if (Directory.EnumerateFiles(root, "*.sln").Any()) return true;
            if (Directory.EnumerateFiles(root, "*.csproj").Any()) return true;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("target", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Directory.EnumerateFiles(dir, "*.sln").Any()) return true;
                if (Directory.EnumerateFiles(dir, "*.csproj").Any()) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }
}
