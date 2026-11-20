using System.IO;

namespace GitWorkspace.Core;

public sealed record GitFile(string Path, string? OriginalPath, char Index, char WorkingTree, int? Added = null, int? Deleted = null)
{
    public bool Untracked => Index == '?' && WorkingTree == '?';
    public bool Conflicted => Index == 'U' || WorkingTree == 'U' || (Index, WorkingTree) is ('A', 'A') or ('D', 'D');
    public bool Staged => !Untracked && !Conflicted && Index != ' ';
    public bool Unstaged => Untracked || Conflicted || WorkingTree != ' ';
    public string State => Conflicted ? "Conflict" : $"{Index}{WorkingTree}";
    public IEnumerable<string> Paths => OriginalPath == null ? [Path] : [Path, OriginalPath];
    public string AddedText => Added is > 0 ? $"+{Added}" : "";
    public string DeletedText => Deleted is > 0 ? $"-{Deleted}" : "";
}
public readonly record struct DiffLineCount(int Added, int Deleted);

// WPF selection keys must stay stable while the changed-file cache is populated.
public sealed class Commit(string hash, string[] parents, string author, string date, string subject, string refs)
{
    public string Hash { get; } = hash;
    public string[] Parents { get; } = parents;
    public string Author { get; } = author;
    public string Date { get; } = date;
    public string Subject { get; } = subject;
    public string Refs { get; } = refs;
    public string ShortHash => Hash[..Math.Min(8, Hash.Length)];
    public GraphRow? Graph { get; set; }
    public System.Collections.ObjectModel.ObservableCollection<CommitFile> Files { get; } = [];
    public bool FilesLoaded { get; set; }
}
public sealed record CommitFile(string CommitHash, string? ParentHash, string Path, string OldPath, string State, string OldBlob, string NewBlob)
{
    public string Label => State.StartsWith('R') ? $"{OldPath} → {Path}" : Path;
}
public sealed record RevisionDiff(FilePreview Old, FilePreview New, string Patch);
public sealed record GraphEdge(int From, int To, string ColorKey);
public sealed record GraphRow(int Node, string Hash, IReadOnlyList<GraphEdge> Above, IReadOnlyList<GraphEdge> Below, int Width);

public sealed class GitService(string root)
{
    private readonly SemaphoreSlim commandGate = new(1, 1);
    public event Action<string>? Log;

    public async Task<CommandResult> RunAsync(IEnumerable<string> arguments, CancellationToken cancellation = default, int maximum = 2 * 1024 * 1024)
    {
        var args = arguments.ToArray();
        await commandGate.WaitAsync(cancellation);
        try
        {
            var writesRepository = args.FirstOrDefault() is "add" or "restore" or "rm" or "reset" or "commit"
                or "pull" or "push" or "fetch" or "merge" or "checkout" or "switch" or "mv" or "rebase" or "cherry-pick" or "revert";
            string[] lockPaths = [];
            if (writesRepository)
            {
                var paths = await RunCoreAsync(new[] { "rev-parse", "--path-format=absolute" }
                    .Concat(GitLockRecovery.GitPaths.SelectMany(path => new[] { "--git-path", path })), cancellation, maximum);
                if (paths.ExitCode != 0) return paths;
                lockPaths = GitLockRecovery.ExpandPaths(paths.RequireSuccess());
                var blocked = await GitLockRecovery.PrepareAsync(lockPaths, message => Log?.Invoke(message), cancellation);
                if (blocked != null) return new(128, "", blocked);
            }

            var result = await RunCoreAsync(args, cancellation, maximum);
            // Retry only a lock collision. Authentication, conflicts and hook failures need their own resolution.
            if (writesRepository && result.ExitCode != 0 && result.Error.Contains(".lock", StringComparison.Ordinal)
                && result.Error.Contains("File exists", StringComparison.OrdinalIgnoreCase))
            {
                Log?.Invoke("Git lock collision detected; checking before one retry.");
                var blocked = await GitLockRecovery.PrepareAsync(lockPaths, message => Log?.Invoke(message), cancellation);
                if (blocked != null) return result with { Error = result.Error + "\n" + blocked };
                return await RunCoreAsync(args, cancellation, maximum);
            }
            return result;
        }
        finally { commandGate.Release(); }
    }

