using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace GitWorkspace.Core;

internal static class GitLockRecovery
{
    internal static readonly string[] GitPaths =
        ["index.lock", "HEAD.lock", "packed-refs.lock", "shallow.lock", "config.lock", "FETCH_HEAD.lock", "ORIG_HEAD.lock", "refs"];
    private static readonly TimeSpan MinimumAge = TimeSpan.FromSeconds(30);
    private const uint GenericRead = 0x80000000, DeleteAccess = 0x00010000, OpenExisting = 3, OpenReparsePoint = 0x00200000;
    private const int FileDispositionInfo = 4;

    internal static string[] ExpandPaths(string output)
    {
        var paths = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(path => path.TrimEnd('\r')).ToArray();
        if (paths.Length != GitPaths.Length || paths.Any(path => !Path.IsPathFullyQualified(path)))
            throw new IOException("Git pre-check could not resolve repository lock paths.");
        return paths;
    }

    internal static async Task<string?> PrepareAsync(string[] paths, Action<string> log, CancellationToken cancellation)
    {
        string? blocked = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellation.ThrowIfCancellationRequested();
            blocked = await Task.Run(() => CheckLocks(paths, log, cancellation), cancellation);
            if (blocked == null) return null;
            if (attempt == 0) log("Git pre-check is waiting for a repository lock to become available.");
            if (attempt < 4) await Task.Delay(500, cancellation);
        }
        return $"Git pre-check: {blocked} Finish the active Git operation, then retry. Recent locks are eligible for recovery after 30 seconds.";
    }

    private static string? CheckLocks(string[] paths, Action<string> log, CancellationToken cancellation)
    {
        try
        {
            var locks = paths[..^1].Where(File.Exists).ToList();
            var refs = paths[^1];
            if (Directory.Exists(refs))
                locks.AddRange(Directory.EnumerateFiles(refs, "*.lock", new EnumerationOptions
                {
                    RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
                }));
            foreach (var path in locks.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellation.ThrowIfCancellationRequested();
                var blocked = Recover(path, log, cancellation);
                if (blocked != null) return blocked;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or Win32Exception)
        {
            return "Lock recovery could not verify safe access. " + ex.Message;
        }
    }

    private static string? Recover(string path, Action<string> log, CancellationToken cancellation)
    {
        // DELETE access and exclusive sharing keep the same file protected through backup and deletion.
        // A Git commit may close its lock handle while an editor is open, so an OS handle check alone is insufficient.
        using var handle = CreateFile(path, GenericRead | DeleteAccess, 0, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3) return null;
            return $"Cannot exclusively access '{path}': {new Win32Exception(error).Message}";
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return $"Lock '{path}' is a link and requires manual inspection.";
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < MinimumAge)
            return $"Lock '{path}' was modified recently.";
        var active = ActiveGitProcess();
        if (active != null) return active;
        cancellation.ThrowIfCancellationRequested();

        var backupDirectory = AppPaths.GitLockRecoveryDirectory;
        Directory.CreateDirectory(backupDirectory);
        var backup = Path.Combine(backupDirectory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.bak");
        using var source = new FileStream(handle, FileAccess.Read);
        using (var destination = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            source.CopyTo(destination);
            destination.Flush(flushToDisk: true);
        }
        cancellation.ThrowIfCancellationRequested();
        byte delete = 1;
        if (!SetFileInformationByHandle(handle, FileDispositionInfo, ref delete, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        log($"Recovered abandoned Git lock '{path}'. Backup: {backup}");
        return null;
    }

    private static string? ActiveGitProcess()
    {
        object? locator = null, services = null, results = null;
        try
        {
            // WMI also finds Git waiting on an editor/hook with a closed lock handle.
            // Fail closed for unreadable processes. Unrelated Git commands are conservatively treated as active.
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")
                ?? throw new COMException("Windows process inspection is unavailable."));
            services = ((dynamic)locator!).ConnectServer(".", "root\\cimv2");
            results = ((dynamic)services).ExecQuery("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'git.exe' OR Name LIKE 'git-%.exe'");
            foreach (var item in (IEnumerable)results)
            {
                try
                {
                    var processId = Convert.ToInt32(ReadProperty(item, "ProcessId"));
                    var command = ReadProperty(item, "CommandLine") as string;
                    // The persistent filesystem monitor does not own index/ref transaction locks.
                    if (command != null && Regex.IsMatch(command, "^(?:\"[^\"]+\"|\\S+)\\s+fsmonitor--daemon(?:\\s|$)", RegexOptions.IgnoreCase)) continue;
                    try
                    {
                        using var process = Process.GetProcessById(processId);
                        if (process.HasExited) continue;
                        return $"Git process {processId} is still running; its repository lock ownership cannot be ruled out.";
                    }
                    catch (ArgumentException) { } // Process exited after the WMI snapshot.
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            return null;
        }
        finally
        {
            if (results != null) Marshal.ReleaseComObject(results);
            if (services != null) Marshal.ReleaseComObject(services);
            if (locator != null) Marshal.ReleaseComObject(locator);
        }
    }

    private static object? ReadProperty(object item, string name)
    {
        object properties = ((dynamic)item).Properties_;
        object? property = null;
        try
        {
            property = ((dynamic)properties).Item(name);
            return ((dynamic)property).Value;
        }
        finally
        {
            if (property != null) Marshal.ReleaseComObject(property);
            Marshal.ReleaseComObject(properties);
        }
    }

    [DllImport("kernel32", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref byte information, uint size);
}
