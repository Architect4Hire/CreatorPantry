using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Managers.Time;
using CreatorPantry.Domain.Modules.Ingredients.Facade;
using CreatorPantry.Domain.Modules.Ingredients.Managers;
using CreatorPantry.Domain.Modules.Measurement.Facade;
using CreatorPantry.Domain.Modules.Nutrition.Data;
using CreatorPantry.Domain.Modules.Nutrition.Data.Entities;
using CreatorPantry.Domain.Modules.Nutrition.Managers;

namespace CreatorPantry.Domain.Modules.Nutrition.Business;

/// <summary>
/// The lifecycle of a workspace's nutrition mapping for one platform ingredient (14.2, B-34).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing automatic is ever confirmed.</strong> A proposal is a <see cref="NutritionMappingStatus.Candidate"/>
/// whatever its confidence, and only a creator's confirmation or remap makes a mapping one an estimate may read.
/// </para>
/// <para>
/// <strong>A source that cannot be asked is never read as an answer.</strong> Not configured, throttled or down,
/// a proposal, remap or refresh writes nothing and says so; only the source saying a record is gone or
/// unapproved withdraws a reference.
/// </para>
/// <para>
/// <strong>Every change quotes the version it was read at</strong>, and a recipe is never touched.
/// </para>
/// </remarks>
public interface INutritionMappingBusiness
{
    Task<OperationResult<NutritionMappingServiceModel>> GetAsync(Guid ingredientId, CancellationToken cancellationToken);

    Task<IReadOnlyList<NutritionMappingServiceModel>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken);

    /// <summary>
    /// Proposes the first approved reference for the ingredient that this workspace has not rejected, or records
    /// that there is none. A mapping that is already a candidate is returned as it is; a confirmed one, or one a
    /// creator declined, is a conflict — changing either is a remap.
    /// </summary>
    Task<OperationResult<NutritionMappingServiceModel>> ProposeAsync(Guid ingredientId, CancellationToken cancellationToken);

    /// <summary>Confirms a candidate, or the newer version of a reference under review.</summary>
    Task<OperationResult<NutritionMappingServiceModel>> ConfirmAsync(
        Guid ingredientId, ConfirmNutritionMappingViewModel model, CancellationToken cancellationToken);

    /// <summary>Turns down the reference the mapping holds, which no proposal offers again, and leaves it unresolved.</summary>
    Task<OperationResult<NutritionMappingServiceModel>> RejectAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken);

    /// <summary>Points the mapping at a reference the creator chose, from any state or none, and confirms it.</summary>
    Task<OperationResult<NutritionMappingServiceModel>> RemapAsync(
        Guid ingredientId, RemapNutritionMappingViewModel model, CancellationToken cancellationToken);

    /// <summary>Records that no reference fits — or that this ingredient has no nutrition to count.</summary>
    Task<OperationResult<NutritionMappingServiceModel>> MarkUnresolvedAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the source about the reference again: a newer version puts a confirmed mapping under review, and a
    /// record gone or no longer approved withdraws it.
    /// </summary>
    Task<OperationResult<NutritionMappingServiceModel>> RefreshAsync(Guid ingredientId, CancellationToken cancellationToken);
}

