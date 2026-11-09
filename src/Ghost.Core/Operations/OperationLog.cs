namespace Ghost.Core.Operations;

public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
}

public sealed class NullLog : ILog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
}

/// <summary>Journal local en fichier texte. Aucune donnée n'est envoyée nulle part.</summary>
public sealed class FileLog : ILog, IDisposable
{
    private readonly StreamWriter _w;
    private readonly object _lock = new();
    public string Path { get; }

    public FileLog(string directory, string operation)
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, $"{DateTime.Now:yyyy-MM-dd_HHmmss}_{operation}.log");
        _w = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
    }

    public static string DefaultDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GhostClonage", "logs");

    private void Write(string level, string m)
    {
        lock (_lock) _w.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {m}");
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERREUR", message);
    public void Dispose() => _w.Dispose();
}
