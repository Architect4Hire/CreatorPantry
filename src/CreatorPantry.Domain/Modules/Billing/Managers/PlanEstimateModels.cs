namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// What a visitor's month on the landing page comes to, and which plan carries it (LP.7, AP.7.2).
/// </summary>
/// <param name="Credits">
/// The month in credits: the pictures at their quality's charged rate, plus the writing at its typical rate. An
/// estimate, as the menu it is priced from is — writing settles at what the model reports.
/// </param>
/// <param name="QualityClass">
/// The picture quality the pictures were priced at — the one asked for when the catalogue publishes it, else the
/// default, else the cheapest published; null when no picture is published, and the month is writing alone.
/// </param>
/// <param name="FitsPlanKey">The smallest paid plan that carries the month; null when none does.</param>
/// <param name="FitsPlanName">That plan's name.</param>
/// <param name="CreditsToSpare">What that plan's credits leave over the estimate; null when no plan fits.</param>
/// <param name="LargestPlanName">The largest paid plan, for "that is more than … includes".</param>
/// <remarks>
/// <strong>Every figure is worked out here, never in the browser</strong> (B-55, owner 2026-10-29): the page formats
/// these and adds nothing, so the estimate and the plan it names are the catalogue's arithmetic, done once.
/// </remarks>
public sealed record PlanEstimateServiceModel(
    decimal Credits,
    string? QualityClass,
    string? FitsPlanKey,
    string? FitsPlanName,
    decimal? CreditsToSpare,
    string? LargestPlanName);

/// <summary>Stable error codes for the plan estimate route (AP.7.2).</summary>
public static class PlanEstimateErrors
{
    /// <summary>A count is negative or past what a month could hold. Maps to 400.</summary>
    public const string Invalid = "billing.estimate.invalid";

    /// <summary>
    /// This deployment publishes no catalogue, so there is nothing to estimate against. Maps to 404: the landing
    /// page renders no estimator then, and never asks.
    /// </summary>
    public const string NotFound = "billing.estimate.not_found";
}
