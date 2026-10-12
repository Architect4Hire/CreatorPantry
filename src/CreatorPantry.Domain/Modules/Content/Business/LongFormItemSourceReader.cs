using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// Reads whether each item of a newsletter issue still has its source (LF.4.1), for the three reads of a draft
/// that say so: its plan, its sections and its document.
/// </summary>
internal static class LongFormItemSourceReader
{
    private static readonly IReadOnlyDictionary<Guid, LongFormItemSourceState> None =
        new Dictionary<Guid, LongFormItemSourceState>();

    /// <summary>
    /// Each item's state by section id; empty for a piece with no items, which costs no read.
    /// </summary>
    /// <remarks>
    /// The context's references are read under the workspace filter of whoever is asking, so a reference in
    /// another workspace is one this piece of work does not hold.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<Guid, LongFormItemSourceState>> ReadItemSourcesAsync(
        this ILongFormDraftDataLayer dataLayer,
        LongFormDraft draft,
        LongFormDocument document,
        IReadOnlyList<LongFormRevisionRecipePin> pins,
        CancellationToken cancellationToken)
    {
        if (draft.Kind is not LongFormKind.NewsletterIssue
            || !document.Sections.Any(section => section.Role is LongFormSectionRole.Item))
        {
            return None;
        }

        return LongFormItemSources.Resolve(
            document,
            [.. pins.Select(pin => new LongFormContextRecipeReference(pin.CreativeContextReferenceId, pin.RecipeId))],
            await dataLayer.ListRecipeReferencesAsync(draft.CreativeContextId, cancellationToken));
    }

    /// <summary>The unresolved items' section ids, in plan order.</summary>
    public static IReadOnlyList<Guid> Unresolved(
        this IReadOnlyDictionary<Guid, LongFormItemSourceState> sources, LongFormDocument document) =>
        [
            .. document.Sections
                .Where(section => sources.TryGetValue(section.Id, out var state) && state is LongFormItemSourceState.Unresolved)
                .Select(section => section.Id),
        ];
}
