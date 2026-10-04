using System.ComponentModel;
using System.Text;
using System.Text.Json;
using FluentValidation;

namespace CreatorPantry.Domain.Modules.Brand.Managers;

/// <summary>
/// The one definition of an acceptable guide edit draft, shared by the validator and Business so the two
/// cannot drift: a JSON object of at most 512 KB of UTF-8.
/// </summary>
/// <remarks>
/// <strong>512 KB rather than the setup session's 64 KB</strong>, and the figure is the guide's own ceilings
/// rather than a round number: eighteen keyed sections plus
/// <see cref="BrandPolicy.MaxStyleGuideChannelVariants"/> channel variants at
/// <see cref="BrandPolicy.StyleGuideSectionBodyMaxLength"/> characters each is over 300 KB of text before
/// fifty rules, fifty citations and JSON escaping. A cap below what a legitimate guide can hold would fail
/// autosave for exactly the creators with the most to lose.
/// </remarks>
public static class BrandStyleGuideEditSessionDraft
{
    /// <summary>The largest draft accepted, in UTF-8 bytes.</summary>
    public const int MaxBytes = 512 * 1024;

    /// <summary>A message describing why the draft is unacceptable, or null when it is fine.</summary>
    public static string? Problem(string? draft)
    {
        if (draft is null)
        {
            return "draftJson is required; send \"{}\" for an empty draft.";
        }

        if (draft.Length > MaxBytes || Encoding.UTF8.GetByteCount(draft) > MaxBytes)
        {
            return "draftJson is larger than 512 KB.";
        }

        try
        {
            using var document = JsonDocument.Parse(draft);

            return document.RootElement.ValueKind == JsonValueKind.Object
                ? null
                : "draftJson must be a JSON object.";
        }
        catch (JsonException)
        {
            return "draftJson must be valid JSON.";
        }
    }
}

/// <summary>
/// Saves the caller's unsaved edit of one style guide, creating it on first save.
/// </summary>
/// <remarks>
/// <para>
/// Carries no workspace, guide or user: the workspace comes from the route and the caller's membership, the
/// guide is a route segment, the user is the authenticated caller, and the concurrency token is the
/// <c>If-Match</c> header.
/// </para>
/// <para>
/// <strong>This writes nothing the guide says.</strong> The draft is scratch; the version a creator means to
/// keep is written by <c>POST .../versions</c>, which is also what clears this.
/// </para>
/// </remarks>
public sealed record SaveBrandStyleGuideEditSessionViewModel
{
    /// <summary>
    /// The guide's working version this draft was composed against, as the guide read reported it. Required.
    /// </summary>
    /// <remarks>
    /// Stored rather than checked: a draft written against an older version is not refused — the creator's
    /// words are theirs and losing them to a refusal would be the worse failure — it is reported as stale so
    /// the editor can say what happened before they save.
    /// </remarks>
    [Description("The working version number this draft was composed against. Stored, and reported back as stale when the guide has moved past it.")]
    public int? BaselineVersionNumber { get; init; }

    /// <summary>
    /// An opaque JSON object holding the editor's in-progress change. At most 512 KB. Stored verbatim, never
    /// interpreted, never logged, and never a source of truth: style guide versions are.
    /// </summary>
    [Description("The in-progress edit as an opaque JSON object, at most 512 KB. Stored exactly as sent.")]
    public string? DraftJson { get; init; }
}

/// <summary>
/// Shape validation for <see cref="SaveBrandStyleGuideEditSessionViewModel"/>.
/// </summary>
/// <remarks>
/// Shape only. Whether the guide exists, whether the draft's baseline is still the working version, and
/// whether the caller already has a draft are facts about the workspace's data, and backend.md keeps those in
/// Business.
/// </remarks>
public sealed class SaveBrandStyleGuideEditSessionViewModelValidator
    : AbstractValidator<SaveBrandStyleGuideEditSessionViewModel>
{
    public SaveBrandStyleGuideEditSessionViewModelValidator()
    {
        RuleFor(model => model.BaselineVersionNumber)
            .NotNull()
            .WithMessage("Name the version this edit was made against.")
            .GreaterThanOrEqualTo(1)
            .WithMessage("Version numbers start at 1.");

        RuleFor(model => model).Custom((model, context) =>
        {
            if (BrandStyleGuideEditSessionDraft.Problem(model.DraftJson) is { } problem)
            {
                context.AddFailure(nameof(model.DraftJson), problem);
            }
        });
    }
}

/// <summary>
/// The caller's own unsaved edit of one guide, as the application publishes it.
/// </summary>
/// <param name="BaselineVersionNumber">The working version this draft was composed against.</param>
/// <param name="WorkingVersionNumber">The guide's working version now.</param>
/// <param name="IsStale">
/// Whether a version has been written since this draft was started — the two numbers differing, decided
/// server-side so a client cannot reach a different answer about it. A stale draft is still the creator's
/// work and is returned in full; what it needs is to be told about, because saving it will be refused until
/// the editor re-reads the guide.
/// </param>
/// <param name="DraftJson">The opaque draft, a JSON object as text, exactly as saved.</param>
/// <param name="RowVersion">Opaque concurrency token; quote it as <c>If-Match</c> on the next write.</param>
/// <remarks>Carries no workspace or user identifier.</remarks>
public sealed record BrandStyleGuideEditSessionServiceModel(
    Guid GuideId,
    int BaselineVersionNumber,
    int WorkingVersionNumber,
    bool IsStale,
    string DraftJson,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string RowVersion);
