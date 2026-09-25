using Microsoft.Extensions.Configuration;

namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>Which task discriminators a client may name, and which are switched on.</summary>
public sealed class AiTaskOptions
{
    public const string SectionName = "Ai:Tasks";

    /// <summary>
    /// The discriminators this deployment will run. Empty by default, on purpose.
    /// </summary>
    /// <remarks>
    /// The endpoint ships dark. Every request is refused until a deployment opts a task in, so no environment
    /// can start spending a provider budget because a route happened to be deployed. Turning one on is a
    /// configuration change somebody makes deliberately.
    /// </remarks>
    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The allow-list a client's task discriminator is resolved through.
/// </summary>
/// <remarks>
/// <para>
/// The client names a task and nothing else. It cannot supply a prompt, a template id, a model, a provider
/// parameter or a tool list, because none of those is a field on the request and none of them is derived from
/// one — the server maps a discriminator it recognises to a task it already knows how to run, or refuses.
/// </para>
/// <para>
/// Two gates, deliberately separate. A discriminator that is not in <see cref="Known"/> is not a task at all
/// and is a bad request; one that is known but not enabled is a real task this deployment has not switched on,
/// which is a different answer and deserves a different code.
/// </para>
/// </remarks>
public static class AiTaskCatalog
{
    /// <summary>The discriminator for the inert task that exercises the lifecycle without calling a model.</summary>
    public const string Diagnostic = "diagnostic";

    private static readonly Dictionary<string, AiTaskType> KnownTasks = new(StringComparer.OrdinalIgnoreCase)
    {
        [Diagnostic] = AiTaskType.Diagnostic,
    };

    /// <summary>Every discriminator the server recognises, enabled or not.</summary>
    public static IReadOnlyCollection<string> Known => KnownTasks.Keys;

    /// <summary>The task a discriminator names, or null when the server does not recognise it.</summary>
    public static AiTaskType? Resolve(string? discriminator) =>
        discriminator is not null && KnownTasks.TryGetValue(discriminator, out var task) ? task : null;

    public static bool IsEnabled(AiTaskOptions options, string discriminator) =>
        options.Enabled.Contains(discriminator);

    public static AiTaskOptions Bind(IConfiguration configuration)
    {
        var options = new AiTaskOptions();

        foreach (var enabled in configuration.GetSection($"{AiTaskOptions.SectionName}:Enabled").Get<string[]>() ?? [])
        {
            options.Enabled.Add(enabled);
        }

        return options;
    }
}
