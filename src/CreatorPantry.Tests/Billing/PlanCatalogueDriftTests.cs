using System.Runtime.CompilerServices;
using CreatorPantry.Domain.Modules.Billing.Managers;
using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Tests.Billing;

/// <summary>
/// The API and the Worker resolve the same plan for the same account (AP.4.2). The Worker opens allowance periods
/// when it admits text work and the API reads them back, so a plan whose credits differed between the two would
/// open a period at one figure and show a creator another.
/// </summary>
/// <remarks>
/// Only what resolution reads is compared: the default, and each plan's key, credits, period, picture caps,
/// quality classes and trial terms. Prices, packs and the page's wording are the API's alone.
/// </remarks>
public sealed class PlanCatalogueDriftTests
{
    private const string Api = "src/CreatorPantry.ApiService/appsettings.Development.json";

    private const string Worker = "src/CreatorPantry.Worker/appsettings.Development.json";

    [Fact]
    public void The_worker_resolves_the_same_development_plans_as_the_api()
    {
        var api = Read(Api);
        var worker = Read(Worker);

        Assert.Equal(api.DefaultPlan, worker.DefaultPlan);
        Assert.Equal(Resolved(api), Resolved(worker));
    }

    /// <summary>
    /// Studio in development (owner, 2026-10-27): nothing moves a local account off its default until AP.4.4, and
    /// the trial would leave a developer five pictures a day and a thousand credits once.
    /// </summary>
    [Fact]
    public void Development_defaults_to_studio()
    {
        Assert.Equal("studio", Read(Api).DefaultPlan);
    }

    private static string[] Resolved(BillingOptions billing) =>
    [
        .. billing.Plans
            .OrderBy(plan => plan.Key, StringComparer.Ordinal)
            .Select(plan => string.Join(
                "|",
                plan.Key,
                plan.Name,
                plan.Credits,
                plan.Period,
                plan.DailyImageCap,
                plan.RunningImages,
                string.Join(",", plan.QualityClasses),
                plan.TrialDays,
                plan.TrialImageCap)),
    ];

    private static BillingOptions Read(string relativePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(RepositoryRoot(CallerPath()), relativePath), optional: false)
            .Build();

        var billing = new BillingOptions();
        configuration.GetSection(BillingOptions.SectionName).Bind(billing);

        Assert.NotEmpty(billing.Plans);

        return billing;
    }

    private static string CallerPath([CallerFilePath] string callerPath = "") => callerPath;

    /// <summary>The repository root, from this file's compile-time location: <c>src/CreatorPantry.Tests/Billing/</c>.</summary>
    private static string RepositoryRoot(string callerPath) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(callerPath)!, "..", "..", ".."));
}
