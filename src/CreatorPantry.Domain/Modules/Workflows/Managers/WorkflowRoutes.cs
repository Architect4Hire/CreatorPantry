namespace CreatorPantry.Domain.Modules.Workflows.Managers;

/// <summary>Turns a capability's route template into the address it opens for one run.</summary>
public static class WorkflowRoutes
{
    /// <summary>
    /// The template with each placeholder filled from the run's link of that kind, or null when the run does not
    /// hold one — an address with a hole in it is not an address.
    /// </summary>
    /// <remarks>
    /// Only placeholders are filled, and only with ids the server linked after checking them; the literal path
    /// and any <c>?area=</c> are the template's own. Nothing the creator typed reaches an address this way.
    /// </remarks>
    public static string? Resolve(string routeTemplate, IReadOnlyDictionary<WorkflowResourceKind, Guid> links)
    {
        ArgumentNullException.ThrowIfNull(routeTemplate);
        ArgumentNullException.ThrowIfNull(links);

        var parts = routeTemplate.Split('?', 2);
        var segments = parts[0].Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length <= 2 || segment[0] != '{' || segment[^1] != '}')
            {
                continue;
            }

            if (!WorkflowResourceKinds.RoutePlaceholders.TryGetValue(segment[1..^1], out var kind)
                || !links.TryGetValue(kind, out var id))
            {
                return null;
            }

            segments[i] = id.ToString("D");
        }

        var path = string.Join('/', segments);

        return parts.Length == 2 ? $"{path}?{parts[1]}" : path;
    }
}
