namespace CreatorPantry.AiProvider;

/// <summary>
/// The Aspire resource names of the model deployments, as <c>CreatorPantry.AppHost</c> declares them.
/// </summary>
/// <remarks>
/// Chat and embeddings are separate deployments rather than one resource with two uses, so they can be
/// sized, priced, swapped and traced independently — a cheap local chat model alongside a stable embedding
/// model is the normal case, and re-embedding a workspace is a far more expensive change than switching
/// chat models. Each name is also the configuration key the deployment's connection string arrives under.
/// </remarks>
public static class AiModelConnections
{
    /// <summary>The chat/completion deployment.</summary>
    public const string Chat = "chat";

    /// <summary>The text-embedding deployment backing semantic search and grounded retrieval.</summary>
    public const string Embeddings = "embeddings";
}
