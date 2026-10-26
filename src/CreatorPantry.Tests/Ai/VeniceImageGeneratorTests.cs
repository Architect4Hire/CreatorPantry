using System.Net;
using System.Text;
using System.Text.Json;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Managers.Ai;
using CreatorPantry.Domain.Modules.Media.Gateways;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Ai;

// MEAI001: see UnconfiguredImageGenerator for why this is suppressed per file rather than project-wide.
#pragma warning disable MEAI001

/// <summary>
/// What <see cref="VeniceImageGenerator"/> is for: Venice's answers reach
/// <see cref="GeneratedImageProviderGateway"/> in the one shape that gateway may name, so a rate limit, a
/// refusal and a rejected key are told apart instead of all being retried as an outage.
/// </summary>
/// <remarks>
/// The outcome tests go through the real gateway rather than asserting on the thrown exception alone, because
/// the claim is about what the unchanged gateway decides — and that is the half a refactor of either side
/// would break without the other noticing.
/// </remarks>
public sealed class VeniceImageGeneratorTests
{
    private const string Model = "test-image-model";

    private const string PromptEchoingBody =
        """{"error":"Blocked: a bowl of SECRET-PROMPT-TEXT on a table"}""";

    private static readonly byte[] Picture = [1, 2, 3, 4];

    [Theory]
    [InlineData(429, GeneratedImageProviderOutcome.RateLimited)]
    [InlineData(400, GeneratedImageProviderOutcome.Refused)]
    [InlineData(401, GeneratedImageProviderOutcome.Refused)]
    [InlineData(402, GeneratedImageProviderOutcome.Refused)]
    [InlineData(403, GeneratedImageProviderOutcome.Refused)]
    // 422 included, and that is the point of listing it: Venice answers CONTENT_POLICY_VIOLATION and
    // IMAGE_TOO_LARGE that way, which are requests it would not run. Nothing was generated and nothing is
    // charged (AP.2.5) — a flagged picture is told apart by ImageWithheldException, below, not by a status.
    [InlineData(422, GeneratedImageProviderOutcome.Refused)]
    [InlineData(408, GeneratedImageProviderOutcome.Unavailable)]
    [InlineData(500, GeneratedImageProviderOutcome.Unavailable)]
    [InlineData(503, GeneratedImageProviderOutcome.Unavailable)]
    public async Task A_provider_status_becomes_the_outcome_the_gateway_already_knows(
        int status, GeneratedImageProviderOutcome expected)
    {
        var gateway = GatewayOver(new StubHandler(_ => Json((HttpStatusCode)status, PromptEchoingBody)));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task A_call_that_never_reached_the_provider_is_unavailable_not_refused()
    {
        var gateway = GatewayOver(new StubHandler(_ => throw new HttpRequestException("No connection.")));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.Unavailable, result.Outcome);
    }

    /// <summary>
    /// An image provider's error body can quote the prompt back. Nothing it wrote may travel onward: not in
    /// the message, and not as an inner exception.
    /// </summary>
    [Fact]
    public async Task A_refusal_carries_the_status_and_nothing_the_provider_wrote()
    {
        var generator = GeneratorOver(new StubHandler(_ => Json(HttpStatusCode.BadRequest, PromptEchoingBody)));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => generator.GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("SECRET-PROMPT-TEXT", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// With safe mode on Venice answers 200 with a blurred picture and says so in a header. That is a refusal
    /// to stage — a creator offered a smear as what they asked for is worse than being told no — and, since
    /// AP.2.5, a <em>billable</em> one: Venice made the picture and charges for it, so it comes back as
    /// <see cref="GeneratedImageProviderOutcome.Flagged"/> rather than as a request that never ran.
    /// </summary>
    /// <remarks>
    /// The generator says so with <see cref="ImageWithheldException"/>, which the gateway reads by type. It is
    /// deliberately not a status code: Venice's own 422s are requests it would not run, and charging for one
    /// of those would bill a creator for a picture nobody made.
    /// </remarks>
    [Theory]
    [InlineData("x-venice-is-blurred")]
    [InlineData("x-venice-is-content-violation")]
    public async Task A_picture_venice_flagged_is_billed_and_is_not_returned(string header)
    {
        var gateway = GatewayOver(new StubHandler(_ =>
        {
            var response = Generated(Picture);
            response.Headers.Add(header, "true");
            return response;
        }));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.Flagged, result.Outcome);
        Assert.True(result.Bytes.IsEmpty);
    }

    /// <summary>
    /// Venice's own <c>422</c> is a request it would not run — <c>CONTENT_POLICY_VIOLATION</c> carries a
    /// suggested prompt and a recommended model — so it generated nothing, costs nothing, and must not be read
    /// as the flagged-and-billed case above.
    /// </summary>
    /// <remarks>
    /// The two were one outcome for a day. Charging a creator for a picture that was never made is the worse
    /// half of that mistake, which is why this is its own test rather than a row in the theory.
    /// </remarks>
    [Fact]
    public async Task A_prompt_venice_will_not_run_is_refused_rather_than_billed_as_a_flagged_picture()
    {
        var gateway = GatewayOver(new StubHandler(_ => Json(
            HttpStatusCode.UnprocessableEntity,
            """{"error":"CONTENT_POLICY_VIOLATION","suggested_prompt":"a loaf","recommended_model":"x"}""")));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.Refused, result.Outcome);
        Assert.NotEqual(GeneratedImageProviderOutcome.Flagged, result.Outcome);
    }

