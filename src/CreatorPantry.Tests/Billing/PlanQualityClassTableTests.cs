using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Modules.Billing.Managers;
using CreatorPantry.Domain.Modules.Media.Managers;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The quality classes a plan allows are names of real ones (PRICE-007, AP.4.1). Billing may not name Media's
/// enum, so the catalogue lists classes as strings; this is what keeps a typo from being a class nobody can ask
/// for and every plan quietly lacks.
/// </summary>
/// <remarks>
/// The catalogue spells a class in the landing page's words (<c>high-2k</c>) and the enum in C#'s
/// (<c>High2K</c>); they are matched here ignoring case and hyphens. Which one spelling admission compares is
/// AP.4.3's to settle when it refuses a class by plan.
/// </remarks>
public sealed class PlanQualityClassTableTests
{
    [Fact]
    public void Every_class_a_development_plan_allows_is_a_generated_image_quality_class()
    {
        var known = Enum.GetNames<GeneratedImageQualityClass>().Select(Normalise).ToHashSet();
        var billing = Billing();

        Assert.NotEmpty(billing.Plans);
        Assert.All(billing.Plans, plan =>
        {
            Assert.NotEmpty(plan.QualityClasses);
            Assert.All(plan.QualityClasses, quality =>
                Assert.True(known.Contains(Normalise(quality)), $"{plan.Key} allows '{quality}', which is not a quality class."));
        });
    }

    /// <summary>The development catalogue resolves: its default is one of its plans, and the trial is granted once.</summary>
    [Fact]
    public void The_development_default_is_a_plan_and_only_the_trial_is_granted_once()
    {
        var billing = Billing();

        Assert.Contains(billing.Plans, plan => plan.Key == billing.DefaultPlan);
        Assert.All(billing.Plans, plan =>
            Assert.Equal(plan.TrialDays is null ? PlanAllowancePeriod.Monthly : PlanAllowancePeriod.Once, plan.Period));
    }

    private static string Normalise(string name) => name.Replace("-", string.Empty).ToUpperInvariant();

    private static BillingOptions Billing()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(
                Path.Combine(RepositoryRoot(CallerPath()), "src/CreatorPantry.ApiService/appsettings.Development.json"),
                optional: false)
            .Build();

        var billing = new BillingOptions();
        configuration.GetSection(BillingOptions.SectionName).Bind(billing);
        return billing;
    }

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Billing/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
