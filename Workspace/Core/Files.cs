using System.IO;
using System.Text;

namespace GitWorkspace.Core;

public sealed record FilePreview(string Text, bool IsText);
public static class Files
{
    public const int PreviewLimit = 2 * 1024 * 1024;
    public static async Task<FilePreview> ReadAsync(string path)
    {
        try
        {
            // Bound the read itself, including files that grow after the length check.
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (file.Length > PreviewLimit) return new("File exceeds the 2 MB preview limit.", false);
            var bytes = new byte[PreviewLimit + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await file.ReadAsync(bytes.AsMemory(length));
                if (read == 0) break;
                length += read;
            }
            if (length > PreviewLimit) return new("File exceeds the 2 MB preview limit.", false);
            Encoding encoding = new UTF8Encoding(false, true);
            var offset = 0;
            if (length >= 3 && bytes[0] == 239 && bytes[1] == 187 && bytes[2] == 191) offset = 3;
            else if (length >= 2 && bytes[0] == 255 && bytes[1] == 254) { encoding = Encoding.Unicode; offset = 2; }
            else if (length >= 2 && bytes[0] == 254 && bytes[1] == 255) { encoding = Encoding.BigEndianUnicode; offset = 2; }
            else if (Array.IndexOf(bytes, (byte)0, 0, length) >= 0) return new("Binary file: text preview is unavailable.", false);
            return new(encoding.GetString(bytes, offset, length - offset), true);
        }
        catch (DecoderFallbackException) { return new("This file is not UTF-8 or BOM-marked UTF-16 text.", false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new($"Unable to read file: {ex.Message}", false); }
    }

    public static async Task WriteAsync(string path, string text)
    {
        var normalized = text.Replace("\r\n", "\n");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(normalized);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await file.WriteAsync(bytes);
        await file.FlushAsync();
    }
}