internal sealed class NutritionMappingBusiness(
    INutritionMappingDataLayer dataLayer,
    IIngredientFacade ingredients,
    IMeasurementFacade measurement,
    IWorkspaceContext workspace,
    IClock clock) : INutritionMappingBusiness
{
    public async Task<OperationResult<NutritionMappingServiceModel>> GetAsync(
        Guid ingredientId, CancellationToken cancellationToken) =>
        await dataLayer.FindAsync(ingredientId, cancellationToken) is { } mapping
            ? Success(mapping)
            : Failure(NotFound());

    public async Task<IReadOnlyList<NutritionMappingServiceModel>> ListAsync(
        IReadOnlyCollection<Guid> ingredientIds, CancellationToken cancellationToken) =>
        [.. (await dataLayer.ListAsync(ingredientIds, cancellationToken)).Select(ToServiceModel)];

    public async Task<OperationResult<NutritionMappingServiceModel>> ProposeAsync(
        Guid ingredientId, CancellationToken cancellationToken)
    {
        var mapping = await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken);

        switch (mapping?.Status)
        {
            case NutritionMappingStatus.Candidate:
                // Already proposed and awaiting a creator: asking again changes nothing.
                return Success(mapping);
            case NutritionMappingStatus.Confirmed or NutritionMappingStatus.NeedsReview:
                return Failure(Conflict("This ingredient's reference is already confirmed. Remap it to change it."));
            case NutritionMappingStatus.Unresolved when mapping.UnresolvedReason is NutritionUnresolvedReason.CreatorDeclined:
                // A creator said no reference fits. A proposal is the system's suggestion, and does not overrule
                // that; a creator who changes their mind remaps.
                return Failure(Conflict("A creator decided no reference fits this ingredient. Remap it to change that."));
        }

        var search = await ingredients.SearchNutritionReferencesAsync(ingredientId, cancellationToken);
        if (search.Status is NutritionReferenceStatus.NotFound)
        {
            return Failure(IngredientUnprocessable());
        }

        if (search.Status is not NutritionReferenceStatus.Found)
        {
            return Failure(SourceUnavailable());
        }

        var rejected = RejectedReferences(mapping);
        var open = search.Candidates.Where(candidate => !rejected.Contains((candidate.SourceCode, candidate.ExternalRecordId))).ToList();

        var isNew = mapping is null;
        mapping ??= NewMapping(ingredientId);

        // Each open candidate in the source's order, until one is still there and approved when fetched. A
        // fetch the source cannot answer stops the proposal rather than skipping to a worse candidate.
        foreach (var candidate in open)
        {
            var reference = await ingredients.GetNutritionReferenceAsync(candidate.SourceCode, candidate.ExternalRecordId, cancellationToken);
            if (reference.Status is NutritionReferenceStatus.NotFound or NutritionReferenceStatus.NoApprovedSource)
            {
                continue;
            }

            if (reference.Status is not NutritionReferenceStatus.Found)
            {
                return Failure(SourceUnavailable());
            }

            SetReference(mapping, reference, NutritionMatchMethod.AutomaticSearch, ConfidenceOf(candidate, open.Count, search.IngredientName));
            mapping.Status = NutritionMappingStatus.Candidate;
            ClearConfirmation(mapping);
            ClearPortion(mapping);

            return await SaveAsync(mapping, isNew, audit: null, cancellationToken);
        }

        SetUnresolved(mapping, NutritionUnresolvedReason.NoCandidates);
        return await SaveAsync(mapping, isNew, audit: null, cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> ConfirmAsync(
        Guid ingredientId, ConfirmNutritionMappingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken) is not { } mapping)
        {
            return Failure(NotFound());
        }

        if (!NutritionMappingConcurrencyToken.Matches(model.ExpectedConcurrencyToken, mapping.RowVersion))
        {
            return Failure(Stale());
        }

        if (mapping.Status is not (NutritionMappingStatus.Candidate or NutritionMappingStatus.NeedsReview))
        {
            return Failure(Conflict($"Only a proposed reference, or one under review, can be confirmed; this one is {mapping.Status}."));
        }

        if (model.Portion is { } portion)
        {
            var reference = await ingredients.GetNutritionReferenceAsync(mapping.SourceCode!, mapping.ExternalRecordId!, cancellationToken);
            if (await ResolvePortionAsync(portion, reference, cancellationToken) is not { } resolved)
            {
                return Failure(PortionUnprocessable());
            }

            ApplyPortion(mapping, resolved);
        }

        var from = mapping.Status;
        if (mapping.Status is NutritionMappingStatus.NeedsReview)
        {
            // Confirming a review accepts the newer version of the same record.
            mapping.SourceVersion = mapping.ReviewSourceVersion;
            mapping.ReviewSourceVersion = null;
        }

        mapping.Status = NutritionMappingStatus.Confirmed;
        mapping.ConfirmedAt = clock.UtcNow;
        mapping.ConfirmedByMembershipId = workspace.MembershipId;

        return await SaveAsync(mapping, isNew: false, Audit(mapping, NutritionAuditActions.Confirmed, $"Confirmed the reference from {from}."), cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> RejectAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        if (await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken) is not { } mapping)
        {
            return Failure(NotFound());
        }

        if (!NutritionMappingConcurrencyToken.Matches(model.ExpectedConcurrencyToken, mapping.RowVersion))
        {
            return Failure(Stale());
        }

        if (mapping.Status is NutritionMappingStatus.Unresolved)
        {
            return Failure(Conflict("There is no reference on this mapping to reject."));
        }

        var from = mapping.Status;
        AddRejection(mapping, mapping.SourceCode!, mapping.ExternalRecordId!);
        SetUnresolved(mapping, NutritionUnresolvedReason.ReferenceRejected);

        return await SaveAsync(mapping, isNew: false, Audit(mapping, NutritionAuditActions.Rejected, $"Rejected the reference from {from}."), cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> RemapAsync(
        Guid ingredientId, RemapNutritionMappingViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var mapping = await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken);
        if (mapping is null)
        {
            if (!await ingredients.IsUsableAsync(ingredientId, cancellationToken))
            {
                return Failure(IngredientUnprocessable());
            }
        }
        else if (!NutritionMappingConcurrencyToken.Matches(model.ExpectedConcurrencyToken, mapping.RowVersion))
        {
            return Failure(Stale());
        }

        var reference = await ingredients.GetNutritionReferenceAsync(model.SourceCode, model.ExternalRecordId, cancellationToken);
        switch (reference.Status)
        {
            case NutritionReferenceStatus.NotFound or NutritionReferenceStatus.NoApprovedSource:
                return Failure(new OperationError(
                    NutritionErrorCodes.ReferenceUnprocessable,
                    "That is not an approved nutrition reference.",
                    Fields(("externalRecordId", "Choose a record from an approved source."))));
            case not NutritionReferenceStatus.Found:
                return Failure(SourceUnavailable());
        }

        ResolvedPortion? resolved = null;
        if (model.Portion is { } portion && (resolved = await ResolvePortionAsync(portion, reference, cancellationToken)) is null)
        {
            return Failure(PortionUnprocessable());
        }

        var isNew = mapping is null;
        mapping ??= NewMapping(ingredientId);
        var sameReference = mapping.SourceCode == reference.SourceCode && mapping.ExternalRecordId == reference.ExternalRecordId;
        var from = isNew ? "none" : mapping.Status.ToString();

        SetReference(mapping, reference, NutritionMatchMethod.CreatorSelected, confidence: null);
        mapping.Status = NutritionMappingStatus.Confirmed;
        mapping.ConfirmedAt = clock.UtcNow;
        mapping.ConfirmedByMembershipId = workspace.MembershipId;

        // A creator choosing a reference they once rejected has changed their mind; the rejection goes.
        mapping.Rejections.RemoveAll(rejection =>
            rejection.SourceCode == reference.SourceCode && rejection.ExternalRecordId == reference.ExternalRecordId);

        if (resolved is not null)
        {
            ApplyPortion(mapping, resolved);
        }
        else if (!sameReference)
        {
            // A portion measured against one food says nothing about another.
            ClearPortion(mapping);
        }

        return await SaveAsync(mapping, isNew, Audit(mapping, NutritionAuditActions.Remapped, $"Remapped the reference from {from}."), cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> MarkUnresolvedAsync(
        Guid ingredientId, NutritionMappingDecisionViewModel model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        var mapping = await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken);
        if (mapping is null)
        {
            if (!await ingredients.IsUsableAsync(ingredientId, cancellationToken))
            {
                return Failure(IngredientUnprocessable());
            }
        }
        else if (!NutritionMappingConcurrencyToken.Matches(model.ExpectedConcurrencyToken, mapping.RowVersion))
        {
            return Failure(Stale());
        }

        var isNew = mapping is null;
        mapping ??= NewMapping(ingredientId);

        if (mapping is { Status: NutritionMappingStatus.Unresolved, UnresolvedReason: NutritionUnresolvedReason.CreatorDeclined })
        {
            return Success(mapping);
        }

        var from = isNew ? "none" : mapping.Status.ToString();
        SetUnresolved(mapping, NutritionUnresolvedReason.CreatorDeclined);

        return await SaveAsync(mapping, isNew, Audit(mapping, NutritionAuditActions.MarkedUnresolved, $"Marked unresolved from {from}."), cancellationToken);
    }

    public async Task<OperationResult<NutritionMappingServiceModel>> RefreshAsync(
        Guid ingredientId, CancellationToken cancellationToken)
    {
        if (await dataLayer.FindForUpdateAsync(ingredientId, cancellationToken) is not { } mapping)
        {
            return Failure(NotFound());
        }

        if (mapping.Status is NutritionMappingStatus.Unresolved)
        {
            return Success(mapping);
        }

        var reference = await ingredients.GetNutritionReferenceAsync(mapping.SourceCode!, mapping.ExternalRecordId!, cancellationToken);

        switch (reference.Status)
        {
            case NutritionReferenceStatus.NotFound or NutritionReferenceStatus.NoApprovedSource:
            {
                // The source itself says the record is gone or no longer approved: the only answer that withdraws one.
                var wasConfirmed = mapping.Status is not NutritionMappingStatus.Candidate;
                var from = mapping.Status;
                SetUnresolved(mapping, NutritionUnresolvedReason.ReferenceWithdrawn);

                return await SaveAsync(
                    mapping,
                    isNew: false,
                    wasConfirmed ? Audit(mapping, NutritionAuditActions.ReferenceWithdrawn, $"The source withdrew the reference; was {from}.") : null,
                    cancellationToken);
            }

            case not NutritionReferenceStatus.Found:
                return Failure(SourceUnavailable());
        }

        var latest = reference.SourceVersion!;
        var current = mapping.Status is NutritionMappingStatus.NeedsReview ? mapping.ReviewSourceVersion : mapping.SourceVersion;
        if (latest == current)
        {
            return Success(mapping);
        }

        AuditEntry? audit = null;
        switch (mapping.Status)
        {
            case NutritionMappingStatus.Candidate:
                // Nobody has confirmed it yet, so there is nothing to review: the proposal simply moves on.
                mapping.SourceVersion = latest;
                mapping.ReferenceDescription = reference.Description;
                break;
            case NutritionMappingStatus.Confirmed:
                mapping.Status = NutritionMappingStatus.NeedsReview;
                mapping.ReviewSourceVersion = latest;
                audit = Audit(mapping, NutritionAuditActions.FlaggedForReview, "The source published a newer version of the confirmed reference.");
                break;
            default:
                mapping.ReviewSourceVersion = latest;
                break;
        }

        return await SaveAsync(mapping, isNew: false, audit, cancellationToken);
    }

    // ---- State changes ----

    private NutritionMapping NewMapping(Guid ingredientId)
    {
        var now = clock.UtcNow;

        return new NutritionMapping
        {
            Id = Guid.NewGuid(),
            WorkspaceId = workspace.WorkspaceId,
            IngredientId = ingredientId,
            CreatedAt = now,
            CreatedByMembershipId = workspace.MembershipId,
            UpdatedAt = now,
            UpdatedByMembershipId = workspace.MembershipId,
        };
    }

    private static void SetReference(
        NutritionMapping mapping, NutritionReferenceServiceModel reference, NutritionMatchMethod method, NutritionMatchConfidence? confidence)
    {
        mapping.ReferenceSourceId = reference.ReferenceSourceId;
        mapping.SourceCode = reference.SourceCode;
        mapping.ExternalRecordId = reference.ExternalRecordId;
        mapping.ReferenceDescription = reference.Description;
        mapping.SourceVersion = reference.SourceVersion;
        mapping.MatchMethod = method;
        mapping.Confidence = confidence;
        mapping.ReviewSourceVersion = null;
        mapping.UnresolvedReason = null;
    }

    private static void SetUnresolved(NutritionMapping mapping, NutritionUnresolvedReason reason)
    {
        mapping.Status = NutritionMappingStatus.Unresolved;
        mapping.UnresolvedReason = reason;
        mapping.ReferenceSourceId = null;
        mapping.SourceCode = null;
        mapping.ExternalRecordId = null;
        mapping.ReferenceDescription = null;
        mapping.SourceVersion = null;
        mapping.ReviewSourceVersion = null;
        mapping.MatchMethod = null;
        mapping.Confidence = null;
        ClearConfirmation(mapping);
        ClearPortion(mapping);
    }

    private static void ClearConfirmation(NutritionMapping mapping)
    {
        mapping.ConfirmedAt = null;
        mapping.ConfirmedByMembershipId = null;
    }

    private static void ClearPortion(NutritionMapping mapping)
    {
        mapping.PortionMeasurementUnitId = null;
        mapping.PortionAmount = null;
        mapping.PortionGrams = null;
        mapping.PortionLabel = null;
        mapping.PortionOrigin = null;
    }

    private static void ApplyPortion(NutritionMapping mapping, ResolvedPortion portion)
    {
        mapping.PortionMeasurementUnitId = portion.MeasurementUnitId;
        mapping.PortionAmount = portion.Amount;
        mapping.PortionGrams = portion.Grams;
        mapping.PortionLabel = portion.Label;
        mapping.PortionOrigin = portion.Origin;
    }

    private void AddRejection(NutritionMapping mapping, string sourceCode, string externalRecordId)
    {
        if (mapping.Rejections.Any(rejection => rejection.SourceCode == sourceCode && rejection.ExternalRecordId == externalRecordId))
        {
            return;
        }

        mapping.Rejections.Add(new NutritionMappingRejection
        {
            Id = Guid.NewGuid(),
            WorkspaceId = mapping.WorkspaceId,
            NutritionMappingId = mapping.Id,
            SourceCode = sourceCode,
            ExternalRecordId = externalRecordId,
            RejectedAt = clock.UtcNow,
            RejectedByMembershipId = workspace.MembershipId,
        });
    }

    private static HashSet<(string, string)> RejectedReferences(NutritionMapping? mapping) =>
        mapping is null ? [] : [.. mapping.Rejections.Select(rejection => (rejection.SourceCode, rejection.ExternalRecordId))];

    /// <summary>
    /// High for the only open hit, or one whose name is the ingredient's own once normalized; Low otherwise. Both
    /// still wait for a creator.
    /// </summary>
    private static NutritionMatchConfidence ConfidenceOf(
        NutritionReferenceCandidateServiceModel candidate, int openCount, string? ingredientName) =>
        openCount == 1
        || (ingredientName is not null
            && NameNormalization.NormalizeName(candidate.Description) == NameNormalization.NormalizeName(ingredientName))
            ? NutritionMatchConfidence.High
            : NutritionMatchConfidence.Low;

    // ---- Portions ----

    private sealed record ResolvedPortion(Guid MeasurementUnitId, decimal Amount, decimal Grams, string? Label, NutritionPortionOrigin Origin);

    /// <summary>
    /// The portion as recorded: the source's, when it is exactly one of the reference's own measured portions,
    /// and otherwise the creator's. Null when the unit is not a usable volume or count unit — a mass needs no
    /// portion, and a temperature or "to taste" cannot have one.
    /// </summary>
    private async Task<ResolvedPortion?> ResolvePortionAsync(
        NutritionPortionViewModel portion, NutritionReferenceServiceModel reference, CancellationToken cancellationToken)
    {
        var dimension = await measurement.FindUsableUnitDimensionAsync(portion.MeasurementUnitId, cancellationToken);
        if (dimension is not (MeasurementDimension.Volume or MeasurementDimension.Count))
        {
            return null;
        }

        // A reference that cannot be read just then only means the portion cannot be shown to be the source's,
        // and the more cautious label is the creator's.
        var measured = reference is { Status: NutritionReferenceStatus.Found, Portions: { } portions }
            ? portions.Where(candidate => candidate.VolumeUnitCode is not null).ToList()
            : [];

        if (measured.Count > 0)
        {
            var units = await measurement.FindUnitsByCodesAsync([.. measured.Select(candidate => candidate.VolumeUnitCode!).Distinct()], cancellationToken);
            var unitIds = units.ToDictionary(unit => unit.Code, unit => unit.Id, StringComparer.Ordinal);

            var match = measured.FirstOrDefault(candidate =>
                unitIds.TryGetValue(candidate.VolumeUnitCode!, out var unitId)
                && unitId == portion.MeasurementUnitId
                && candidate.Amount == portion.Amount
                && candidate.GramWeight == portion.Grams);

            if (match is not null)
            {
                return new ResolvedPortion(portion.MeasurementUnitId, portion.Amount, portion.Grams, match.SourceLabel, NutritionPortionOrigin.Reference);
            }
        }

        return new ResolvedPortion(portion.MeasurementUnitId, portion.Amount, portion.Grams, null, NutritionPortionOrigin.Creator);
    }

    // ---- Saving and answering ----

    private async Task<OperationResult<NutritionMappingServiceModel>> SaveAsync(
        NutritionMapping mapping, bool isNew, AuditEntry? audit, CancellationToken cancellationToken)
    {
        mapping.UpdatedAt = clock.UtcNow;
        mapping.UpdatedByMembershipId = workspace.MembershipId;

        return await dataLayer.SaveAsync(mapping, isNew, audit, cancellationToken) switch
        {
            NutritionMappingWrite.Saved => Success(mapping),
            NutritionMappingWrite.Stale => Failure(Stale()),
            _ => Failure(new OperationError(
                NutritionErrorCodes.MappingNotSaved,
                "That change could not be saved just then. Nothing was written, so you can send it again.",
                Fields())),
        };
    }

    private AuditEntry Audit(NutritionMapping mapping, string action, string summary) =>
        // State names only: no ingredient name, reference description or creator text (AuditPolicy).
        new(
            workspace.AccountId,
            action,
            NutritionAuditActions.ResourceType,
            mapping.Id.ToString("D"),
            CorrelationId(),
            summary,
            AfterReference: mapping.Status.ToString());

    private static Guid CorrelationId()
    {
        // The request's trace id, so an audit row can be found in the trace that made it.
        var traceId = System.Diagnostics.Activity.Current?.TraceId;

        return traceId is { } id && id != default ? Guid.ParseExact(id.ToHexString(), "N") : Guid.NewGuid();
    }

    internal static NutritionMappingServiceModel ToServiceModel(NutritionMapping mapping) =>
        new(
            mapping.Id,
            mapping.IngredientId,
            mapping.Status,
            mapping.MatchMethod,
            mapping.Confidence,
            mapping.Status is NutritionMappingStatus.Unresolved
                ? null
                : new NutritionMappingReferenceServiceModel(
                    mapping.SourceCode!, mapping.ExternalRecordId!, mapping.ReferenceDescription!, mapping.SourceVersion!),
            mapping.ReviewSourceVersion,
            mapping.UnresolvedReason,
            mapping.PortionMeasurementUnitId is { } unitId
                ? new NutritionMappingPortionServiceModel(
                    unitId, mapping.PortionAmount!.Value, mapping.PortionGrams!.Value, mapping.PortionLabel, mapping.PortionOrigin!.Value)
                : null,
            mapping.Rejections.Count,
            mapping.ConfirmedAt,
            mapping.UpdatedAt,
            NutritionMappingConcurrencyToken.From(mapping.RowVersion));

    private static OperationResult<NutritionMappingServiceModel> Success(NutritionMapping mapping) =>
        OperationResult<NutritionMappingServiceModel>.Success(ToServiceModel(mapping));

    private static OperationResult<NutritionMappingServiceModel> Failure(OperationError error) =>
        OperationResult<NutritionMappingServiceModel>.Failure(error);

    private static Dictionary<string, string[]> Fields(params (string Field, string Error)[] errors) =>
        errors.ToDictionary(error => error.Field, error => new[] { error.Error });

    private static OperationError NotFound() =>
        new(NutritionErrorCodes.MappingNotFound, "This workspace has no nutrition mapping for that ingredient.", Fields());

    private static OperationError Stale() =>
        new(NutritionErrorCodes.MappingStale, "That mapping has changed since you read it. Read it again and decide on what it says now.", Fields());

    private static OperationError Conflict(string message) => new(NutritionErrorCodes.MappingConflict, message, Fields());

    private static OperationError IngredientUnprocessable() =>
        new(NutritionErrorCodes.IngredientUnprocessable, "That is not an ingredient the catalogue offers.", Fields(("ingredientId", "Choose an active catalogue ingredient.")));

    private static OperationError PortionUnprocessable() =>
        new(NutritionErrorCodes.PortionUnprocessable, "A portion is measured in a volume or count unit.", Fields(("portion.measurementUnitId", "Choose a volume or count unit.")));

    private static OperationError SourceUnavailable() =>
        new(NutritionErrorCodes.SourceUnavailable, "The nutrition source could not be asked just then. Nothing was changed.", Fields());
}
