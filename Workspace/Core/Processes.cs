using System.Diagnostics;
using System.IO;
using System.Text;

namespace GitWorkspace.Core;

public sealed record CommandResult(int ExitCode, string Output, string Error, bool Truncated = false)
{
    public string Display => string.Join(Environment.NewLine, new[] { Output.TrimEnd(), Error.TrimEnd() }.Where(s => s.Length > 0))
        + (Truncated ? "\n[Output exceeds the preview limit.]" : "");
    public string RequireSuccess()
    {
        if (ExitCode != 0) throw new InvalidOperationException(Display);
        if (Truncated) throw new InvalidOperationException("Command output exceeded the size limit. Narrow the selection.");
        return Output;
    }
}

public static class Processes
{
    public static string? Find(string name)
    {
        if (Path.IsPathRooted(name)) return File.Exists(name) ? name : null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            foreach (var extension in new[] { ".exe", ".bat", ".cmd", "" })
            {
                var path = Path.Combine(directory.Trim('"'), name + extension);
                if (File.Exists(path)) return path;
            }
        return null;
    }

    public static async Task<CommandResult> RunAsync(string executable, IEnumerable<string> arguments, string directory,
        CancellationToken cancellation = default, int maxCharacters = 2 * 1024 * 1024)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var args = arguments.ToArray();
        if (Path.GetExtension(executable).ToLowerInvariant() is ".bat" or ".cmd")
        {
            // cmd.exe has different quoting rules from CommandLineToArgvW. Expand fixed
            // script arguments through environment variables once, inside double quotes.
            start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
            var values = new[] { executable }.Concat(args).ToArray();
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].Contains('"') || values[i].Contains('\r') || values[i].Contains('\n'))
                    throw new ArgumentException("Batch tool arguments cannot contain quotes or newlines.");
                start.Environment[$"{AppInfo.BatchEnvPrefix}{i}"] = values[i];
            }
            start.Arguments = "/d /v:off /s /c \"" + string.Join(" ", values.Select((_, i) => $"\"%{AppInfo.BatchEnvPrefix}{i}%\"")) + "\"";
        }
        else foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException($"Unable to start {Path.GetFileName(executable)}.");
        process.StandardInput.Close();
        using var registration = cancellation.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        });
        var output = DrainAsync(process.StandardOutput, maxCharacters);
        var error = DrainAsync(process.StandardError, maxCharacters);
        try { await process.WaitForExitAsync(cancellation); }
        catch (OperationCanceledException)
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            throw;
        }
        var stdout = await output;
        var stderr = await error;
        cancellation.ThrowIfCancellationRequested();
        return new(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int maximum)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        {
            var keep = Math.Min(count, maximum - text.Length);
            if (keep > 0) text.Append(buffer, 0, keep);
            truncated |= keep < count;
        }
        return (text.ToString(), truncated);
    }
}
