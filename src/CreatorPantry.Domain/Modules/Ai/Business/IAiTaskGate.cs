using CreatorPantry.Domain.Modules.Ai.Data;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Domain.Modules.Ai.Business;

/// <summary>
/// Whether one AI task is switched on for the workspace this request resolved.
/// </summary>
internal interface IAiTaskGate
{
    Task<bool> IsEnabledAsync(string discriminator, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAiTaskGate"/>
/// <remarks>
/// <para>
/// <strong>The workspace's own answer wins, in both directions.</strong> An Owner who has switched a task on
/// may use it although the deployment's <c>Ai:Tasks:Enabled</c> does not list it, and one who has switched it
/// off is refused although it does. Only a workspace that has never chosen falls back to that list, which is
/// what the list now is: the default a workspace starts from, not a ceiling over it.
/// </para>
/// <para>
/// What that gives up is the list's old job of keeping a deployed route from spending a provider budget.
/// The account allowance (<see cref="IAiRequestQuotaGate"/>) is the bound on spend now, and it is checked
/// straight after this on every request.
/// </para>
/// <para>
/// The workspace comes from the query filter on the read below, never from an argument: there is nothing
/// here a caller or a model could name a workspace with.
/// </para>
/// </remarks>
internal sealed class AiTaskGate(AiTaskOptions defaults, IAiTaskSwitchDataLayer switches) : IAiTaskGate
{
    public async Task<bool> IsEnabledAsync(string discriminator, CancellationToken cancellationToken)
    {
        // Only a switchable task can have a row, so anything else — the diagnostic task — skips the read and
        // stays the deployment's decision alone.
        if (AiFeaturePolicy.Canonical(discriminator) is { } task
            && await switches.FindEnabledAsync(task, cancellationToken) is { } chosen)
        {
            return chosen;
        }

        return AiTaskCatalog.IsEnabled(defaults, discriminator);
    }
}
