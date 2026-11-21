using System.Text;

namespace Swarm.Core.Diagnostics;

/// <summary>极简滚动文件日志：按天分文件、单文件 2MB 上限，写入失败时静默降级，不影响主流程。</summary>
public static class FileLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwarmCursors", "logs");

    /// <summary>追加一行日志，返回实际写入路径；失败返回空字符串。</summary>
    public static string Write(string area, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}";
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"{area}-{DateTime.Now:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var archived = path + ".1";
                    try { File.Delete(archived); }
                    catch (IOException) { }
                    File.Move(path, archived);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
                return path;
            }
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
