using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Content.Data;
using CreatorPantry.Domain.Modules.Content.Data.Entities;
using CreatorPantry.Domain.Modules.Content.Managers;
using CreatorPantry.Domain.Modules.Recipes.Facade;

namespace CreatorPantry.Domain.Modules.Content.Business;

/// <summary>
/// Reads the recipe versions a long-form revision is pinned to, as the export's attribution names them
/// (LF.5.1).
/// </summary>
public interface ILongFormExportRecipeReader
{
    /// <summary>
    /// One entry per pin, in pin order. A version that can no longer be read is a null entry rather than a
    /// missing one: the document was still written from it, and the attribution says so.
    /// </summary>
    Task<IReadOnlyList<LongFormExportRecipe?>> ReadAsync(
        IReadOnlyList<LongFormRevisionRecipePin> pins, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// The version that was pinned, not the recipe as it stands: read through the recipes module's facade by the
/// ids the revision holds, under the workspace filter. Only a title and a number are taken from it.
/// </remarks>
internal sealed class LongFormExportRecipeReader(IRecipeFacade recipes) : ILongFormExportRecipeReader
{
    public async Task<IReadOnlyList<LongFormExportRecipe?>> ReadAsync(
        IReadOnlyList<LongFormRevisionRecipePin> pins, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pins);

        var named = new List<LongFormExportRecipe?>(pins.Count);

        foreach (var pin in pins.OrderBy(pin => pin.SortOrder))
        {
            var snapshot = await recipes.GetSnapshotAsync(pin.RecipeId, pin.RecipeVersionId, cancellationToken);

            named.Add(snapshot.Succeeded
                ? new LongFormExportRecipe(snapshot.Value!.Document.Recipe.Title, snapshot.Value.VersionNumber)
                : null);
        }

        return named;
    }
}

/// <summary>An accepted long-form draft as one document (LF.5.1).</summary>
public interface ILongFormExportBusiness
{
    /// <summary>The draft's accepted revision as Markdown or HTML. Refused for a draft that is not accepted.</summary>
    Task<OperationResult<LongFormExportServiceModel>> ExportAsync(
        Guid draftId, LongFormExportFormat format, CancellationToken cancellationToken);
}

/// <inheritdoc />
/// <remarks>
/// <para>
/// <strong>A read.</strong> It writes no revision, moves no status and records no audit entry: an export is
/// the creator reading their own accepted article in another shape.
/// </para>
/// <para>
/// <strong>The accepted revision, by its id.</strong> Not the latest: while a draft is accepted they are the
/// same row, and once its sources have moved the article of record is still the one that was accepted.
/// </para>
/// <para>
/// Whether a draft may be exported is <see cref="LongFormExporter"/>'s decision, made from its status. This
/// only gathers what it reads.
/// </para>
/// </remarks>
internal sealed class LongFormExportBusiness(
    ILongFormDraftDataLayer dataLayer,
    ILongFormExportRecipeReader recipes) : ILongFormExportBusiness
{
    public async Task<OperationResult<LongFormExportServiceModel>> ExportAsync(
        Guid draftId, LongFormExportFormat format, CancellationToken cancellationToken)
    {
        var draft = await dataLayer.FindAsync(draftId, cancellationToken);

        if (draft is null)
        {
            return Failure(ContentErrorCodes.LongFormDraftNotFound, "That long-form draft does not exist.");
        }

        var revision = draft.AcceptedRevisionId is { } acceptedId
            ? await dataLayer.FindRevisionByIdAsync(draft.Id, acceptedId, cancellationToken)
            : null;

        if (revision is null)
        {
            return Failure(
                ContentErrorCodes.LongFormExportNotAccepted,
                "This piece has not been accepted yet. Accept it to export it.");
        }

        var pins = await dataLayer.ListPinsAsync(revision.Id, cancellationToken);

        var report = LongFormExporter.Export(
            new LongFormExportInput(
                draft.Kind,
                draft.Status,
                revision.RevisionNumber,
                await dataLayer.FindContextWorkingTitleAsync(draft.CreativeContextId, cancellationToken),
                LongFormDocument.Deserialize(revision.Content),
                await recipes.ReadAsync(pins, cancellationToken)),
            format);

        if (report.Content is null)
        {
            var refusal = report.Refusals[0];

            return Failure(refusal.Code, refusal.Message);
        }

        return OperationResult<LongFormExportServiceModel>.Success(new LongFormExportServiceModel(
            draft.Id,
            draft.Kind,
            format,
            revision.RevisionNumber,
            report.Title,
            report.Content,
            LongFormExportFileName.For(report.Title, draft.Kind, revision.RevisionNumber, format),
            [.. report.Warnings.Select(warning => new LongFormExportWarningServiceModel(warning.Code, warning.Message))]));
    }

    private static OperationResult<LongFormExportServiceModel> Failure(string code, string message) =>
        OperationResult<LongFormExportServiceModel>.Failure(
            new OperationError(code, message, new Dictionary<string, string[]>()));
}
