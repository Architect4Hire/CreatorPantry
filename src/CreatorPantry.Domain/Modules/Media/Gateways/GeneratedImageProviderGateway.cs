using System.Diagnostics;
using System.Net;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Domain.Modules.Media.Gateways;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed here rather than project-wide.
#pragma warning disable MEAI001

/// <summary>What the image deployment is, as far as provenance needs to know.</summary>
public enum GeneratedImageModelStatus
{
    /// <summary>The deployment named itself.</summary>
    Identified = 1,

    /// <summary>This host has no image deployment: its generator is the one that refuses every call.</summary>
    NotConfigured = 2,

    /// <summary>A deployment is there but does not say which model it is, so provenance could not be written.</summary>
    Unidentified = 3,
}

/// <param name="ProviderName">Who served it. Recorded on every staged image.</param>
/// <param name="ModelName">Which model produced it.</param>
/// <param name="ModelDeployment">The deployment, when the provider distinguishes one from the model.</param>
public sealed record GeneratedImageModel(
    GeneratedImageModelStatus Status,
    string? ProviderName = null,
    string? ModelName = null,
    string? ModelDeployment = null);

/// <summary>How one image request ended.</summary>
public enum GeneratedImageProviderOutcome
{
    /// <summary>Bytes came back. They have not been looked at yet.</summary>
    Generated = 1,

    /// <summary>No deployment is configured. Terminal.</summary>
    NotConfigured = 2,

    /// <summary>Unreachable, timed out, or failed in a way that may pass. Worth another attempt.</summary>
    Unavailable = 3,

    /// <summary>Throttled. Worth another attempt, after a backoff.</summary>
    RateLimited = 4,

    /// <summary>
    /// The provider refused the request and would refuse it again. Terminal.
    /// </summary>
    /// <remarks>
    /// A safety block, a malformed request and a rejected credential are one outcome here, because telling
    /// them apart means reading a provider SDK exception type this module may not name. See
    /// <see cref="GeneratedImageFailureCategory.ProviderRefused"/>.
    /// </remarks>
    Refused = 5,

    /// <summary>
    /// The provider answered with nothing this can stage: no image content, or a link instead of bytes.
    /// </summary>
    InvalidResponse = 6,
}

/// <param name="Bytes">Present exactly when the outcome is <see cref="GeneratedImageProviderOutcome.Generated"/>.</param>
public sealed record GeneratedImageResult(
    GeneratedImageProviderOutcome Outcome, ReadOnlyMemory<byte> Bytes = default);

/// <summary>
/// The boundary to the image deployment, in terms of the application's own types.
/// </summary>
/// <remarks>
/// <para>
/// A gateway by backend.md's definition: it owns the provider call and nothing else. It depends on
/// <see cref="IImageGenerator"/> and never on a provider SDK — the one assembly permitted to name one is
/// <c>CreatorPantry.AiProvider</c> — so swapping the deployment behind it changes nothing above it.
/// </para>
/// <para>
/// <strong>One image per call.</strong> Not an oversight: <see cref="ImageGenerationOptions.Count"/> exists,
/// but what an implementation does with it is unverified for every provider this could be pointed at, and
/// <see cref="MediaPolicy.ProviderSupportsBatchGeneration"/> is the constant that says so. Asking for four
/// and being handed two silently is the failure mode that a per-variant call makes impossible.
/// </para>
/// </remarks>
public interface IGeneratedImageProviderGateway
{
    /// <summary>Which deployment would serve a request made now.</summary>
    GeneratedImageModel Describe();

