namespace CreatorPantry.Domain.Managers.Persistence;

/// <summary>
/// A member's authorization level within one workspace. Ordered, not a bitmask: callers compare with
/// <c>&gt;=</c> (e.g. "at least Editor"), never combine values with bitwise operators. Values leave gaps
/// so a role can be inserted between existing ones without renumbering.
/// </summary>
public enum WorkspaceRole
{
    Viewer = 0,
    Contributor = 10,
    Editor = 20,
    Owner = 30,
}
