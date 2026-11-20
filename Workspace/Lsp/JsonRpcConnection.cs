using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GitWorkspace.Lsp;

/// <summary>LSP Content-Length framed JSON-RPC over a duplex stream pair.</summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private static readonly byte[] HeaderSeparator = "\r\n\r\n"u8.ToArray();
    private readonly Stream input;
    private readonly Stream output;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly byte[] readBuffer = new byte[8192];
    private byte[] pending = new byte[8192];
    private int pendingLength;
    private Task? reader;
    private bool disposed;

    public event Action<JsonObject>? Message;
    public event Action<Exception>? Faulted;

    public JsonRpcConnection(Stream input, Stream output)
    {
        this.input = input;
        this.output = output;
    }

    public void Start()
    {
        if (reader != null) return;
        reader = ReadLoopAsync();
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellation = default)
    {
        var payload = Encoding.UTF8.GetBytes(message.ToJsonString(SerializerOptions));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await output.WriteAsync(header, cancellation).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellation).ConfigureAwait(false);
            await output.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally { writeLock.Release(); }
    }

    public static JsonObject? TryParseMessage(ReadOnlySpan<byte> body)
    {
        try { return JsonNode.Parse(body)?.AsObject(); }
        catch (JsonException) { return null; }
    }

    /// <summary>Parses one framed message from a growing buffer. Returns whether a frame boundary was reached.</summary>
    public static bool TryConsume(byte[] data, int length, out JsonObject? message, out int consumed)
    {
        message = null;
        consumed = 0;
        var span = data.AsSpan(0, length);
        var headerEnd = span.IndexOf(HeaderSeparator);
        if (headerEnd < 0) return false;
        var headers = Encoding.ASCII.GetString(span[..headerEnd]);
        var contentLength = 0;
        var found = false;
        foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line["Content-Length:".Length..].Trim(), out contentLength) || contentLength < 0)
            {
                consumed = headerEnd + HeaderSeparator.Length;
                return true;
            }
            found = true;
        }
        if (!found) { consumed = headerEnd + HeaderSeparator.Length; return true; }
        var bodyStart = headerEnd + HeaderSeparator.Length;
        if (span.Length < bodyStart + contentLength) return false;
        message = TryParseMessage(span.Slice(bodyStart, contentLength));
        consumed = bodyStart + contentLength;
        return true;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var read = await input.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), stop.Token).ConfigureAwait(false);
                if (read == 0) break;
                EnsurePending(pendingLength + read);
                Buffer.BlockCopy(readBuffer, 0, pending, pendingLength, read);
                pendingLength += read;
                while (TryConsume(pending, pendingLength, out var message, out var consumed))
                {
                    TrimPending(consumed);
                    if (message != null) Message?.Invoke(message);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { Faulted?.Invoke(ex); }
        catch (Exception ex) { Faulted?.Invoke(ex); }
    }

    private void EnsurePending(int size)
    {
        if (size <= pending.Length) return;
        var next = pending.Length;
        while (next < size) next *= 2;
        Array.Resize(ref pending, next);
    }

    private void TrimPending(int consumed)
    {
        if (consumed <= 0) return;
        var remaining = pendingLength - consumed;
        if (remaining <= 0) { pendingLength = 0; return; }
        Buffer.BlockCopy(pending, consumed, pending, 0, remaining);
        pendingLength = remaining;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        stop.Cancel();
        if (reader != null)
        {
            try { await reader.ConfigureAwait(false); }
            catch (Exception) { }
        }
        stop.Dispose();
        writeLock.Dispose();
    }

    internal static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