    private Task<CommandResult> RunCoreAsync(IEnumerable<string> arguments, CancellationToken cancellation, int maximum) =>
        Processes.RunAsync(Processes.Find("git.exe") ?? throw new InvalidOperationException("Install Git for Windows and reopen the app."),
            new[] { "--no-optional-locks", "--literal-pathspecs", "-C", root }.Concat(arguments), root, cancellation, maximum);

    public async Task<IReadOnlyList<GitFile>> StatusAsync(CancellationToken cancellation = default) =>
        ParseStatus((await RunAsync(["status", "--porcelain=v1", "-z", "--untracked-files=all"], cancellation, 16 * 1024 * 1024)).RequireSuccess());

    public async Task<(IReadOnlyDictionary<string, DiffLineCount> Staged, IReadOnlyDictionary<string, DiffLineCount> Unstaged)>
        LineCountsAsync(IEnumerable<GitFile> files, CancellationToken cancellation = default)
    {
        var staged = await NumstatAsync(cached: true, cancellation);
        var unstaged = new Dictionary<string, DiffLineCount>(await NumstatAsync(cached: false, cancellation), StringComparer.Ordinal);
        foreach (var file in files.Where(f => f.Untracked).GroupBy(f => f.Path, StringComparer.Ordinal).Select(g => g.First()))
        {
            var added = await CountUntrackedLinesAsync(file.Path, cancellation);
            if (added != null) unstaged[file.Path] = new(added.Value, 0);
        }
        return (staged, unstaged);
    }

    public static GitFile WithLineCount(GitFile file, IReadOnlyDictionary<string, DiffLineCount> counts) =>
        counts.TryGetValue(file.Path, out var count) ? file with { Added = count.Added, Deleted = count.Deleted } : file;

    public static DiffLineCount SumLineCounts(IEnumerable<GitFile> files)
    {
        var added = 0;
        var deleted = 0;
        foreach (var file in files)
        {
            added += file.Added ?? 0;
            deleted += file.Deleted ?? 0;
        }
        return new(added, deleted);
    }

