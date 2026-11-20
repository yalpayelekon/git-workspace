using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace GitWorkspace.Terminal;

public sealed class PseudoTerminal : IAsyncDisposable
{
    private IntPtr console, job;
    private FileStream? input, output;
    private Process? process;
    private readonly Channel<string> inputQueue = Channel.CreateBounded<string>(128);
    private readonly Channel<string> outputQueue = Channel.CreateBounded<string>(32);
    private readonly CancellationTokenSource stop = new();
    private Task reader = Task.CompletedTask, writer = Task.CompletedTask;
    public ChannelReader<string> Output => outputQueue.Reader;
    public int ProcessId => process?.Id ?? 0;

    public PseudoTerminal(string shell, string directory, int columns = 100, int rows = 28)
    {
        try
        {
            if (!Native.CreatePipe(out var inputRead, out var inputWrite, IntPtr.Zero, 0)) throw Error();
            using (inputRead)
            {
                input = new FileStream(inputWrite, FileAccess.Write, 4096, false);
                if (!Native.CreatePipe(out var outputRead, out var outputWrite, IntPtr.Zero, 0)) throw Error();
                using (outputWrite)
                {
                    output = new FileStream(outputRead, FileAccess.Read, 4096, false);
                    Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new(columns, rows), inputRead, outputWrite, 0, out console));
                }
            }
            // Blocking pipe I/O stays off the dispatcher. Both directions have their own queues.
            reader = Task.Run(ReadLoop);
            writer = Task.Run(WriteLoop);
            StartShell(shell, directory);
        }
        catch
        {
            stop.Cancel();
            if (job != IntPtr.Zero) Native.CloseHandle(job);
            if (console != IntPtr.Zero) Native.ClosePseudoConsole(console);
            input?.Dispose(); output?.Dispose();
            throw;
        }
    }

    private void StartShell(string shell, string directory)
    {
        IntPtr list = IntPtr.Zero;
        var info = new Native.ProcessInfo();
        try
        {
            var size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            list = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw Error();
            if (!Native.UpdateProcThreadAttribute(list, 0, (IntPtr)0x00020016, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) throw Error();
            var startup = new Native.StartupInfoEx { Attributes = list };
            startup.Startup.Size = Marshal.SizeOf<Native.StartupInfoEx>();
            // Do not inherit redirected handles from a launcher/test console. Null handles with
            // STARTF_USESTDHANDLES let ConPTY supply its own console streams (terminal#15814).
            startup.Startup.Flags = 0x00000100;
            // Suspended creation lets the job own all descendants before PowerShell can spawn any.
            if (!Native.CreateProcess(shell, new StringBuilder($"\"{shell}\" -NoLogo"), IntPtr.Zero, IntPtr.Zero, false,
                0x00080000 | 0x00000004, IntPtr.Zero, directory, ref startup, out info)) throw Error();
            job = Native.CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw Error();
            var limits = new Native.ExtendedLimits();
            limits.Basic.Flags = 0x00002000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.ExtendedLimits>()) ||
                !Native.AssignProcessToJobObject(job, info.Process)) throw Error();
            process = Process.GetProcessById(info.ProcessId);
            if (Native.ResumeThread(info.Thread) == uint.MaxValue) throw Error();
        }
        catch { if (info.Process != IntPtr.Zero) Native.TerminateProcess(info.Process, 1); throw; }
        finally
        {
            if (info.Thread != IntPtr.Zero) Native.CloseHandle(info.Thread);
            if (info.Process != IntPtr.Zero) Native.CloseHandle(info.Process);
            if (list != IntPtr.Zero) { Native.DeleteProcThreadAttributeList(list); Marshal.FreeHGlobal(list); }
        }
    }

    private async Task ReadLoop()
    {
        try
        {
            using var text = new StreamReader(output!, new UTF8Encoding(false), false, 8192, leaveOpen: true);
            var buffer = new char[8192];
            int read;
            while ((read = await text.ReadAsync(buffer)) > 0)
            {
                // Continue draining shutdown output so ClosePseudoConsole cannot block on a full pipe.
                if (stop.IsCancellationRequested) continue;
                try { await outputQueue.Writer.WriteAsync(new string(buffer, 0, read), stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        finally { outputQueue.Writer.TryComplete(); }
    }
    private async Task WriteLoop()
    {
        try
        {
            await foreach (var data in inputQueue.Reader.ReadAllAsync(stop.Token))
            {
                var bytes = Encoding.UTF8.GetBytes(data);
                await input!.WriteAsync(bytes, stop.Token);
                await input.FlushAsync(stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    public ValueTask WriteAsync(string data) => inputQueue.Writer.WriteAsync(data, stop.Token);
    public void Resize(int columns, int rows)
    {
        if (console != IntPtr.Zero) Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new(Math.Clamp(columns, 2, 500), Math.Clamp(rows, 1, 300))));
    }
    private static Win32Exception Error() => new(Marshal.GetLastWin32Error());

    public async ValueTask DisposeAsync()
    {
        if (stop.IsCancellationRequested) return;
        stop.Cancel(); inputQueue.Writer.TryComplete();
        // Free the queues before closing ConPTY, which may flush a final screen synchronously.
        if (job != IntPtr.Zero) { Native.CloseHandle(job); job = IntPtr.Zero; }
        await Task.Run(() => { if (console != IntPtr.Zero) { Native.ClosePseudoConsole(console); console = IntPtr.Zero; } });
        input?.Dispose(); output?.Dispose();
        await Task.WhenAny(Task.WhenAll(reader, writer), Task.Delay(2000));
        process?.Dispose(); stop.Dispose();
    }
}
