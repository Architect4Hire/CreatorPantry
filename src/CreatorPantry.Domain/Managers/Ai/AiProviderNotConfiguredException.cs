namespace CreatorPantry.Domain.Managers.Ai;

/// <summary>
/// Thrown when application code reaches a model client on a host that has no model provider configured.
/// </summary>
/// <remarks>
/// This is a configuration fault, not a provider outage: the host started deliberately without a model
/// deployment, so the call could never have succeeded. See <see cref="UnconfiguredChatClient"/> for why the
/// unconfigured host fails here rather than at startup.
/// </remarks>
public sealed class AiProviderNotConfiguredException : InvalidOperationException
{
    public AiProviderNotConfiguredException(string capability)
        : base($"No model provider is configured for {capability}. The host started without a model "
            + "deployment, which is allowed only in development. Enable the provider in the AppHost, or "
            + "supply the deployment's connection string, before using this capability.")
    {
        Capability = capability;
    }

    /// <summary>The abstraction that was reached — <c>chat</c> or <c>embeddings</c>.</summary>
    public string Capability { get; }
}
