using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace CreatorPantry.ServiceDefaults;

/// <summary>
/// The resilience pipeline model-provider calls run inside.
/// </summary>
/// <remarks>
/// <para>
/// Here rather than in the domain because this project owns cross-cutting resilience — the standard HTTP
/// handler is configured a few lines away, and a second place deciding timeouts and breaker windows would be a
/// second place to get them wrong.
/// </para>
/// <para>
/// <strong>The transience test is supplied by the caller, and that is the crux.</strong> Whether a failure is
/// worth another attempt is a domain judgement: a schema failure, a safety block and a malformed request must
/// never be retried, and no predicate written here could tell those apart from a 503 without naming domain
/// types. <c>CreatorPantry.Domain</c> may not reference a host assembly and this project may not reference the
/// domain, so the host — which references both — hands the predicate in. That is the composition point, the
/// same way <c>Program.cs</c> is for everything else.
/// </para>
/// </remarks>
public static class AiResilience
{
    /// <summary>The name the model-provider pipeline is registered under.</summary>
    /// <remarks>
    /// The one value this project and the domain gateway share. The host passes it to the module's
    /// registration rather than either side hard-coding the other's string.
    /// </remarks>
    public const string PipelineKey = "creatorpantry-ai";

    /// <summary>How long one provider call may take before it is abandoned.</summary>
    /// <remarks>
    /// Generous, because a long generation legitimately takes tens of seconds and cutting one off wastes the
    /// tokens already spent. A backstop against a hung connection, not a latency target.
    /// </remarks>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(90);

    /// <param name="isTransient">
    /// Whether a failure should be retried and counted against the circuit breaker. Anything this returns
    /// false for passes straight out, unretried, which is what keeps a safety block from becoming three.
    /// </param>
    public static TBuilder AddAiResilience<TBuilder>(this TBuilder builder, Func<Exception, bool> isTransient)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(isTransient);

        builder.Services.AddResiliencePipeline(PipelineKey, pipeline => pipeline
            // Retry outside the timeout, so each attempt gets the full allowance rather than all of them
            // sharing one budget.
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),

                // Jitter, because every worker that hit one rate limit would otherwise come back at the same
                // moment and reproduce it.
                UseJitter = true,
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } exception && isTransient(exception)),
            })
            .AddTimeout(new TimeoutStrategyOptions { Timeout = AttemptTimeout })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // A provider failing for everyone should be shed quickly rather than retried once per queued
                // operation.
                FailureRatio = 0.5,
                MinimumThroughput = 8,
                SamplingDuration = TimeSpan.FromSeconds(60),
                BreakDuration = TimeSpan.FromSeconds(30),
                ShouldHandle = args => ValueTask.FromResult(
                    args.Outcome.Exception is { } exception && isTransient(exception)),
            }));

        return builder;
    }
}
