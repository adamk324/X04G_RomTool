using System.Text;

namespace X04G.Core.Common;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Message)
{
    public override string ToString() =>
        $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level.ToString().ToUpperInvariant(),-7}] {Message}";
}

/// <summary>
/// Thread-safe operation log. Keeps every entry in memory, optionally mirrors to a file,
/// and raises <see cref="EntryAdded"/> so a UI can display progress.
/// </summary>
public sealed class OperationLog : IDisposable
{
    private readonly object _lock = new();
    private readonly List<LogEntry> _entries = new();
    private StreamWriter? _file;

    public event Action<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_lock) return _entries.ToArray(); }
    }

    public string? FilePath { get; private set; }

    /// <summary>Starts mirroring the log (including earlier entries) to a new file. Never overwrites.</summary>
    public void AttachFile(string path)
    {
        lock (_lock)
        {
            _file?.Dispose();
            var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            _file = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
            FilePath = path;
            foreach (var e in _entries) _file.WriteLine(e.ToString());
        }
    }

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        lock (_lock)
        {
            _entries.Add(entry);
            _file?.WriteLine(entry.ToString());
        }
        EntryAdded?.Invoke(entry);
    }

    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warning, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries) sb.AppendLine(e.ToString());
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_lock) { _file?.Dispose(); _file = null; }
    }
}

public sealed record ProgressInfo(string Stage, double Fraction, string? Detail = null);
