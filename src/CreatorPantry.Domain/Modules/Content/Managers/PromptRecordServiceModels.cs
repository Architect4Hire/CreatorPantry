namespace CreatorPantry.Domain.Modules.Content.Managers;

/// <summary>
/// A prompt as the application reports it after saving: the row's identity, the text as stored, and the lineage
/// it is pinned to.
/// </summary>
/// <param name="PromptRecordId">The row's identity. Permanent, because the row is.</param>
/// <param name="Text">The prompt as stored — trimmed, so a client sees what the library actually holds.</param>
/// <param name="GeneratedText">The model's draft, when a model wrote one.</param>
/// <remarks>
/// <para>
/// It echoes the prompt back deliberately. The text was normalized on the way in, and a creator who typed
/// trailing whitespace or hit a length limit should be able to see what was kept without a second request.
/// </para>
/// <para>
/// <strong>No membership id, and no author at all.</strong> The row stores
/// <see cref="Data.Entities.PromptRecord.CreatedByMembershipId"/>, but the rule
/// <see cref="Recipes.Managers.RecipeDetailServiceModel"/> states applies here too: <c>WorkspaceId</c> and the
/// membership ids never leave the server. It was briefly published here and removed before anything consumed
/// it. The consequence, worth stating rather than discovering: a prompt cannot show who saved it, and could not
/// render it usefully if it could, because no route publishes another member's name. When a workspace members
/// endpoint exists, adding an author to <c>PRM-003</c>'s detail is a compatible change.
/// </para>
/// <para>
/// <strong>No URL, no object path, no bytes</strong>, and no generated-image or DAM-asset id — the first three
/// because a prompt record holds none (media.md), the last two because this seam does not accept them yet. See
/// <see cref="SavePromptRecordViewModel"/>.
/// </para>
/// </remarks>
public sealed record SavedPromptRecordServiceModel(
    Guid PromptRecordId,
    string ChannelKey,
    PromptImageKind ImageKind,
    string Text,
    string? GeneratedText,
    string? Label,
    PromptRecordSource Source,
    Guid? AiProposalId,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    string? PromptTemplateBodyChecksum,
    DateTimeOffset CreatedAt);

/// <summary>
/// One prompt as a library screen reads it: enough to recognise it in a list and decide which to open.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The prompt itself is not here.</strong> <paramref name="TextPreview"/> is truncated by the database
/// to <see cref="ContentPolicy.PromptPreviewMaxLength"/> characters — a hundred-row page must not carry a
/// hundred full prompts, which is what PRM-003's detail route is for. <paramref name="TextLength"/> comes with
/// it so a client can render an ellipsis honestly instead of guessing whether it has the whole thing.
/// </para>
/// <para>
/// <strong>No workspace id and no membership id</strong>, which never leave the server (tenancy.md); and no
/// template triple, model draft or proposal id, which are a prompt's full provenance and belong to the detail
/// route rather than to a list answering "which one was it". Adding an optional field later is compatible;
/// removing one is not (api-contract.md).
/// </para>
/// <para>
/// <strong>No URL, no object path and no bytes</strong> — a prompt record holds none, and
/// <c>PromptSearchProjectionTests</c> fails on a field named like one.
/// </para>
/// </remarks>
/// <param name="RecipeId">The recipe this prompt was written for, when it names one.</param>
/// <param name="RecipeVersionId">
/// The exact version it was written against, when it pins one. Present beside the recipe so a list can show
/// that a prompt is version-pinned without a second request; which version it is, is the detail route's.
/// </param>
public sealed record PromptSummaryServiceModel(
    Guid PromptRecordId,
    string ChannelKey,
    PromptImageKind ImageKind,
    string TextPreview,
    int TextLength,
    string? Label,
    PromptRecordSource Source,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    DateTimeOffset CreatedAt);

