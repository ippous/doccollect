using DocCollect.Core.Diagnostics;

namespace DocCollect.Api.Infrastructure;

/// <summary>Fournisseur de journalisation qui alimente <see cref="InMemoryLogStore"/> (page « Journaux » de l'admin).</summary>
[ProviderAlias("Memory")]
public sealed class InMemoryLoggerProvider(InMemoryLogStore store, TimeProvider clock) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger(store, clock, categoryName);
    public void Dispose() { }

    private sealed class MemoryLogger(InMemoryLogStore store, TimeProvider clock, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // Le filtrage par catégorie (section Logging de la configuration) est appliqué en amont par le framework.
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            store.Add(clock.GetUtcNow(), (LogSeverity)(int)logLevel, category, formatter(state, exception), exception?.ToString());
        }
    }
}

public static class MemoryLoggingExtensions
{
    public static WebApplicationBuilder AddMemoryLogs(this WebApplicationBuilder builder)
    {
        var store = new InMemoryLogStore(builder.Configuration.GetValue("Logging:MemoryCapacity", 5000));
        builder.Services.AddSingleton(store);
        builder.Logging.AddProvider(new InMemoryLoggerProvider(store, TimeProvider.System));
        return builder;
    }
}
