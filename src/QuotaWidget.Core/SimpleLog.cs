using System.Globalization;
using System.Text;

namespace QuotaWidget.Core;

/// <summary>
/// Plain operational log. Callers pass fixed phrases, status codes and error kinds only:
/// never tokens, cookies, response bodies or account e-mail.
/// </summary>
public sealed class SimpleLog
{
    readonly string _path;
    readonly object _gate = new();
    const long MaxBytes = 512 * 1024;

    public SimpleLog(string path) => _path = path;

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes) File.Move(_path, _path + ".1", overwrite: true);
                var line = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine;
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
            catch { /* logging must never break collection */ }
        }
    }
}
