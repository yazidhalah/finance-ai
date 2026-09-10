using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FinanceAi.Api.Logging;

/// <summary>
/// Structured JSON logs with a <c>requestId</c> for correlation (SEC-101), written through the
/// redaction layer (SEC-41). Redaction sits at the sink deliberately: a scrubber that has to be
/// invoked by each caller is a scrubber that will eventually be forgotten.
/// </summary>
[ProviderAlias("RedactingJson")]
public sealed class RedactingJsonLoggerProvider(TextWriter output) : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, RedactingJsonLogger> loggers = new(StringComparer.Ordinal);
    private IExternalScopeProvider? scopeProvider;

    public ILogger CreateLogger(string categoryName) =>
        this.loggers.GetOrAdd(categoryName, name => new RedactingJsonLogger(name, output, () => this.scopeProvider));

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => this.scopeProvider = scopeProvider;

    public void Dispose() => this.loggers.Clear();
}

internal sealed class RedactingJsonLogger(
    string category, TextWriter output, Func<IExternalScopeProvider?> scopeProvider) : ILogger
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => scopeProvider()?.Push(state);

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(formatter);

        var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["level"] = logLevel.ToString(),
            ["category"] = category,
            ["eventId"] = eventId.Id,
            ["message"] = SensitiveData.Redact(formatter(state, exception)),
        };

        if (state is IEnumerable<KeyValuePair<string, object?>> properties)
        {
            foreach (var (key, value) in properties)
            {
                if (key == "{OriginalFormat}")
                {
                    continue;
                }

                entry["p." + key] = SensitiveData.IsSensitiveKey(key)
                    ? SensitiveData.Placeholder
                    : SensitiveData.Redact(value?.ToString());
            }
        }

        if (exception is not null)
        {
            // The type and message, never the stack trace: stack traces routinely carry argument
            // values, and an argument value here can be a password or a customer's balance.
            entry["exception"] = exception.GetType().FullName;
            entry["exceptionMessage"] = SensitiveData.Redact(exception.Message);
        }

        lock (output)
        {
            output.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
            output.Flush();
        }
    }
}
