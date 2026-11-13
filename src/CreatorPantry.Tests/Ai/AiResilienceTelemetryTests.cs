using System.Collections.Concurrent;
using CreatorPantry.ServiceDefaults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly.Registry;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The model pipeline's telemetry says that an attempt ran and how it ended, never what the model said.
/// </summary>
/// <remarks>
/// Live worker logs carried every generated answer in full: Polly logs each execution attempt with its result, and
/// for anything but an HTTP response it formats the result as-is. ai.md: generated creator content is not logged by
/// default. Exercised through the real registration on a real host builder, so the test also proves the options
/// reach a pipeline registered through dependency injection.
/// </remarks>
public sealed class AiResilienceTelemetryTests
{
    private const string GeneratedContent = "{\"title\":\"Grandma's secret soda bread\"}";

    [Fact]
    public async Task A_model_answer_never_reaches_the_pipeline_log()
    {
        var sink = new CapturingLoggerProvider();
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).AddProvider(sink);
        builder.AddAiResilience(_ => false);

        using var host = builder.Build();
        var pipeline = host.Services
            .GetRequiredService<ResiliencePipelineProvider<string>>()
            .GetPipeline(AiResilience.PipelineKey);

        var result = await pipeline.ExecuteAsync(
            _ => ValueTask.FromResult(GeneratedContent), TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedContent, result);
        Assert.NotEmpty(sink.Messages);
        Assert.DoesNotContain(sink.Messages, message => message.Contains("secret soda bread", StringComparison.Ordinal));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(";", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                messages.Enqueue(formatter(state, exception) + " | " + structured);
            }
        }
    }
}
