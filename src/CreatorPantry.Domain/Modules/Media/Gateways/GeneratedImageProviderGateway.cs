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
/// <param name="Resolution">
/// The resolution tier this deployment will send when a request names none, so a picture's allowance can be
/// held at what the picture will cost (AP.2.5). Null when the model takes no tier, and also when the
/// deployment will not say — which is not the same thing, and is why
/// <see cref="GeneratedImageModelStatus.Unidentified"/> covers the second case.
/// </param>
/// <param name="Quality">The quality it will send on the same terms. Null when the model offers none.</param>
public sealed record GeneratedImageModel(
    GeneratedImageModelStatus Status,
    string? ProviderName = null,
    string? ModelName = null,
    string? ModelDeployment = null,
    string? Resolution = null,
    string? Quality = null);

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

    /// <summary>
    /// The provider generated a picture and then withheld it on its own safety grounds. Terminal, and
    /// <strong>billed</strong>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told apart from <see cref="Refused"/> because the money is the difference: a refused request was never
    /// generated and costs nothing, while a flagged one was generated, is charged for, and must be settled as
    /// such (AP.2.5). Both are terminal — asking again buys the same answer.
    /// </para>
    /// <para>
    /// <strong>Carried by <see cref="ImageWithheldException"/>, not by a status code.</strong> It was a status
    /// code first, and that was wrong: a provider's own 4xx includes request rejections — Venice answers
    /// <c>CONTENT_POLICY_VIOLATION</c> and <c>IMAGE_TOO_LARGE</c> as 422 — and reading those as flagged would
    /// bill a creator for a picture nobody made, which is the exact mistake this outcome exists to prevent in
    /// the other direction. A generator says so in as many words instead.
    /// </para>
    /// </remarks>
    Flagged = 7,
}

/// <param name="Bytes">Present exactly when the outcome is <see cref="GeneratedImageProviderOutcome.Generated"/>.</param>
/// <param name="QualitySent">The quality the provider was asked for, when its model takes one (AP.2.3).</param>
/// <param name="SizeSent">The size the provider was asked for, in the model's own terms.</param>
public sealed record GeneratedImageResult(
    GeneratedImageProviderOutcome Outcome,
    ReadOnlyMemory<byte> Bytes = default,
    string? QualitySent = null,
    string? SizeSent = null);

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
    /// <param name="prompt">The creator's prompt.</param>
    /// <param name="avoid">What to avoid, when the creator said.</param>
    /// <param name="settings">
    /// The model and settings a quality class sends (AP.3.1), or null for the deployment's own default. Passed
    /// through <see cref="ImageGenerationOptions"/> under provider-neutral keys; the generator refuses a value
    /// its model does not offer rather than sending it.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<GeneratedImageResult> GenerateAsync(
        string prompt, string? avoid, GeneratedImageSettings? settings, CancellationToken cancellationToken);
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
        if (provider is null || model is null)
        {
            return new GeneratedImageModel(GeneratedImageModelStatus.Unidentified);
        }

        // What it will send when nothing names a size. A deployment that answers nothing here is identified
        // but unpriceable: admission reads these to hold the picture's own price, and a generation admitted
        // against a guess would charge for a picture nobody asked for. Unidentified says exactly that — the
        // deployment will not say enough about itself to run a billable picture through it.
        var defaults = generator.GetService(typeof(ImageGenerationDefaults)) as ImageGenerationDefaults;

        return defaults is null
            ? new GeneratedImageModel(GeneratedImageModelStatus.Unidentified)
            : new GeneratedImageModel(
                GeneratedImageModelStatus.Identified,
                provider,
                model,
                model,
                defaults.Resolution,
                defaults.Quality);
    }

    public async Task<GeneratedImageResult> GenerateAsync(
        string prompt, string? avoid, GeneratedImageSettings? settings, CancellationToken cancellationToken)
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
                Options(settings),
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

        // What the generator says it asked for, under provider-neutral keys; absent from a generator that does
        // not say, and trimmed to the column like the provider's other identifiers.
        return new GeneratedImageResult(
            GeneratedImageProviderOutcome.Generated,
            data.Data,
            Bounded(ImageGenerationSettings.Read(data.AdditionalProperties, ImageGenerationSettings.QualitySent), MediaPolicy.GenerationQualityMaxLength),
            Bounded(ImageGenerationSettings.Read(data.AdditionalProperties, ImageGenerationSettings.SizeSent), MediaPolicy.GenerationSizeMaxLength));
    }

    private static string? Bounded(string? value, int maxLength) => value is { Length: > 0 } && value.Length <= maxLength ? value : null;

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
        // Generated and withheld, said by the generator rather than inferred from a status: the provider's own
        // 4xx are requests it would not run, and those cost nothing.
        ImageWithheldException => GeneratedImageProviderOutcome.Flagged,

        HttpRequestException { StatusCode: { } status } when (int)status is >= 400 and < 500 =>
            GeneratedImageProviderOutcome.Refused,

        _ => GeneratedImageProviderOutcome.Unavailable,
    };

    /// <summary>
    /// The request's options: one picture, as bytes, at the class's model and settings when there is a class.
    /// </summary>
    private static ImageGenerationOptions Options(GeneratedImageSettings? settings)
    {
        var options = new ImageGenerationOptions
        {
            // One, always. See MediaPolicy.ProviderSupportsBatchGeneration.
            Count = 1,
            ResponseFormat = ImageGenerationResponseFormat.Data,
        };

        if (settings is null)
        {
            return options;
        }

        options.ModelId = settings.ModelName;
        options.AdditionalProperties = [];

        if (settings.Quality is { } quality)
        {
            options.AdditionalProperties[ImageGenerationSettings.Quality] = quality;
        }

        if (settings.Resolution is { } resolution)
        {
            options.AdditionalProperties[ImageGenerationSettings.Resolution] = resolution;
        }

        return options;
    }

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
