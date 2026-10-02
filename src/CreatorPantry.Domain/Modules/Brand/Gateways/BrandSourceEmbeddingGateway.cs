using System.Diagnostics;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Brand.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Brand.Gateways;

/// <summary>What the embedding deployment is, as far as provenance needs to know.</summary>
public enum BrandSourceEmbeddingModelStatus
{
    /// <summary>The deployment named itself.</summary>
    Identified = 1,

    /// <summary>This host has no embedding deployment: its generator is the one that refuses every call.</summary>
    NotConfigured = 2,

    /// <summary>A deployment is there but does not say which model it is, so a set could not record it.</summary>
    Unidentified = 3,
}

public sealed record BrandSourceEmbeddingModel(BrandSourceEmbeddingModelStatus Status, string? ModelId = null);

/// <summary>How one embedding request ended.</summary>
public enum BrandSourceEmbeddingBatchOutcome
{
    Embedded = 1,

    /// <summary>No deployment is configured.</summary>
    NotConfigured = 2,

    /// <summary>The provider failed in a way that may pass: unreachable, throttled, timed out.</summary>
    Unavailable = 3,

    /// <summary>The provider answered with the wrong number of vectors or the wrong width. Never stored.</summary>
    Invalid = 4,
}

public sealed record BrandSourceEmbeddingBatch(
    BrandSourceEmbeddingBatchOutcome Outcome, IReadOnlyList<float[]>? Vectors = null);

/// <summary>
/// The boundary to the embedding deployment, in terms of the application's own types.
/// </summary>
/// <remarks>
/// A gateway by backend.md's definition: it owns the provider call and nothing else. It depends on
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> and never on a provider SDK — the one assembly
/// permitted to name one is <c>CreatorPantry.AiProvider</c>.
/// </remarks>
public interface IBrandSourceEmbeddingGateway
{
    /// <summary>Which deployment would embed a request made now.</summary>
    BrandSourceEmbeddingModel Describe();

    /// <summary>
    /// Embeds <paramref name="texts"/> in one provider call, and checks the answer before anything can store
    /// it: one vector per text, each exactly <see cref="BrandPolicy.EmbeddingDimension"/> wide.
    /// </summary>
    Task<BrandSourceEmbeddingBatch> EmbedAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IBrandSourceEmbeddingGateway"/>
/// <remarks>
/// <para>
/// <strong>No retry here.</strong> Transport retries live in the ServiceDefaults pipeline beneath the
/// generator; the operation-level requeue above this adds the only other layer, and a third would multiply
/// attempts against the creator's budget.
/// </para>
/// <para>
/// <strong>Nothing about the request is logged but its shape.</strong> The passages are creator content and a
/// provider's error message can quote them, so a failure is recorded by exception type alone.
/// </para>
/// </remarks>
public sealed class BrandSourceEmbeddingGateway(
    IEmbeddingGenerator<string, Embedding<float>> generator,
    ILogger<BrandSourceEmbeddingGateway> logger) : IBrandSourceEmbeddingGateway
{
    public BrandSourceEmbeddingModel Describe()
    {
        if (generator is UnconfiguredEmbeddingGenerator)
        {
            return new BrandSourceEmbeddingModel(BrandSourceEmbeddingModelStatus.NotConfigured);
        }

        var modelId = generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;

        return string.IsNullOrWhiteSpace(modelId) || modelId.Length > BrandPolicy.EmbeddingModelMaxLength
            ? new BrandSourceEmbeddingModel(BrandSourceEmbeddingModelStatus.Unidentified)
            : new BrandSourceEmbeddingModel(BrandSourceEmbeddingModelStatus.Identified, modelId);
    }

    public async Task<BrandSourceEmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(texts);

        var clock = Stopwatch.StartNew();
        GeneratedEmbeddings<Embedding<float>> generated;

        try
        {
            generated = await generator.GenerateAsync(texts, options: null, cancellationToken);
        }
        catch (AiProviderNotConfiguredException)
        {
            return new BrandSourceEmbeddingBatch(BrandSourceEmbeddingBatchOutcome.NotConfigured);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Any failure while the caller still wants the answer, a client-side timeout (which surfaces as a
            // cancellation) included: it is the provider being slow, not the worker being stopped.
            logger.LogWarning(
                "Embedding request of {Count} passages failed after {ElapsedMs} ms: {ExceptionType}.",
                texts.Count,
                clock.ElapsedMilliseconds,
                exception.GetType().Name);

            return new BrandSourceEmbeddingBatch(BrandSourceEmbeddingBatchOutcome.Unavailable);
        }

        if (generated.Count != texts.Count
            || generated.Any(embedding => embedding.Vector.Length != BrandPolicy.EmbeddingDimension))
        {
            logger.LogError(
                "Embedding request of {Count} passages returned {Returned} vectors, or vectors not {Dimension} wide.",
                texts.Count,
                generated.Count,
                BrandPolicy.EmbeddingDimension);

            return new BrandSourceEmbeddingBatch(BrandSourceEmbeddingBatchOutcome.Invalid);
        }

        logger.LogInformation(
            "Embedded {Count} passages in {ElapsedMs} ms using {Tokens} tokens.",
            texts.Count,
            clock.ElapsedMilliseconds,
            generated.Usage?.TotalTokenCount);

        return new BrandSourceEmbeddingBatch(
            BrandSourceEmbeddingBatchOutcome.Embedded,
            generated.Select(embedding => embedding.Vector.ToArray()).ToList());
    }
}
