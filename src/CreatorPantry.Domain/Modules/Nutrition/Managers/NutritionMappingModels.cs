using FluentValidation;

namespace CreatorPantry.Domain.Modules.Nutrition.Managers;

/// <summary>
/// The one portion that turns a volume or count quantity into grams for this ingredient: so many of a unit
/// weigh so much. Matching one of the reference's own portions exactly records it as the source's; anything
/// else is the creator's own figure.
/// </summary>
public sealed class NutritionPortionViewModel
{
    public Guid MeasurementUnitId { get; set; }

    public decimal Amount { get; set; }

    public decimal Grams { get; set; }
}

/// <summary>Confirms the reference a mapping holds — a proposal, or a newer version under review.</summary>
public sealed class ConfirmNutritionMappingViewModel
{
    public string? ExpectedConcurrencyToken { get; set; }

    /// <summary>Replaces the mapping's portion when given; leaves it as it is when not.</summary>
    public NutritionPortionViewModel? Portion { get; set; }
}

/// <summary>Points a mapping at a reference the creator chose, overriding whatever was proposed.</summary>
public sealed class RemapNutritionMappingViewModel
{
    /// <summary>Required when the workspace already has a mapping for the ingredient; ignored when it has none.</summary>
    public string? ExpectedConcurrencyToken { get; set; }

    public string SourceCode { get; set; } = string.Empty;

    public string ExternalRecordId { get; set; } = string.Empty;

    public NutritionPortionViewModel? Portion { get; set; }
}

/// <summary>A change that carries nothing but the version of the mapping the caller read: reject, or mark unresolved.</summary>
public sealed class NutritionMappingDecisionViewModel
{
    /// <summary>Required when the workspace already has a mapping for the ingredient.</summary>
    public string? ExpectedConcurrencyToken { get; set; }
}

internal sealed class NutritionPortionViewModelValidator : AbstractValidator<NutritionPortionViewModel>
{
    public NutritionPortionViewModelValidator()
    {
        RuleFor(portion => portion.MeasurementUnitId).NotEmpty();
        RuleFor(portion => portion.Amount)
            .GreaterThan(0m)
            .LessThanOrEqualTo(NutritionMappingPolicy.PortionAmountMax)
            .PrecisionScale(NutritionMappingPolicy.PortionPrecision, NutritionMappingPolicy.PortionScale, ignoreTrailingZeros: true);
        RuleFor(portion => portion.Grams)
            .GreaterThan(0m)
            .LessThanOrEqualTo(NutritionMappingPolicy.PortionGramsMax)
            .PrecisionScale(NutritionMappingPolicy.PortionPrecision, NutritionMappingPolicy.PortionScale, ignoreTrailingZeros: true);
    }
}

internal sealed class ConfirmNutritionMappingViewModelValidator : AbstractValidator<ConfirmNutritionMappingViewModel>
{
    public ConfirmNutritionMappingViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(NutritionMappingConcurrencyToken.IsWellFormed)
            .WithMessage("Send the concurrency token of the mapping you read.");
        RuleFor(model => model.Portion!).SetValidator(new NutritionPortionViewModelValidator()).When(model => model.Portion is not null);
    }
}

internal sealed class RemapNutritionMappingViewModelValidator : AbstractValidator<RemapNutritionMappingViewModel>
{
    public RemapNutritionMappingViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(NutritionMappingConcurrencyToken.IsWellFormed)
            .When(model => model.ExpectedConcurrencyToken is not null)
            .WithMessage("That is not a concurrency token this server issued.");
        RuleFor(model => model.SourceCode).NotEmpty().MaximumLength(NutritionMappingPolicy.SourceCodeMaxLength);
        RuleFor(model => model.ExternalRecordId).NotEmpty().MaximumLength(NutritionMappingPolicy.ExternalRecordIdMaxLength);
        RuleFor(model => model.Portion!).SetValidator(new NutritionPortionViewModelValidator()).When(model => model.Portion is not null);
    }
}

internal sealed class NutritionMappingDecisionViewModelValidator : AbstractValidator<NutritionMappingDecisionViewModel>
{
    public NutritionMappingDecisionViewModelValidator()
    {
        RuleFor(model => model.ExpectedConcurrencyToken)
            .Must(NutritionMappingConcurrencyToken.IsWellFormed)
            .When(model => model.ExpectedConcurrencyToken is not null)
            .WithMessage("That is not a concurrency token this server issued.");
    }
}

/// <summary>The reference a mapping holds, as the source named it.</summary>
public sealed record NutritionMappingReferenceServiceModel(
    string SourceCode,
    string ExternalRecordId,
    string Description,
    string SourceVersion);

public sealed record NutritionMappingPortionServiceModel(
    Guid MeasurementUnitId,
    decimal Amount,
    decimal Grams,
    string? Label,
    NutritionPortionOrigin Origin);

/// <summary>One workspace's decision about one platform ingredient's nutrition reference (14.2).</summary>
/// <param name="Reference">Null exactly when the status is Unresolved.</param>
/// <param name="ReviewSourceVersion">The newer version a creator is asked to review, while the status is NeedsReview.</param>
/// <param name="RejectedReferenceCount">How many references a creator has turned down; none is proposed again.</param>
/// <param name="ConcurrencyToken">Quoted back on the next change.</param>
public sealed record NutritionMappingServiceModel(
    Guid Id,
    Guid IngredientId,
    NutritionMappingStatus Status,
    NutritionMatchMethod? MatchMethod,
    NutritionMatchConfidence? Confidence,
    NutritionMappingReferenceServiceModel? Reference,
    string? ReviewSourceVersion,
    NutritionUnresolvedReason? UnresolvedReason,
    NutritionMappingPortionServiceModel? Portion,
    int RejectedReferenceCount,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken);

/// <summary>A mapping's row version as the opaque token a caller quotes back. The encoding every module uses.</summary>
public static class NutritionMappingConcurrencyToken
{
    /// <summary>A SQL Server <c>rowversion</c> is eight bytes.</summary>
    public const int ByteLength = 8;

    public static string From(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static bool IsWellFormed(string? token) => TryParse(token, out _);

    public static bool Matches(string? token, byte[] rowVersion) =>
        TryParse(token, out var submitted) && submitted.AsSpan().SequenceEqual(rowVersion);

    private static bool TryParse(string? token, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[ByteLength];
        if (!Convert.TryFromBase64String(token, buffer, out var written) || written != ByteLength)
        {
            return false;
        }

        bytes = buffer.ToArray();
        return true;
    }
}