    public async Task<IReadOnlyDictionary<string, DiffLineCount>> NumstatAsync(bool cached, CancellationToken cancellation = default)
    {
        // -z keeps literal path bytes (no core.quotepath C-quotes) and NUL-terminates records.
        var args = cached
            ? new[] { "diff", "--cached", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--find-renames" }
            : new[] { "diff", "--numstat", "-z", "--no-ext-diff", "--no-textconv", "--find-renames" };
        var result = await RunAsync(args, cancellation, 16 * 1024 * 1024);
        if (result.ExitCode != 0) return new Dictionary<string, DiffLineCount>(StringComparer.Ordinal);
        return ParseNumstat(result.Output);
    }

    public static IReadOnlyDictionary<string, DiffLineCount> ParseNumstat(string output)
    {
        var map = new Dictionary<string, DiffLineCount>(StringComparer.Ordinal);
        // Ordinary: "added\tdeleted\tpath\0"
        // Rename/copy: "added\tdeleted\t\0oldPath\0newPath\0"
        var parts = output.Split('\0');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0) continue;
            var fields = part.Split('\t');
            if (fields.Length < 2) continue;
            var binary = fields[0] == "-" || fields[1] == "-";
            var rename = fields.Length < 3 || fields[2].Length == 0;
            string? path = null;
            if (rename)
            {
                if (i + 2 >= parts.Length) break;
                i += 2;
                path = parts[i];
            }
            else path = fields[2];
            if (binary || path is not { Length: > 0 }) continue;
            if (!int.TryParse(fields[0], out var added) || !int.TryParse(fields[1], out var deleted)) continue;
            map[path] = new(added, deleted);
        }
        return map;
    }

    public async Task<int?> CountUntrackedLinesAsync(string relativePath, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var absolute = Path.GetFullPath(relativePath, root);
        if (!File.Exists(absolute) || Directory.Exists(absolute)) return null;
        var preview = await Files.ReadAsync(absolute);
        if (!preview.IsText) return null;
        if (preview.Text.Length == 0) return 0;
        var lines = 1;
        for (var i = 0; i < preview.Text.Length; i++)
            if (preview.Text[i] == '\n') lines++;
        if (preview.Text[^1] == '\n') lines--;
        return Math.Max(lines, 0);
    }

    public static IReadOnlyList<GitFile> ParseStatus(string output)
    {
        var result = new List<GitFile>();
        var parts = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length < 4 || part[2] != ' ') throw new FormatException("Unexpected Git status record.");
            string? source = null;
            if (part[0] is 'R' or 'C' || part[1] is 'R' or 'C')
            {
                if (++i >= parts.Length) throw new FormatException("Incomplete Git rename record.");
                source = parts[i];
            }
            result.Add(new(part[3..], source, part[0], part[1]));
        }
        return result;
    }

    public Task<CommandResult> StageAsync(IEnumerable<GitFile> files, CancellationToken cancellation = default) =>
        RunAsync(new[] { "add", "-A", "--" }.Concat(files.SelectMany(f => f.Paths).Distinct()), cancellation);

    public async Task<CommandResult> UnstageAsync(IEnumerable<GitFile> files, CancellationToken cancellation = default)
    {
        var paths = files.SelectMany(f => f.Paths).Distinct().ToArray();
        var head = await RunAsync(["rev-parse", "--verify", "HEAD"], cancellation);
        // An initial commit has no HEAD; remove only index entries, preserving working files.
        return await RunAsync((head.ExitCode == 0 ? new[] { "restore", "--staged", "--" } : new[] { "rm", "--cached", "--force", "--" }).Concat(paths), cancellation);
    }

    public async Task<CommandResult> DiscardAsync(IEnumerable<GitFile> files, CancellationToken cancellation = default)
    {
        var list = files.ToArray();
        var tracked = list.Where(file => !file.Untracked).SelectMany(file => file.Paths).Distinct().ToArray();
        var untracked = list.Where(file => file.Untracked).SelectMany(file => file.Paths).Distinct().ToArray();
        CommandResult? result = null;
        if (tracked.Length > 0)
        {
            // Worktree only: leave the index alone so staged hunks stay staged.
            result = await RunAsync(new[] { "restore", "--worktree", "--" }.Concat(tracked), cancellation);
            if (result.ExitCode != 0) return result;
        }
        foreach (var path in untracked)
        {
            var absolute = Path.GetFullPath(path, root);
            try
            {
                DeleteUntrackedPath(absolute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return new(1, "", $"Unable to discard untracked path '{path}': {ex.Message}");
            }
        }
        return result ?? new(0, "", "");
    }

    private void DeleteUntrackedPath(string absolute)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!IsUnderRoot(absolute, fullRoot))
            throw new InvalidOperationException("Path escapes the repository root.");
        if (!File.Exists(absolute) && !Directory.Exists(absolute)) return;

        var attributes = File.GetAttributes(absolute);
        // Symlinks/junctions must be removed as the link node; never recurse through them.
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(absolute);
            else File.Delete(absolute);
            return;
        }
        if (PathCrossesReparsePoint(absolute, fullRoot))
            throw new InvalidOperationException("Path crosses a directory link; refusing to delete the target.");
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.EnumerateFileSystemEntries(absolute))
                DeleteUntrackedPath(child);
            Directory.Delete(absolute);
        }
        else File.Delete(absolute);
    }

    private static bool IsUnderRoot(string absolute, string fullRoot)
    {
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(absolute);
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathCrossesReparsePoint(string absolute, string fullRoot)
    {
        var current = Path.GetDirectoryName(Path.GetFullPath(absolute));
        while (!string.IsNullOrEmpty(current) && IsUnderRoot(current, fullRoot)
            && !current.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    public Task<CommandResult> DiffAsync(GitFile file, bool staged, CancellationToken cancellation = default) =>
        RunAsync(new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color" }
            .Concat(staged ? ["--cached"] : Array.Empty<string>()).Concat(["--"]).Concat(file.Paths), cancellation);

    public async Task<RevisionDiff> StatusDiffAsync(GitFile file, bool staged, CancellationToken cancellation = default)
    {
        async Task<FilePreview> Blob(string hash)
        {
            var size = await RunAsync(["cat-file", "-s", hash], cancellation);
            if (size.ExitCode != 0 || !long.TryParse(size.Output.Trim(), out var length)) return new("Unable to read Git revision.", false);
            if (length > Files.PreviewLimit) return new("Revision exceeds the 2 MB preview limit.", false);
            var content = await RunAsync(["cat-file", "blob", hash], cancellation);
            if (content.Truncated || content.ExitCode != 0) return new("Unable to preview revision: " + content.Display, false);
            if (content.Output.Contains('\0') || content.Output.Contains('\uFFFD')) return new("Binary or unsupported text encoding: revision preview is unavailable.", false);
            return new(content.Output, true);
        }
        async Task<FilePreview?> IndexBlob(string path)
        {
            var result = await RunAsync(["ls-files", "--stage", "-z", "--", path], cancellation);
            var record = result.RequireSuccess().Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (record == null) return null;
            var separator = record.IndexOf('\t');
            if (separator < 0) throw new FormatException("Unexpected Git index record.");
            var fields = record[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 3 || fields[2] != "0") return new("File has unresolved index stages.", false);
            return await Blob(fields[1]);
        }
        async Task<FilePreview> HeadBlob(string path)
        {
            var hash = await RunAsync(["rev-parse", "--verify", "HEAD:" + path], cancellation);
            return hash.ExitCode == 0 ? await Blob(hash.Output.Trim()) : new("", true);
        }

        var index = await IndexBlob(file.Path);
        var head = staged ? await HeadBlob(file.OriginalPath ?? file.Path) : await HeadBlob(file.Path);
        FilePreview before;
        FilePreview after;
        if (staged)
        {
            before = head;
            after = index ?? new("", true);
        }
        else
        {
            before = index ?? head;
            after = File.Exists(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)))
                ? await Files.ReadAsync(Path.Combine(root, file.Path.Replace('/', Path.DirectorySeparatorChar)))
                : new("", true);
        }

        var patchResult = await DiffAsync(file, staged, cancellation);
        if (patchResult.Truncated)
            return new(new("Diff exceeds the 2 MB preview limit.", false), new("Diff exceeds the 2 MB preview limit.", false), "");
        var patch = patchResult.ExitCode == 0 ? patchResult.Output : "";
        if (file.Untracked && !staged && before.IsText && after.IsText)
        {
            var lines = after.Text.Split('\n');
            var count = lines.Length - (after.Text.EndsWith('\n') ? 1 : 0);
            patch = $"@@ -0,0 +1,{count} @@\n" + string.Concat(lines.Take(count).Select(line => "+" + line + "\n"));
        }
        return new(before, after, patch);
    }

    public async Task<List<Commit>> HistoryAsync(int count, string? path = null, CancellationToken cancellation = default)
    {
        const string format = "--format=%H%x00%P%x00%an%x00%aI%x00%s%x00%D";
        var arguments = new List<string> { "log", "--topo-order", "-z", "--encoding=UTF-8", format, $"--max-count={count}" };
        if (path == null) arguments.Add("--all");
        else arguments.Add("--follow");
        if (path != null) arguments.AddRange(["--", path]);
        var response = await RunAsync(arguments, cancellation, 8 * 1024 * 1024);
        if (response.ExitCode != 0)
        {
            var head = await RunAsync(["rev-parse", "--verify", "HEAD"], cancellation);
            if (head.ExitCode != 0) return [];
        }
        var fields = response.RequireSuccess().Split('\0');
        var commits = new List<Commit>();
        for (var i = 0; i + 5 < fields.Length; i += 6)
            commits.Add(new(fields[i], fields[i + 1].Split(' ', StringSplitOptions.RemoveEmptyEntries), fields[i + 2], fields[i + 3], fields[i + 4], fields[i + 5]));
        BuildGraph(commits);
        return commits;
    }

    public static void BuildGraph(IReadOnlyList<Commit> commits)
    {
        var lanes = new List<string>();
        foreach (var commit in commits)
        {
            if (!lanes.Contains(commit.Hash)) lanes.Add(commit.Hash);
            var before = lanes.ToArray();
            var node = lanes.IndexOf(commit.Hash);
            lanes.RemoveAt(node);
            foreach (var parent in commit.Parents.Reverse()) if (!lanes.Contains(parent)) lanes.Insert(Math.Min(node, lanes.Count), parent);
            var above = before.Select((hash, i) => new GraphEdge(i, i, hash)).ToList();
            var below = before.Select((hash, i) => (hash, i)).Where(x => x.hash != commit.Hash)
                .Select(x => new GraphEdge(x.i, lanes.IndexOf(x.hash), x.hash)).ToList();
            below.AddRange(commit.Parents.Select(p => new GraphEdge(node, lanes.IndexOf(p), p)));
            commit.Graph = new(node, commit.Hash, above, below, Math.Max(before.Length, lanes.Count));
        }
    }

    public Task<CommandResult> CommitDetailsAsync(string hash, CancellationToken cancellation = default) =>
        RunAsync(["show", "--no-ext-diff", "--no-textconv", "--no-color", "--format=fuller", "--stat", "--patch", hash, "--"], cancellation);

    public async Task<IReadOnlyList<CommitFile>> ChangedFilesAsync(Commit commit, CancellationToken cancellation = default)
    {
        // Match the screenshot's merge behavior: compare the commit tree to its first parent.
        var args = new List<string> { "diff-tree", "--no-commit-id", "--raw", "-r", "-z", "--no-abbrev", "-M" };
        if (commit.Parents.Length > 0) args.Add(commit.Parents[0]); else args.Add("--root");
        args.Add(commit.Hash); args.Add("--");
        var parts = (await RunAsync(args, cancellation, 8 * 1024 * 1024)).RequireSuccess().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var files = new List<CommitFile>();
        for (var i = 0; i < parts.Length;)
        {
            var metadata = parts[i++].Split(' ');
            if (metadata.Length != 5 || i >= parts.Length) throw new FormatException("Unexpected changed-file record.");
            var oldPath = parts[i++]; var path = oldPath;
            if (metadata[4][0] is 'R' or 'C') path = parts[i++];
            files.Add(new(commit.Hash, commit.Parents.FirstOrDefault(), path, oldPath, metadata[4], metadata[2], metadata[3]));
        }
        return files;
    }

    public async Task<RevisionDiff> RevisionDiffAsync(CommitFile file, CancellationToken cancellation = default)
    {
        async Task<FilePreview> Blob(string id)
        {
            if (id.All(c => c == '0')) return new("", true);
            var size = (await RunAsync(["cat-file", "-s", id], cancellation)).RequireSuccess();
            if (!long.TryParse(size.Trim(), out var length) || length > Files.PreviewLimit) return new("Revision exceeds the 2 MB preview limit.", false);
            var content = await RunAsync(["cat-file", "blob", id], cancellation);
            if (content.Truncated || content.ExitCode != 0) return new("Unable to preview revision: " + content.Display, false);
            if (content.Output.Contains('\0') || content.Output.Contains('\uFFFD')) return new("Binary or unsupported text encoding: revision preview is unavailable.", false);
            return new(content.Output, true);
        }
        var before = await Blob(file.OldBlob); var after = await Blob(file.NewBlob);
        var patch = "";
        if (before.IsText && after.IsText && file.OldBlob != file.NewBlob)
        {
            var args = new List<string> { "diff", "-M", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=0" };
            if (file.ParentHash != null) args.AddRange([file.ParentHash, file.CommitHash]);
            else args = ["show", "-M", "--format=", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=0", file.CommitHash];
            args.AddRange(["--", file.OldPath]); if (file.Path != file.OldPath) args.Add(file.Path);
            var result = await RunAsync(args, cancellation);
            if (result.Truncated)
                return new(new("Diff exceeds the 2 MB preview limit.", false), new("Diff exceeds the 2 MB preview limit.", false), "");
            patch = result.RequireSuccess();
        }
        return new(before, after, patch);
    }

    public Task<CommandResult> CompareAsync(string older, string newer, string? path, CancellationToken cancellation = default) =>
        RunAsync(new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color", older, newer, "--" }.Concat(path == null ? [] : new[] { path }), cancellation);
}
