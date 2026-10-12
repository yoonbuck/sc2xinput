namespace Sc2XInput;

internal static class Log
{
    private static readonly object Lock = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    /// <summary>Also write log lines to a file (truncated). Failure to open it is ignored.</summary>
    public static void ToFile(string path)
    {
        try
        {
            _file = new StreamWriter(path, append: false) { AutoFlush = true };
            FilePath = path;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void Info(string msg) => Write(msg, null);
    public static void Warn(string msg) => Write("warning: " + msg, ConsoleColor.Yellow);
    public static void Error(string msg) => Write("error: " + msg, ConsoleColor.Red);

    private static void Write(string msg, ConsoleColor? color)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {msg}";
        lock (Lock)
        {
            if (color is { } c) Console.ForegroundColor = c;
            Console.WriteLine(line);
            if (color != null) Console.ResetColor();
            try { _file?.WriteLine(line); }
            catch (IOException) { } // disk full, file removed: logging must never take the app down
        }
    }
}
