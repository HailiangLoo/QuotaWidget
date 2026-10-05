using System.Text;

namespace QuotaWidget.Core;

public static class AtomicFile
{
    static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// Writes to a temp file in the same directory, flushes it to disk and swaps it in,
    /// so a reader sees either the old file or the new one, never a torn write.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Utf8NoBom.GetBytes(content);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            // A reader holding the file without FILE_SHARE_DELETE blocks the swap briefly; retry.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && attempt < 40)
                {
                    Thread.Sleep(25);
                }
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    /// <summary>Reads without blocking a concurrent atomic replace.</summary>
    public static string? TryReadAllText(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Utf8NoBom, detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            catch (IOException) { Thread.Sleep(20); }
        }
        return null;
    }
}
