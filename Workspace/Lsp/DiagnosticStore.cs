using System.Collections.Concurrent;
using System.IO;
using GitWorkspace.Core;

namespace GitWorkspace.Lsp;

public sealed class DiagnosticStore
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<Problem>> byFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public event Action? Changed;

    public void Replace(string source, string filePath, IReadOnlyList<Problem> problems)
    {
        var full = Path.GetFullPath(filePath);
        lock (gate)
        {
            var existing = byFile.TryGetValue(full, out var current) ? current : Array.Empty<Problem>();
            var kept = existing.Where(p => !p.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();
            kept.AddRange(problems);
            if (kept.Count == 0) byFile.TryRemove(full, out _);
            else byFile[full] = kept;
        }
        Changed?.Invoke();
    }

    public void ClearSource(string source)
    {
        lock (gate)
        {
            foreach (var path in byFile.Keys.ToArray())
            {
                if (!byFile.TryGetValue(path, out var current)) continue;
                var kept = current.Where(p => !p.Source.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();
                if (kept.Count == 0) byFile.TryRemove(path, out _);
                else byFile[path] = kept;
            }
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        byFile.Clear();
        Changed?.Invoke();
    }

    public IReadOnlyList<Problem> Snapshot()
    {
        lock (gate)
        {
            return byFile.Values.SelectMany(v => v)
                .OrderBy(p => p.Severity, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.File, StringComparer.OrdinalIgnoreCase)
                .ThenBy(p => p.Line).ThenBy(p => p.Column)
                .ToArray();
        }
    }

    public IReadOnlyList<Problem> ForFile(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        return byFile.TryGetValue(full, out var problems) ? problems : Array.Empty<Problem>();
    }
}
