using System.Runtime.InteropServices;
using System.Text;

namespace DiskSpaceGuard.Services;

/// <summary>
/// §15.2 命令行处理：用 CommandLineToArgvW 解析既有命令，执行时按参数列表重新加引号。
/// 全程不做字符串拼接，也不把应用提供的整条命令交给 cmd.exe。
/// </summary>
public static class WindowsCommandLine
{
    public static IReadOnlyList<string> Split(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return Array.Empty<string>();

        var argv = CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero || count <= 0) return Array.Empty<string>();

        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
            {
                var pointer = Marshal.ReadIntPtr(argv, i * IntPtr.Size);
                result[i] = Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    /// <summary>按 Windows 命令行规则为一个参数加引号。</summary>
    public static string QuoteArgument(string argument)
    {
        if (argument.Length == 0) return "\"\"";

        var needsQuotes = argument.Any(c => char.IsWhiteSpace(c) || c == '"');
        if (!needsQuotes) return argument;

        var builder = new StringBuilder(argument.Length + 8);
        builder.Append('"');
        var backslashes = 0;

        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            if (backslashes > 0)
            {
                builder.Append('\\', backslashes);
                backslashes = 0;
            }
            builder.Append(c);
        }

        if (backslashes > 0) builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }

    public static string Join(IEnumerable<string> arguments)
        => string.Join(" ", arguments.Select(QuoteArgument));

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string lpCmdLine, out int pNumArgs);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
