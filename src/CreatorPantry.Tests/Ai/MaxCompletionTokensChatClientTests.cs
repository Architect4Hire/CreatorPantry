using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using AzureAIInferenceClientOptions = Azure.AI.Inference.AzureAIInferenceClientOptions;
using ChatCompletionsClient = Azure.AI.Inference.ChatCompletionsClient;
using Azure.Core.Pipeline;
using CreatorPantry.AiProvider;
using Microsoft.Extensions.AI;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The output ceiling reaches the provider as <c>max_completion_tokens</c> (AP.2.2). The GPT-5 family refuses
/// <c>max_tokens</c> with a 400, which is what Azure.AI.Inference would otherwise send for
/// <see cref="ChatOptions.MaxOutputTokens"/>, so a ceiling sent the obvious way would fail every call.
/// </summary>
public sealed class MaxCompletionTokensChatClientTests
{
    /// <summary>
    /// The real SDK, over a captured HTTP exchange: what is asserted is the request body the provider would
    /// receive, not what a fake was handed.
    /// </summary>
    [Fact]
    public async Task The_ceiling_is_sent_as_max_completion_tokens_and_max_tokens_is_not_sent()
    {
        var handler = new CapturingHandler();
        var sdk = new ChatCompletionsClient(
            new Uri("https://example.openai.azure.com/openai/deployments/gpt-5.4-nano"),
            new AzureKeyCredential("test-key"),
            new AzureAIInferenceClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) });
        using var client = new MaxCompletionTokensChatClient(sdk.AsIChatClient("gpt-5.4-nano"));

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Write a headnote.")],
            new ChatOptions { MaxOutputTokens = 4_000 },
            TestContext.Current.CancellationToken);

        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(4_000, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("max_tokens", out _), handler.Body);
        Assert.Equal("Done.", response.Text);
    }

    [Fact]
    public async Task The_callers_options_are_not_changed()
    {
        var inner = new RecordingClient();
        using var client = new MaxCompletionTokensChatClient(inner);
        var options = new ChatOptions { MaxOutputTokens = 8_000 };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options, TestContext.Current.CancellationToken);

        Assert.Equal(8_000, options.MaxOutputTokens);
        Assert.Null(options.AdditionalProperties);
        Assert.Null(inner.Options!.MaxOutputTokens);
        Assert.Equal(8_000, inner.Options.AdditionalProperties![MaxCompletionTokensChatClient.Parameter]);
    }

    /// <summary>A call with no ceiling is passed through as it came, so the wrapper adds nothing of its own.</summary>
    [Fact]
    public async Task A_call_with_no_ceiling_is_passed_through_untouched()
    {
        var inner = new RecordingClient();
        using var client = new MaxCompletionTokensChatClient(inner);
        var options = new ChatOptions { Temperature = 0.2f };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], options, TestContext.Current.CancellationToken);
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], null, TestContext.Current.CancellationToken);

        Assert.Null(inner.Options);
        Assert.Same(options, inner.AllOptions[0]);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);

            const string answer = """
                {"id":"chatcmpl-1","object":"chat.completion","created":1767225600,"model":"gpt-5.4-nano-2026-03-17",
                 "choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"Done."}}],
                 "usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(answer, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingClient : IChatClient
    {
        public List<ChatOptions?> AllOptions { get; } = [];

        public ChatOptions? Options => AllOptions.LastOrDefault();

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            AllOptions.Add(options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
