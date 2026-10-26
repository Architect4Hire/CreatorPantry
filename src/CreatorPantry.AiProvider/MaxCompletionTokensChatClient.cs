using Microsoft.Extensions.AI;

namespace CreatorPantry.AiProvider;

/// <summary>
/// Sends a call's output ceiling as <c>max_completion_tokens</c> rather than <c>max_tokens</c> (AP.2.2).
/// </summary>
/// <remarks>
/// <para>
/// The domain sets the provider-neutral <see cref="ChatOptions.MaxOutputTokens"/>. Azure.AI.Inference maps that
/// to <c>max_tokens</c>, which the GPT-5 family and the other reasoning models refuse outright with a 400 —
/// every call would fail, not merely run unbounded. <c>max_completion_tokens</c> is the parameter those models
/// take, it is accepted by the non-reasoning chat models too, and on a reasoning model it bounds the hidden
/// reasoning as well as the visible answer, which is the bound a cost ceiling needs.
/// </para>
/// <para>
/// Here, because the parameter's name is provider wire detail and this is the one assembly allowed to know it.
/// The SDK has no property for it, so it travels as an additional request property, which the client writes
/// into the request body as given.
/// </para>
/// </remarks>
public sealed class MaxCompletionTokensChatClient(IChatClient innerClient) : DelegatingChatClient(innerClient)
{
    /// <summary>The request-body field the ceiling is sent as.</summary>
    public const string Parameter = "max_completion_tokens";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Rewrite(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Rewrite(options), cancellationToken);

    /// <summary>A copy with the ceiling moved; the caller's options are never changed.</summary>
    private static ChatOptions? Rewrite(ChatOptions? options)
    {
        if (options?.MaxOutputTokens is not { } ceiling)
        {
            return options;
        }

        var rewritten = options.Clone();
        rewritten.MaxOutputTokens = null;
        rewritten.AdditionalProperties = rewritten.AdditionalProperties?.Clone() ?? new();
        rewritten.AdditionalProperties[Parameter] = ceiling;

        return rewritten;
    }
}
