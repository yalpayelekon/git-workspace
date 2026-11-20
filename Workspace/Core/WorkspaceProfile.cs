using System.IO;

namespace GitWorkspace.Core;

public sealed record WorkspaceProfile(
    bool HasDart,
    bool HasGo,
    bool HasPython,
    bool HasTypeScript,
    bool HasRust,
    bool HasCSharp,
    bool HasPhp,
    bool HasCpp,
    string? GoModuleDirectory,
    string? DotNetProjectPath)
{
    public bool HasAnyLanguage =>
        HasDart || HasGo || HasPython || HasTypeScript || HasRust || HasCSharp || HasPhp || HasCpp;

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
        var dotNetProject = FindDotNetTarget(fullRoot);
        var hasPhp =
            File.Exists(Path.Combine(fullRoot, "composer.json")) ||
            File.Exists(Path.Combine(fullRoot, "phpunit.xml")) ||
            File.Exists(Path.Combine(fullRoot, "phpunit.xml.dist"));
        var hasCpp =
            File.Exists(Path.Combine(fullRoot, "compile_commands.json")) ||
            File.Exists(Path.Combine(fullRoot, "CMakeLists.txt")) ||
            File.Exists(Path.Combine(fullRoot, "Makefile")) ||
            File.Exists(Path.Combine(fullRoot, "makefile")) ||
            File.Exists(Path.Combine(fullRoot, "meson.build")) ||
            File.Exists(Path.Combine(fullRoot, ".clangd")) ||
            File.Exists(Path.Combine(fullRoot, "build", "compile_commands.json"));
        return new(hasDart, goModule != null, hasPython, hasTypeScript, hasRust, dotNetProject != null,
            hasPhp, hasCpp, goModule, dotNetProject);
    }

    /// <summary>Prefer root .sln, then root .csproj, then the first nested project/solution.</summary>
    public static string? FindDotNetTarget(string root)
    {
        try
        {
            var solution = Directory.EnumerateFiles(root, "*.sln").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (solution != null) return solution;
            var project = Directory.EnumerateFiles(root, "*.csproj").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (project != null) return project;
            foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("target", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("vendor", StringComparison.OrdinalIgnoreCase))
                    continue;
                solution = Directory.EnumerateFiles(dir, "*.sln").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (solution != null) return solution;
                project = Directory.EnumerateFiles(dir, "*.csproj").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (project != null) return project;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
