using System.Diagnostics;
using System.IO;
using System.Text;

namespace DiskSpaceGuard.Services;

/// <summary>
/// §9 运行日志：只记录异常摘要与错误，默认脱敏；与清理台账分离。
/// 单文件超过 1 MB 时轮转为 run.log.1，避免无限增长。
/// </summary>
public static class RunLog
{
    private const long MaxBytes = 1_000_000;
    private static readonly object Gate = new();

    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskSpaceGuard");

    public static string FilePath => Path.Combine(Dir, "run.log");
    public static string LastErrorPath => Path.Combine(Dir, "last-error.txt");

    public static void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {PrivacyRedactor.RedactDefault(message.Trim())}";
        Append(line);
    }

    public static void WriteException(string context, Exception exception)
    {
        var detail = new StringBuilder()
            .Append(context)
            .Append(": ")
            .Append(exception.GetType().Name)
            .Append(": ")
            .AppendLine(exception.Message)
            .Append(exception.StackTrace ?? string.Empty)
            .ToString();

        Write(detail);

        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(LastErrorPath, PrivacyRedactor.RedactDefault(detail), Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot write last-error.txt: {ex.Message}");
        }
    }

    private static void Append(string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Gate)
            {
                try
                {
                    if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                        File.Move(FilePath, FilePath + ".1", overwrite: true);
                }
                catch (IOException) { }

                File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Cannot write run log: {ex.Message}");
        }
    }
}
