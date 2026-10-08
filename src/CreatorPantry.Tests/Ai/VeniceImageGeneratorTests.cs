using System.Net;
using System.Text;
using System.Text.Json;
using CreatorPantry.AiProvider;
using CreatorPantry.Domain.Modules.Media.Gateways;
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
    /// With safe mode on Venice answers 200 with a blurred picture and says so in a header. That is a
    /// refusal: staging it would offer a creator a smear as what they asked for.
    /// </summary>
    [Theory]
    [InlineData("x-venice-is-blurred")]
    [InlineData("x-venice-is-content-violation")]
    public async Task A_picture_venice_flagged_is_a_refusal_and_is_not_returned(string header)
    {
        var gateway = GatewayOver(new StubHandler(_ =>
        {
            var response = Generated(Picture);
            response.Headers.Add(header, "true");
            return response;
        }));

        var result = await gateway.GenerateAsync("a loaf of bread", avoid: null, TestContext.Current.CancellationToken);

        Assert.Equal(GeneratedImageProviderOutcome.Refused, result.Outcome);
        Assert.True(result.Bytes.IsEmpty);
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

        // No size in either dialect: Venice's models reject the one they do not speak.
        Assert.False(sent.TryGetProperty("width", out _));
        Assert.False(sent.TryGetProperty("aspect_ratio", out _));
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

    private static GeneratedImageProviderGateway GatewayOver(StubHandler handler) =>
        new(GeneratorOver(handler), NullLogger<GeneratedImageProviderGateway>.Instance);

    private static VeniceImageGenerator GeneratorOver(StubHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://venice.test/api/v1/") },
        Model,
        NullLogger<VeniceImageGenerator>.Instance);

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
