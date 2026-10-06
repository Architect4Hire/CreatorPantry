namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A prompt another module asks this one to save, from inside that module's own transaction (DAM-001).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Not <see cref="SavePromptRecordViewModel"/>, and the reason is a boundary rule.</strong> A
/// ViewModel is HTTP input and one of the module-internal types that may never cross a module boundary —
/// <c>ModuleBoundaryTests</c> refuses it by name. The Media module needs to hand this module a prompt, so
/// the crossing type has to be one this module publishes for that purpose. It carries the same fields; the
/// facade maps it.
/// </para>
/// <para>
/// <strong>It cannot name an asset.</strong> The caller is the thing creating the asset, and the id is
/// filled in by the facade from the transaction that commits both — a prompt record is immutable and
/// cannot gain one afterwards.
/// </para>
/// </remarks>
public sealed record PromptRecordSaveInput
{
    public string? ChannelKey { get; init; }

    public PromptImageKind? ImageKind { get; init; }

    public string? Text { get; init; }

    public string? GeneratedText { get; init; }

    public string? Label { get; init; }

    public PromptRecordSource? Source { get; init; }

    public Guid? AiProposalId { get; init; }

    public Guid? RecipeId { get; init; }

    public Guid? RecipeVersionId { get; init; }

    public Guid? GeneratedImageId { get; init; }

    public string? PromptTemplateId { get; init; }

    public string? PromptTemplateVersion { get; init; }

    public string? PromptTemplateBodyChecksum { get; init; }
}
