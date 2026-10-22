using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CreatorPantry.Domain.Managers.Limits;

public static class LimitsServiceCollectionExtensions
{
    /// <summary>
    /// Binds <see cref="LimitsOptions"/> from <c>Limits</c>. Called by the API and the Worker, because the in-progress
    /// caps and daily budgets are checked by both — at the request and again before the work is done — and the two
    /// hosts have to hold the same numbers.
    /// </summary>
    public static IServiceCollection AddLimits(this IServiceCollection services, IConfiguration? configuration)
    {
        var options = services.AddOptions<LimitsOptions>();

        if (configuration is not null)
        {
            options.Bind(configuration.GetSection(LimitsOptions.SectionName));
        }

        // A cap of nought is not "no limit": it is a worker that claims nothing and a queue that never moves,
        // silently. Refused at startup in whichever host binds it.
        options
            .Validate(
                limits => new[]
                {
                    limits.AiInFlightPerMember, limits.AiInFlightPerWorkspace, limits.AiRunningPerWorkspace,
                    limits.ImageInFlightPerWorkspace, limits.ImageRunningPerWorkspace,
                    limits.ImageVariantsPerWorkspacePerDay, limits.PublicationsPerWorkspacePerDay,
                }.All(cap => cap >= 1),
                "Every in-progress cap and daily budget under Limits must be at least one.")
            .ValidateOnStart();

        return services;
    }
}
