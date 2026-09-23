using CreatorPantry.Domain.Managers.Persistence;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>A workspace plus the caller's own membership in it.</summary>
public sealed record WorkspaceServiceModel(
    Guid WorkspaceId, string Name, string Slug, DateTimeOffset CreatedAt, Guid MembershipId, WorkspaceRole Role);