/// <summary>
/// One prompt in full: the whole text, the draft it came from, and every pin it was produced against.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the only place the prompt itself is published.</strong>
/// <see cref="PromptSummaryServiceModel.TextPreview"/> is truncated by the database and comes with a length so
/// a list can render an ellipsis honestly; <paramref name="Text"/> here is the whole thing, up to
/// <see cref="ContentPolicy.PromptTextMaxLength"/>. The model's draft and the template triple are published
/// here and nowhere else for the same reason — they are a prompt's full provenance, which is a question about
/// one prompt rather than about which prompt it was. There is no <c>textLength</c>: a client holding the text
/// can measure it.
/// </para>
/// <para>
/// <strong>A separate record from <see cref="SavedPromptRecordServiceModel"/>, whose fields it currently
/// matches exactly.</strong> Deliberate, and the same choice <see cref="Recipes.Managers.RecipeDetailServiceModel"/>
/// made against its own create response: the two are two contracts, and welding them would mean a field added
/// to one appearing on the other. The two that are coming are the reason — an author, once a workspace-members
/// endpoint can name one, and the asset ids once something can verify them — and neither belongs on the reply
/// to a save.
/// </para>
/// <para>
/// <strong>No author, and no membership id to build one from.</strong> The row stores
/// <see cref="Data.Entities.PromptRecord.CreatedByMembershipId"/> and it stays on the server, with
/// <c>WorkspaceId</c>, by the rule <see cref="Recipes.Managers.RecipeDetailServiceModel"/> states
/// (tenancy.md). The consequence, stated rather than discovered: <strong>a prompt cannot show who saved
/// it</strong> — and could not render it if it could, because no route publishes another member's name.
/// 12.3a published the raw id here by mistake and it was removed. Adding a resolved author later is a
/// compatible addition.
/// </para>
/// <para>
/// <strong>No <c>generatedImageId</c> and no <c>damAssetId</c>, although the row has columns for both.</strong>
/// Nothing can write them yet — the save seam refuses them, so they are null on every row that exists — and a
/// field that is always null documents a capability this server does not have. Each arrives with the release
/// that can verify the id: 12.6 and 12.9.
/// </para>
/// <para>
/// <strong>No URL, no object path and no bytes</strong>, because a prompt record holds none (media.md).
/// </para>
/// </remarks>
/// <param name="Text">
/// The authoritative prompt — what was actually used, after any creator edit — as stored, so trimmed.
/// </param>
/// <param name="GeneratedText">
/// The model's draft, when a model wrote one. Null for a manual prompt, and equal to <paramref name="Text"/>
/// when the creator accepted the draft unchanged; the two columns are kept side by side so the library can
/// still answer what the creator changed.
/// </param>
/// <param name="RecipeVersionId">
/// The exact version the prompt was written against, when it pins one. Never present without
/// <paramref name="RecipeId"/>, which the composite foreign key guarantees rather than merely expects.
/// </param>
public sealed record PromptDetailServiceModel(
    Guid PromptRecordId,
    string ChannelKey,
    PromptImageKind ImageKind,
    string Text,
    string? GeneratedText,
    string? Label,
    PromptRecordSource Source,
    Guid? AiProposalId,
    Guid? RecipeId,
    Guid? RecipeVersionId,
    string? PromptTemplateId,
    string? PromptTemplateVersion,
    string? PromptTemplateBodyChecksum,
    DateTimeOffset CreatedAt);

/// <summary>
/// One page of the prompt library: the rows, where to resume, and how many there are in total.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <em>not</em> the shared <c>CursorPageServiceModel&lt;T&gt;</c>, which is the published
/// <c>CursorPage</c> component and carries exactly two fields. Adding a total to that envelope would change a
/// shape already shipped on routes that have no total to report; a module-owned page that carries one is the
/// compatible way to differ — the same choice <see cref="Recipes.Managers.RecipeSearchPageServiceModel"/> made.
/// </para>
/// <para>
/// <see cref="NextCursor"/> is <c>null</c> on the last page, so a client loops until it is null rather than
/// comparing counts against a page size it may not have chosen.
/// </para>
/// </remarks>
/// <param name="TotalCount">
/// How many prompts match the filters across every page, or <c>null</c> when the caller asked not to be told.
/// <para>
/// Counted by a second statement, so under a concurrent write it can disagree with <see cref="Items"/> by
/// however many prompts were saved in between. That is the accepted cost of not paying for a windowed count on
/// every page. It is a count of rows, never a page number — the ordering is a keyset, and there is no page N
/// to jump to.
/// </para>
/// </param>
public sealed record PromptSearchPageServiceModel(
    IReadOnlyList<PromptSummaryServiceModel> Items,
    string? NextCursor,
    int? TotalCount);