    /// <summary>Generates one image, and makes no claim about what came back beyond its being bytes.</summary>
    Task<GeneratedImageResult> GenerateAsync(string prompt, string? avoid, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IGeneratedImageProviderGateway"/>
/// <remarks>
/// <para>
/// <strong>Nothing about the request is logged but its shape.</strong> The prompt is private creator
/// content and an image provider's error body can quote it back, so a failure is recorded by exception type
/// and elapsed time alone. No credential, no endpoint, no response body (ai.md).
/// </para>
/// <para>
/// <strong>No retry here.</strong> Transport retries live in the ServiceDefaults pipeline beneath the
/// generator; the operation-level requeue above this is the only other layer. A third would multiply paid
/// attempts against the creator's spend, which for image generation is the most expensive mistake available.
/// </para>
/// <para>
/// <strong>Bytes only.</strong> The request asks for <see cref="ImageGenerationResponseFormat.Data"/>, and a
/// response that carries a link instead is <see cref="GeneratedImageProviderOutcome.InvalidResponse"/>
/// rather than something to go and fetch. Following a URL a response chose is a request this server makes to
/// an address a stranger picked, and no provider answer gets to decide what this host connects to.
/// </para>
/// </remarks>
public sealed class GeneratedImageProviderGateway(
    IImageGenerator generator,
    ILogger<GeneratedImageProviderGateway> logger) : IGeneratedImageProviderGateway
{
    public GeneratedImageModel Describe()
    {
        if (generator is UnconfiguredImageGenerator)
        {
            return new GeneratedImageModel(GeneratedImageModelStatus.NotConfigured);
        }

        var metadata = generator.GetService(typeof(ImageGeneratorMetadata)) as ImageGeneratorMetadata;
        var provider = Trimmed(metadata?.ProviderName);
        var model = Trimmed(metadata?.DefaultModelId);

        // Provenance is not decoration on a generated image: it is the only record of what made the bytes,
        // and a staged image with a guessed provider could never be told from one whose provider was known.
        // A deployment that will not say is a fact to record as a failure, not to paper over.
        return provider is null || model is null
            ? new GeneratedImageModel(GeneratedImageModelStatus.Unidentified)
            : new GeneratedImageModel(GeneratedImageModelStatus.Identified, provider, model, model);
    }

    public async Task<GeneratedImageResult> GenerateAsync(
        string prompt, string? avoid, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var clock = Stopwatch.StartNew();

        // Bounded here as well as by the transport, because a provider that accepts a connection and then
        // says nothing would otherwise hold the operation's lease until it lapsed — and a lapsed lease is
        // how two workers come to generate for one request.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MediaPolicy.GenerationProviderTimeout);

        ImageGenerationResponse response;

        try
        {
            response = await generator.GenerateAsync(
                new ImageGenerationRequest(Compose(prompt, avoid)),
                new ImageGenerationOptions
                {
                    // One, always. See MediaPolicy.ProviderSupportsBatchGeneration.
                    Count = 1,
                    ResponseFormat = ImageGenerationResponseFormat.Data,
                },
                timeout.Token);
        }
        catch (AiProviderNotConfiguredException)
        {
            return new GeneratedImageResult(GeneratedImageProviderOutcome.NotConfigured);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller is stopping, not the provider failing. Let the worker settle it as cancelled.
            throw;
        }
        catch (Exception exception)
        {
            var outcome = Classify(exception);

            logger.LogWarning(
                "Image generation failed after {ElapsedMs} ms as {Outcome}: {ExceptionType}.",
                clock.ElapsedMilliseconds,
                outcome,
                exception.GetType().Name);

            return new GeneratedImageResult(outcome);
        }

        return Read(response, clock);
    }

    /// <summary>
    /// Takes the one image out of the response, and refuses anything that is not bytes.
    /// </summary>
    /// <remarks>
    /// The first <see cref="DataContent"/> and no other, because the request asked for one. A provider that
    /// returned several when asked for one is not trusted to have returned four when asked for four, which
    /// is the whole reason each variant is its own call.
    /// </remarks>
    private GeneratedImageResult Read(ImageGenerationResponse response, Stopwatch clock)
    {
        var data = response.Contents.OfType<DataContent>().FirstOrDefault();

        if (data is null)
        {
            logger.LogError(
                "Image generation returned no image data after {ElapsedMs} ms ({ContentCount} content items).",
                clock.ElapsedMilliseconds,
                response.Contents.Count);

            return new GeneratedImageResult(GeneratedImageProviderOutcome.InvalidResponse);
        }

        if (data.Data.IsEmpty)
        {
            logger.LogError("Image generation returned empty image data after {ElapsedMs} ms.", clock.ElapsedMilliseconds);

            return new GeneratedImageResult(GeneratedImageProviderOutcome.InvalidResponse);
        }

        logger.LogInformation(
            "Generated one image of {SizeBytes} bytes in {ElapsedMs} ms.",
            data.Data.Length,
            clock.ElapsedMilliseconds);

        return new GeneratedImageResult(GeneratedImageProviderOutcome.Generated, data.Data);
    }

    /// <summary>
    /// Turns a provider failure into an outcome, from the shapes this module is allowed to name.
    /// </summary>
    /// <remarks>
    /// Conservative in the same way <c>DefaultAiFailureClassifier</c> is, and for the same reason: the
    /// interesting signals live on SDK exception types the domain may not reference. An unknown exception is
    /// transient, which is the right default for a transport error and the wrong one for a refusal — so the
    /// bound on attempts, not this method, is what stops a permanently failing request cycling.
    /// </remarks>
    private static GeneratedImageProviderOutcome Classify(Exception exception) => exception switch
    {
        // A per-attempt timeout surfaces as cancellation; the caller's token was checked before this.
        OperationCanceledException or TimeoutException => GeneratedImageProviderOutcome.Unavailable,

        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            GeneratedImageProviderOutcome.RateLimited,

        HttpRequestException { StatusCode: HttpStatusCode.RequestTimeout } =>
            GeneratedImageProviderOutcome.Unavailable,

        // Every other answer the provider actually formed and returned with a client-error status: it
        // decided, and it will decide the same way again.
        HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and < 500 =>
            GeneratedImageProviderOutcome.Refused,

        _ => GeneratedImageProviderOutcome.Unavailable,
    };

    /// <summary>
    /// Puts the avoid list after the prompt, under a plain heading of its own.
    /// </summary>
    /// <remarks>
    /// Both halves are the creator's own text and neither is a system instruction: an image provider is given
    /// one string, has no tools to be talked into using, and this workspace's words are all that is in it
    /// (ai.md). The heading is prose, not a delimiter a model is bound by — it tells the two apart for the
    /// provider and does not stop an avoid list worded as a directive from being read as one. That is
    /// acceptable here because the worst it can do is change a picture the same creator asked for.
    /// </remarks>
    private static string Compose(string prompt, string? avoid) =>
        string.IsNullOrWhiteSpace(avoid) ? prompt : $"{prompt}\n\nAvoid the following: {avoid}";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) || value.Length > MediaPolicy.ProviderIdentifierMaxLength
            ? null
            : value.Trim();
}
