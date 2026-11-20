using System.IO;

namespace GitWorkspace.Core;

public sealed record WorkspaceProfile(bool HasDart, bool HasGo, bool HasPython, string? GoModuleDirectory)
{
    public bool HasAnyLanguage => HasDart || HasGo || HasPython;

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
        return new(hasDart, goModule != null, hasPython, goModule);
    }
}