    [Fact]
    public async Task A_generated_image_comes_back_as_its_bytes()
    {
        var handler = new StubHandler(_ =>
        {
            var response = Generated(Picture);
            response.Headers.Add("x-venice-is-blurred", "false");
            return response;
        });

        var result = await GatewayOver(handler)
            .GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.Generated, result.Outcome);
        Assert.Equal(Picture, result.Bytes.ToArray());
    }

    /// <summary>
    /// The request is the contract with Venice: the native route, the configured model, the creator's prompt,
    /// and the three choices this application makes on their behalf.
    /// </summary>
    [Fact]
    public async Task The_request_names_the_model_and_asks_for_an_unwatermarked_png()
    {
        var handler = new StubHandler(_ => Generated(Picture));

        await GeneratorOver(handler).GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("https://venice.test/api/v1/image/generate", handler.Uri?.ToString());

        using var body = JsonDocument.Parse(handler.Body!);
        var sent = body.RootElement;

        Assert.Equal(Model, sent.GetProperty("model").GetString());
        Assert.Equal("a loaf of bread", sent.GetProperty("prompt").GetString());
        Assert.Equal("png", sent.GetProperty("format").GetString());
        Assert.True(sent.GetProperty("hide_watermark").GetBoolean());
        Assert.True(sent.GetProperty("safe_mode").GetBoolean());
        Assert.False(sent.GetProperty("return_binary").GetBoolean());
    }

    /// <summary>
    /// Each sizing kind sends its own fields and none of another's (AP.2.3): Venice rejects a field a model does
    /// not take, so one sent "just in case" is a 400 on every picture. With nothing asked for, the profile's
    /// defaults are sent — never the model's own, which for the configured model is its dearest quality.
    /// </summary>
    [Theory]
    [InlineData(Model, "medium", "1K", "1:1", null, null)]
    [InlineData(TierNoQuality, null, "1K", "1:1", null, null)]
    [InlineData(AspectOnly, null, null, "1:1", null, null)]
    [InlineData(Pixels, null, null, null, 1024, 1024)]
    public async Task Each_sizing_kind_sends_its_own_fields_and_no_others(
        string model, string? quality, string? resolution, string? aspect, int? width, int? height)
    {
        var handler = new StubHandler(_ => Generated(Picture));

        await GeneratorOver(handler).GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"),
            new ImageGenerationOptions { ModelId = model },
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        var sent = body.RootElement;

        Assert.Equal(model, sent.GetProperty("model").GetString());
        AssertField(sent, "quality", quality);
        AssertField(sent, "resolution", resolution);
        AssertField(sent, "aspect_ratio", aspect);
        AssertField(sent, "width", width);
        AssertField(sent, "height", height);
        Assert.Equal("png", sent.GetProperty("format").GetString());
        Assert.True(sent.GetProperty("safe_mode").GetBoolean());
    }

    /// <summary>
    /// A value asked for through the provider-neutral keys is sent as the model spells it, so a later caller
    /// writing <c>2k</c> gets <c>2K</c> on the wire.
    /// </summary>
    [Fact]
    public async Task A_requested_quality_and_resolution_are_sent_as_the_model_names_them()
    {
        var handler = new StubHandler(_ => Generated(Picture));

        await GeneratorOver(handler).GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"),
            new ImageGenerationOptions
            {
                AdditionalProperties = new()
                {
                    [ImageGenerationSettings.Quality] = "HIGH",
                    [ImageGenerationSettings.Resolution] = "2k",
                    [ImageGenerationSettings.AspectRatio] = "3:2",
                },
            },
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("high", body.RootElement.GetProperty("quality").GetString());
        Assert.Equal("2K", body.RootElement.GetProperty("resolution").GetString());
        Assert.Equal("3:2", body.RootElement.GetProperty("aspect_ratio").GetString());
    }

    /// <summary>
    /// What was sent comes back on the picture, and through the Media gateway, so provenance records the request
    /// the picture came from.
    /// </summary>
    [Fact]
    public async Task What_was_sent_is_stamped_on_the_picture_and_reaches_the_gateway()
    {
        var handler = new StubHandler(_ => Generated(Picture));

        var response = await GeneratorOver(handler).GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"), cancellationToken: TestContext.Current.CancellationToken);
        var image = Assert.IsType<DataContent>(Assert.Single(response.Contents));

        Assert.Equal("medium", image.AdditionalProperties![ImageGenerationSettings.QualitySent]);
        Assert.Equal("1K 1:1", image.AdditionalProperties[ImageGenerationSettings.SizeSent]);

        var result = await GatewayOver(new StubHandler(_ => Generated(Picture)))
            .GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal("medium", result.QualitySent);
        Assert.Equal("1K 1:1", result.SizeSent);
    }

    [Theory]
    [InlineData(AspectOnly, null, "1:1")]
    [InlineData(Pixels, null, "1024x1024")]
    public async Task A_model_with_no_quality_stamps_only_its_size(string model, string? quality, string size)
    {
        var response = await GeneratorOver(new StubHandler(_ => Generated(Picture))).GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"),
            new ImageGenerationOptions { ModelId = model },
            TestContext.Current.CancellationToken);
        var image = Assert.IsType<DataContent>(Assert.Single(response.Contents));

        Assert.Equal(quality, image.AdditionalProperties!.TryGetValue(ImageGenerationSettings.QualitySent, out var sent) ? sent : null);
        Assert.Equal(size, image.AdditionalProperties[ImageGenerationSettings.SizeSent]);
    }

    /// <summary>
    /// Refused before anything is sent, and in the shape the gateway reads as refused and terminal: a model with
    /// no profile is not guessed at, a value the model does not offer is not sent, and a prompt past the model's
    /// limit is not cut.
    /// </summary>
    [Theory]
    [MemberData(nameof(RefusedBeforeSending))]
    public async Task A_request_the_model_cannot_take_is_refused_without_being_sent(
        string why, string prompt, ImageGenerationOptions options)
    {
        var handler = new StubHandler(_ => Generated(Picture));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => GeneratorOver(handler).GenerateAsync(
            new ImageGenerationRequest(prompt), options, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadRequest, failure.StatusCode);
        Assert.Null(handler.Method);
        Assert.False(string.IsNullOrWhiteSpace(why));

        var result = await new GeneratedImageProviderGateway(
                new RefusingGenerator(failure), NullLogger<GeneratedImageProviderGateway>.Instance)
            .GenerateAsync(prompt, avoid: null, TestContext.Current.CancellationToken);
        Assert.Equal(GeneratedImageProviderOutcome.Refused, result.Outcome);
    }

    public static TheoryData<string, string, ImageGenerationOptions> RefusedBeforeSending() => new()
    {
        { "a model with no profile", "a loaf", new ImageGenerationOptions { ModelId = "no-such-model" } },
        { "a prompt past the model's limit", new string('a', 101), new ImageGenerationOptions() },
        { "a quality the model does not offer", "a loaf", Asking(ImageGenerationSettings.Quality, "ultra") },
        { "a resolution the model does not offer", "a loaf", Asking(ImageGenerationSettings.Resolution, "8K") },
        { "a quality for a model that takes none", "a loaf", Asking(ImageGenerationSettings.Quality, "high", AspectOnly) },
        { "a pixel size for a tiered model", "a loaf", new ImageGenerationOptions { ImageSize = new System.Drawing.Size(1024, 1024) } },
        { "a pixel size off the divisor", "a loaf", new ImageGenerationOptions { ModelId = Pixels, ImageSize = new System.Drawing.Size(1000, 1001) } },
        { "a pixel size over the model's largest", "a loaf", new ImageGenerationOptions { ModelId = Pixels, ImageSize = new System.Drawing.Size(2048, 1024) } },
    };

    /// <summary>A refusal for an over-long prompt names the limit, never the prompt.</summary>
    [Fact]
    public async Task An_over_limit_refusal_does_not_carry_the_prompt()
    {
        var prompt = "secret family recipe " + new string('x', 100);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => GeneratorOver(new StubHandler(_ => Generated(Picture)))
            .GenerateAsync(new ImageGenerationRequest(prompt), cancellationToken: TestContext.Current.CancellationToken));

        Assert.DoesNotContain("secret family recipe", failure.Message, StringComparison.Ordinal);
        Assert.Contains("100", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(BrokenProfiles))]
    public void A_profile_that_cannot_be_used_says_why(string expected, VeniceModelProfile profile)
    {
        Assert.Contains(profile.Problems(), problem => problem.Contains(expected, StringComparison.Ordinal));
    }

    public static TheoryData<string, VeniceModelProfile> BrokenProfiles() => new()
    {
        { "Sizing", new VeniceModelProfile { PromptCharacterLimit = 10 } },
        { "PromptCharacterLimit", Profile(VeniceSizing.AspectRatio, aspects: ["1:1"], defaultAspect: "1:1", limit: 0) },
        { "DefaultResolution", Profile(VeniceSizing.Resolution, resolutions: ["1K"], defaultResolution: "2K", aspects: ["1:1"], defaultAspect: "1:1") },
        { "DefaultQuality", Profile(VeniceSizing.Resolution, resolutions: ["1K"], defaultResolution: "1K", aspects: ["1:1"], defaultAspect: "1:1", qualities: ["low"], defaultQuality: "high") },
        { "takes no Resolutions", Profile(VeniceSizing.AspectRatio, aspects: ["1:1"], defaultAspect: "1:1", resolutions: ["1K"]) },
        { "MaxDimension", new VeniceModelProfile { Sizing = VeniceSizing.Pixels, WidthHeightDivisor = 8, MaxDimension = 2048, DefaultWidth = 1024, DefaultHeight = 1024, PromptCharacterLimit = 10 } },
        { "multiples of WidthHeightDivisor", new VeniceModelProfile { Sizing = VeniceSizing.Pixels, WidthHeightDivisor = 8, MaxDimension = 1280, DefaultWidth = 1001, DefaultHeight = 1024, PromptCharacterLimit = 10 } },
        { "provenance can record", Profile(VeniceSizing.Resolution, resolutions: ["1K"], defaultResolution: "1K", aspects: ["1:1"], defaultAspect: "1:1", qualities: ["extraordinarily-high"], defaultQuality: "extraordinarily-high") },
    };

    [Fact]
    public void Every_test_profile_is_usable()
    {
        Assert.All(Profiles, profile => Assert.Empty(profile.Value.Problems()));
    }

    /// <summary>
    /// A 200 with nothing usable in it is an invalid response, not an outage: thrown from here it would be
    /// retried, and each retry is a generation paid for.
    /// </summary>
    [Theory]
    [InlineData("""{"images":[]}""")]
    [InlineData("""{"id":"generate-image-1"}""")]
    [InlineData("""{"images":["not base64 !!"]}""")]
    [InlineData("<html>bad gateway</html>")]
    public async Task An_answer_with_no_usable_image_is_an_invalid_response(string body)
    {
        var gateway = GatewayOver(new StubHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.InvalidResponse, result.Outcome);
    }

    /// <summary>
    /// The caller stopping is not the provider failing, and the gateway tells the two apart by exception
    /// type — so cancellation has to arrive as itself.
    /// </summary>
    [Fact]
    public async Task Cancellation_is_not_translated()
    {
        var generator = GeneratorOver(new StubHandler(_ => throw new OperationCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(
            new ImageGenerationRequest("a loaf of bread"), cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Provenance is read from the generator's own metadata, and it must say Venice and the configured model
    /// — that is the only record of what made the bytes.
    /// </summary>
    [Fact]
    public void Provenance_names_venice_and_the_configured_model()
    {
        var model = GatewayOver(new StubHandler(_ => Generated(Picture))).Describe();

        Assert.Equal(GeneratedImageModelStatus.Identified, model.Status);
        Assert.Equal(VeniceImageGenerator.ProviderName, model.ProviderName);
        Assert.Equal(Model, model.ModelName);
    }

    /// <summary>
    /// A body that says it is larger than any image can be is not read at all, and is an invalid response rather
    /// than an outage to retry and pay for again (15.2).
    /// </summary>
    [Fact]
    public async Task A_body_declaring_more_than_an_image_can_be_is_invalid_without_being_read()
    {
        var gateway = GatewayOver(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([]) { Headers = { ContentLength = VeniceImageGenerator.ResponseMaxBytes + 1 } },
        }));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.InvalidResponse, result.Outcome);
    }

    [Fact]
    public async Task A_body_of_unknown_length_is_abandoned_at_the_cap_rather_than_held_whole()
    {
        var endless = new EndlessStream();
        var gateway = GatewayOver(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(endless),
        }));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.InvalidResponse, result.Outcome);
        Assert.InRange(endless.BytesRead, VeniceImageGenerator.ResponseMaxBytes, VeniceImageGenerator.ResponseMaxBytes + (1024 * 1024));
    }

    [Fact]
    public void The_response_cap_holds_an_image_at_the_staging_cap_once_it_is_base64()
    {
        var encoded = 4 * ((MediaPolicy.ImageMaxBytes + 2) / 3);

        Assert.True(VeniceImageGenerator.ResponseMaxBytes > encoded + 64 * 1024);
    }

    /// <summary>Spaces forever, of no declared length: what a provider that never stops talking looks like.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer.AsSpan(offset, count).Fill((byte)' ');
            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static GeneratedImageProviderGateway GatewayOver(StubHandler handler) =>
        new(GeneratorOver(handler), NullLogger<GeneratedImageProviderGateway>.Instance);

    private static VeniceImageGenerator GeneratorOver(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://venice.test/api/v1/") },
        Model,
        Profiles,
        NullLogger<VeniceImageGenerator>.Instance);

    private const string TierNoQuality = "test-tier-model";
    private const string AspectOnly = "test-aspect-model";
    private const string Pixels = "test-pixel-model";

    /// <summary>One profile per sizing kind; the configured <see cref="Model"/> is tiered with qualities.</summary>
    private static readonly Dictionary<string, VeniceModelProfile> Profiles = new(StringComparer.OrdinalIgnoreCase)
    {
        [Model] = Profile(
            VeniceSizing.Resolution,
            resolutions: ["1K", "2K", "4K"], defaultResolution: "1K",
            qualities: ["low", "medium", "high"], defaultQuality: "medium",
            aspects: ["1:1", "3:2"], defaultAspect: "1:1",
            limit: 100),
        [TierNoQuality] = Profile(
            VeniceSizing.Resolution, resolutions: ["1K", "2K"], defaultResolution: "1K", aspects: ["1:1"], defaultAspect: "1:1"),
        [AspectOnly] = Profile(VeniceSizing.AspectRatio, aspects: ["1:1", "4:3"], defaultAspect: "1:1"),
        [Pixels] = new VeniceModelProfile
        {
            Sizing = VeniceSizing.Pixels,
            WidthHeightDivisor = 8,
            MaxDimension = 1280,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
            PromptCharacterLimit = 7500,
        },
    };

    private static VeniceModelProfile Profile(
        VeniceSizing sizing,
        string[]? resolutions = null,
        string? defaultResolution = null,
        string[]? qualities = null,
        string? defaultQuality = null,
        string[]? aspects = null,
        string? defaultAspect = null,
        int limit = 10_000)
    {
        var profile = new VeniceModelProfile
        {
            Sizing = sizing,
            DefaultResolution = defaultResolution,
            DefaultQuality = defaultQuality,
            DefaultAspectRatio = defaultAspect,
            PromptCharacterLimit = limit,
        };
        profile.Resolutions.AddRange(resolutions ?? []);
        profile.Qualities.AddRange(qualities ?? []);
        profile.AspectRatios.AddRange(aspects ?? []);

        return profile;
    }

    private static ImageGenerationOptions Asking(string key, string value, string? model = null) =>
        new() { ModelId = model, AdditionalProperties = new() { [key] = value } };

    private static void AssertField(JsonElement sent, string name, object? expected)
    {
        if (expected is null)
        {
            Assert.False(sent.TryGetProperty(name, out _), $"'{name}' was sent and the model does not take it.");
            return;
        }

        var value = sent.GetProperty(name);
        Assert.Equal(expected.ToString(), value.ValueKind == JsonValueKind.Number ? value.GetInt32().ToString() : value.GetString());
    }

    /// <summary>Stands in for the generator so the gateway's reading of a refusal can be checked on its own.</summary>
    private sealed class RefusingGenerator(Exception failure) : IImageGenerator
    {
        public Task<ImageGenerationResponse> GenerateAsync(
            ImageGenerationRequest request, ImageGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
            throw failure;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private static HttpResponseMessage Generated(byte[] bytes) => Json(
        HttpStatusCode.OK,
        $$"""{"id":"generate-image-1","images":["{{Convert.ToBase64String(bytes)}}"],"timing":{"total":1} }""");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return respond(request);
        }
    }
}
