namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// What a repository read of one asset's prompt lineage found: enough to name a prompt, never its words.
/// </summary>
/// <remarks>
/// A <c>Record</c> because it does not cross a module boundary —
/// <see cref="AssetPromptServiceModel"/> is what the facade returns. The two are separate so that widening the
/// internal read never widens the published one, which for prompt bodies is a rule rather than a habit (ai.md).
/// </remarks>
public sealed record AssetPromptRecord(
    Guid Id,
    string? Label,
    PromptImageKind ImageKind,
    PromptRecordSource Source,
    DateTimeOffset CreatedAt);

/// <summary>
/// One prompt in an asset's lineage, as another module reads it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>No prompt text, and that is the point.</strong> This exists so a DAM detail panel can say which
/// prompts produced an asset and let a creator open one; it is not a way for another module to read prompt
/// bodies it has no business holding. The words are reached by asking for one prompt by its id.
/// </para>
/// <para>
/// <see cref="Label"/> is the creator's own short name and is optional, so a caller rendering lineage needs a
/// fallback — the <see cref="ImageKind"/> and the date are always present.
/// </para>
/// </remarks>
public sealed record AssetPromptServiceModel(
    Guid Id,
    string? Label,
    PromptImageKind ImageKind,
    PromptRecordSource Source,
    DateTimeOffset CreatedAt);
