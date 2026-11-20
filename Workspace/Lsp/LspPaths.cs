using System.IO;
using System.Text;

namespace GitWorkspace.Lsp;

public static class LspPaths
{
    public static string PathToUri(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        if (full.Length >= 2 && full[1] == ':')
            return "file:///" + EncodePath(full);
        return "file://" + EncodePath(full);
    }

    public static string? UriToPath(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeFile)
            return null;
        return Path.GetFullPath(parsed.LocalPath);
    }

    public static string? LanguageId(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".dart" => "dart",
        ".go" => "go",
        ".py" or ".pyi" => "python",
        _ => null
    };

    public static bool IsWatched(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("pubspec.yaml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pubspec.lock", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("go.mod", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("go.sum", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("requirements.txt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("setup.py", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("setup.cfg", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Pipfile", StringComparison.OrdinalIgnoreCase))
            return true;
        return LanguageId(path) != null;
    }

    public static bool IsIgnored(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return true;
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in parts)
        {
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("build", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".dart_tool", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("__pycache__", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".venv", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("venv", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".mypy_cache", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".pytest_cache", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(".ruff_cache", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string EncodePath(string path)
    {
        var builder = new StringBuilder(path.Length + 16);
        foreach (var character in path)
        {
            if (character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '/' or ':' or '-' or '_' or '.' or '~')
                builder.Append(character);
            else if (character == ' ') builder.Append("%20");
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(character.ToString()))
                    builder.Append('%').Append(b.ToString("X2"));
            }
        }
        return builder.ToString();
    }
}
