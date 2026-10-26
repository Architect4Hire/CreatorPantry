using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Managers.FileInspection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.AiProvider;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// Venice.ai's image API as the rest of the application is allowed to see it: an
/// <see cref="IImageGenerator"/> whose failures arrive in the one shape the Media module's gateway may name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The native route, not the OpenAI-compatible one.</strong> Venice serves both.
/// <c>/images/generations</c> would have let an OpenAI SDK client be pointed here, but it caps a prompt at
/// 1,500 characters where a creator may write 4,000 and an avoid list beside it, and it answers an unknown
/// model id with Venice's default model instead of an error — so provenance would record a model that did not
/// make the picture. <c>/image/generate</c> requires the model, takes the longer prompt, and is the only one
/// of the two that can be told not to watermark. It needs no SDK, so this is plain HTTP.
/// </para>
/// <para>
/// <strong>The provider's words are dropped, not forwarded.</strong> An error body can quote the prompt back,
/// and a prompt is private creator content (ai.md). A failure crosses as <see cref="HttpRequestException"/>
/// carrying the status and one of this application's own sentences; the body is never read. That is the
/// shape <c>GeneratedImageProviderGateway</c> already maps: 429 is rate-limited, any other 4xx is refused and
/// terminal, 408/5xx/unreachable is unavailable.
/// </para>
/// <para>
/// <strong>A picture Venice flagged is a refusal, not a result.</strong> With safe mode on, a request it
/// objects to still answers 200 — with a blurred image and a header saying so. Staging that would offer a
/// creator a smear as the thing they asked for, so it is reported the way a refusal is.
/// </para>
/// <para>
/// <strong>Size and quality are always stated, in the model's own dialect</strong> (AP.2.3). Venice's models
/// disagree about how a size is expressed — a resolution tier and aspect ratio for some, an aspect ratio alone
/// for others, pixels for the rest — and each rejects the others' fields with a 400. Leaving them out left the
/// model's default in force, and <c>gpt-image-2-5-flare</c>'s default quality is its dearest. So every model
/// this sends to has a <see cref="VeniceModelProfile"/>; a model without one, a value its profile does not
/// offer, or a prompt past its limit is refused here, before anything is paid for — never guessed, never cut.
/// What was sent is stamped on the returned image (<see cref="ImageGenerationSettings"/>) for provenance.
/// Field names and limits are Venice's, read 2026-10-26: the request reference at
/// <c>https://docs.venice.ai/api-reference/endpoint/image/generate</c> (<c>additionalProperties: false</c>) and
/// each model's <c>model_spec.constraints</c> in <c>GET https://api.venice.ai/api/v1/models?type=image</c>.
/// </para>
/// <para>
/// <strong>The model a request may name is not yet recorded per picture.</strong> Provenance takes its model from
/// <see cref="ImageGeneratorMetadata"/>, which names the configured model; nothing sets
/// <see cref="ImageGenerationOptions.ModelId"/> today, so the two agree. Quality classes (AP.3.1) will, and must
/// record the model actually sent when they do.
/// </para>
/// </remarks>
public sealed class VeniceImageGenerator(
    HttpClient http,
    string model,
    IReadOnlyDictionary<string, VeniceModelProfile> profiles,
    ILogger<VeniceImageGenerator> logger) : IImageGenerator
{
    /// <summary>The name provenance records as the provider of every image this generates.</summary>
    public const string ProviderName = "venice";

    /// <summary>
    /// The status a flagged picture is reported under. Venice answered 200, so there is no status of its own
    /// to carry; this one is in the range the gateway reads as "it decided, and would decide the same again".
    /// </summary>
    public const HttpStatusCode FlaggedStatus = HttpStatusCode.UnprocessableEntity;

    private const string GeneratePath = "image/generate";
    private const string ImageMediaType = "image/png";

    private readonly ImageGeneratorMetadata metadata = new(ProviderName, http.BaseAddress, model);

    public async Task<ImageGenerationResponse> GenerateAsync(
        ImageGenerationRequest request,
        ImageGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);

        var clock = Stopwatch.StartNew();
        var modelId = options?.ModelId ?? model;

        if (!profiles.TryGetValue(modelId, out var profile))
        {
            logger.LogWarning("Venice image model {Model} has no sizing profile; nothing was sent.", modelId);

            throw Refused($"The image model '{modelId}' has no sizing profile under {VeniceModelProfile.SectionName}.");
        }

        // Counted here because a model's own limit is lower than the schema's for several of them, and an
        // over-long prompt is refused by Venice only after the request has been made. Never truncated: a cut
        // prompt is a different picture from the one the creator asked for.
        if (request.Prompt.Length > profile.PromptCharacterLimit)
        {
            logger.LogWarning(
                "A prompt of {Length} characters is over {Model}'s limit of {Limit}; nothing was sent.",
                request.Prompt.Length,
                modelId,
                profile.PromptCharacterLimit);

            throw Refused($"The prompt is longer than the {profile.PromptCharacterLimit:N0} characters '{modelId}' accepts.");
        }

        var sizing = profile.Settle(options);

        if (sizing.Refusal is { } refusal)
        {
            logger.LogWarning("Venice image request for {Model} refused before sending: {Reason}.", modelId, refusal);

            throw Refused($"The image request could not be sent: {refusal}.");
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, GeneratePath)
        {
            Content = JsonContent.Create(new GenerateRequest(modelId, request.Prompt)
            {
                Quality = sizing.Quality,
                Resolution = sizing.Resolution,
                AspectRatio = sizing.AspectRatio,
                Width = sizing.Width,
                Height = sizing.Height,
            }),
        };

        // Headers first, so the body is read under this class's own bound rather than buffered whole by the
        // client before anything here can look at its size.
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Venice refused an image after {ElapsedMs} ms with status {Status}.",
                clock.ElapsedMilliseconds,
                (int)response.StatusCode);

            throw new HttpRequestException(
                $"Venice answered with status {(int)response.StatusCode}.", inner: null, response.StatusCode);
        }

        if (IsFlagged(response, "x-venice-is-content-violation") || IsFlagged(response, "x-venice-is-blurred"))
        {
            logger.LogWarning(
                "Venice flagged an image after {ElapsedMs} ms; it was not staged.", clock.ElapsedMilliseconds);

            throw new HttpRequestException(
                "Venice flagged the image it generated.", inner: null, FlaggedStatus);
        }

        var bytes = await ReadImageAsync(response, cancellationToken);

        logger.LogInformation("Venice answered in {ElapsedMs} ms.", clock.ElapsedMilliseconds);

        // No content when there was no usable image: the gateway reads that as an invalid response, which is
        // what it is, where an exception from here would be retried as an outage and paid for again.
        return bytes is null
            ? new ImageGenerationResponse()
            : new ImageGenerationResponse([Stamped(new DataContent(bytes, ImageMediaType), sizing)]);
    }

    /// <summary>What was asked of the provider, on the picture it answered with.</summary>
    private static DataContent Stamped(DataContent image, VeniceSizingChoice sizing)
    {
        image.AdditionalProperties = new AdditionalPropertiesDictionary();

        if (sizing.Quality is { } quality)
        {
            image.AdditionalProperties[ImageGenerationSettings.QualitySent] = quality;
        }

        if (sizing.Size is { } size)
        {
            image.AdditionalProperties[ImageGenerationSettings.SizeSent] = size;
        }

        return image;
    }

    /// <summary>
    /// A request refused before it was sent, in the shape the Media gateway already reads as refused and
    /// terminal: the same request would be refused again, and nothing was spent on it.
    /// </summary>
    private static HttpRequestException Refused(string reason) =>
        new(reason, inner: null, HttpStatusCode.BadRequest);

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        if (serviceKey is not null)
        {
            return null;
        }

        return serviceType == typeof(ImageGeneratorMetadata) ? metadata
            : serviceType.IsInstanceOfType(this) ? this
            : null;
    }

    public void Dispose()
    {
        // The HttpClient belongs to the factory that made it.
    }

    private static bool IsFlagged(HttpResponseMessage response, string header) =>
        response.Headers.TryGetValues(header, out var values)
        && values.Any(value => string.Equals(value.Trim(), "true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The most of a 200 body this will read: one base64 image at the staging cap (32 MB becomes about 43 MB of
    /// text) and the JSON around it.
    /// </summary>
    /// <remarks>
    /// A body past it is an invalid response, like any other answer without a usable image: not an outage to
    /// retry and pay for again, and not something to hold in memory to find out. <c>VeniceImageGeneratorTests</c>
    /// holds this above what the staging cap encodes to.
    /// </remarks>
    public const long ResponseMaxBytes = 48L * 1024 * 1024;

    private async Task<byte[]?> ReadImageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > ResponseMaxBytes)
        {
            logger.LogError("Venice answered 200 with a body larger than an image can be; it was not read.");

            return null;
        }

        try
        {
            using var buffered = new MemoryStream();

            await using (var content = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                if (!await BoundedCopy.TryCopyAsync(content, buffered, ResponseMaxBytes, cancellationToken))
                {
                    logger.LogError("Venice answered 200 with a body larger than an image can be; it was not read.");

                    return null;
                }
            }

            buffered.Position = 0;
            var body = await JsonSerializer.DeserializeAsync<GenerateResponse>(
                buffered, JsonSerializerOptions.Web, cancellationToken);

            return body?.Images?.FirstOrDefault() is { Length: > 0 } image
                ? Convert.FromBase64String(image)
                : null;
        }
        catch (Exception reading) when (reading is JsonException or FormatException)
        {
            logger.LogError("Venice answered 200 with a body that was not an image: {ExceptionType}.", reading.GetType().Name);

            return null;
        }
    }

    /// <summary>
    /// The request body. PNG because that is what staging has been checking since the first provider; no
    /// watermark because the picture is the creator's; safe mode stated rather than left to Venice's default,
    /// since the handling of a flagged picture above depends on it. The size and quality fields are left out
    /// when null, because a model rejects the ones it does not take even when they are empty.
    /// </summary>
    private sealed record GenerateRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt)
    {
        [JsonPropertyName("quality")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Quality { get; init; }

        [JsonPropertyName("resolution")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Resolution { get; init; }

        [JsonPropertyName("aspect_ratio")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AspectRatio { get; init; }

        [JsonPropertyName("width")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Width { get; init; }

        [JsonPropertyName("height")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Height { get; init; }

        [JsonPropertyName("format")]
        public string Format => "png";

        [JsonPropertyName("hide_watermark")]
        public bool HideWatermark => true;

        [JsonPropertyName("safe_mode")]
        public bool SafeMode => true;

        [JsonPropertyName("return_binary")]
        public bool ReturnBinary => false;
    }

    private sealed record GenerateResponse([property: JsonPropertyName("images")] string[]? Images);
}
