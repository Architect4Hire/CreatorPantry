using CreatorPantry.Domain.Managers.Limits;
using Microsoft.Extensions.Options;

namespace CreatorPantry.Domain.Modules.Billing.Managers;

/// <summary>
/// The published daily picture cap is the enforced one (DL.6). The plan's cap is a limit and switches off with
/// <c>Limits:Enabled</c> (B-56), so a host that publishes plans with limits off would print a cap on every card that
/// nothing enforces. A published cap of nought would print a plan that makes no pictures, and a cap above the
/// workspace's day is one a creator in one workspace could never reach.
/// </summary>
/// <remarks>
/// A class rather than three <c>Validate</c> lambdas so each failure names the plan and both figures: the person
/// reading a host that will not start should not have to work out which of four plans is wrong.
/// </remarks>
public sealed class PublishedPlanCapsValidation(IOptions<LimitsOptions> limits) : IValidateOptions<BillingOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingOptions billing)
    {
        var failures = new List<string>();
        var limit = limits.Value;

        if (billing.PublishPlans && !limit.Enabled)
        {
            failures.Add(
                "Billing:PublishPlans is on but Limits:Enabled is off; every published plan states a daily picture cap, "
                + "and with limits off nothing enforces it.");
        }

        foreach (var plan in billing.Plans)
        {
            if (billing.PublishPlans && plan.DailyImageCap < 1)
            {
                failures.Add(
                    $"Plan '{plan.Key}' under Billing:Plans is published with a DailyImageCap of {plan.DailyImageCap}; "
                    + "a published plan makes at least one picture a day.");
            }

            if (limit.Enabled && plan.DailyImageCap > limit.ImageVariantsPerWorkspacePerDay)
            {
                failures.Add(
                    $"Plan '{plan.Key}' under Billing:Plans has a DailyImageCap of {plan.DailyImageCap}, above "
                    + $"Limits:ImageVariantsPerWorkspacePerDay of {limit.ImageVariantsPerWorkspacePerDay}; a creator in "
                    + "one workspace could never reach it.");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
