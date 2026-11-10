namespace DocCollect.Core.Diagnostics;

public enum LogSeverity { Trace = 0, Debug = 1, Information = 2, Warning = 3, Error = 4, Critical = 5 }

public sealed record LogRecord(long Id, DateTimeOffset At, LogSeverity Level, string Category, string Message, string? Exception);

/// <summary>
/// Derniers journaux de l'application, en mémoire (tampon circulaire), consultables par l'administrateur.
/// Les journaux complets restent ceux du conteneur (docker compose logs).
/// </summary>
public sealed class InMemoryLogStore(int capacity = 5000)
{
    private readonly LogRecord?[] _buffer = new LogRecord?[Math.Max(10, capacity)];
    private readonly object _lock = new();
    private long _nextId = 1;
    private int _head; // prochaine position d'écriture
    private int _count;

    public int Capacity => _buffer.Length;

    public void Add(DateTimeOffset at, LogSeverity level, string category, string message, string? exception = null)
    {
        lock (_lock)
        {
            _buffer[_head] = new LogRecord(_nextId++, at, level, category, Truncate(message, 4000)!, Truncate(exception, 8000));
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    /// <summary>Plus récents d'abord. <paramref name="afterId"/> : seulement les entrées plus récentes que cet identifiant.</summary>
    public IReadOnlyList<LogRecord> Query(LogSeverity minLevel = LogSeverity.Information, string? search = null, long? afterId = null, int take = 200)
    {
        take = Math.Clamp(take, 1, 1000);
        var result = new List<LogRecord>(take);
        lock (_lock)
        {
            for (var i = 0; i < _count && result.Count < take; i++)
            {
                var index = (_head - 1 - i + _buffer.Length) % _buffer.Length;
                var r = _buffer[index]!;
                if (afterId is { } a && r.Id <= a) break;
                if (r.Level < minLevel) continue;
                if (!string.IsNullOrWhiteSpace(search)
                    && !r.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !r.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                    && !(r.Exception?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                    continue;
                result.Add(r);
            }
        }
        return result;
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max] + "…";
}
