namespace CreatorPantry.Domain.Modules.Ai.Managers;

/// <summary>
/// What an audit entry from this module calls the thing an action happened to.
/// </summary>
/// <remarks>
/// <para>
/// Entity names rather than route segments, so an entry stays readable after a route is versioned or renamed —
/// the same choice <c>RecipeAuditActions.ResourceType</c> made, for the same reason.
/// </para>
/// <para>
/// The action codes themselves are not here: they come from
/// <see cref="AiOperationTransitionPolicy.AuditAction"/>, which is the only thing that decides how an operation
/// moves. A second list of names for the same transitions would be a second chance to disagree with it.
/// </para>
/// </remarks>
public static class AiAuditResources
{
    /// <summary>
    /// A proposal a creator decided about. The proposal rather than the operation, because the proposal is what
    /// they read, and it is what the resulting recipe version names.
    /// </summary>
    public const string Proposal = "AiProposal";
}
