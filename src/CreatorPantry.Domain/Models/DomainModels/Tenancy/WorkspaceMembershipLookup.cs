using CreatorPantry.Domain.Tenancy;

namespace CreatorPantry.Domain.Models.DomainModels.Tenancy;

/// <summary>A workspace found by slug, independent of any caller's membership in it.</summary>
public sealed record WorkspaceSummary(Guid Id, string Slug);

/// <summary>One caller's membership in a workspace, independent of whether it is active.</summary>
public sealed record MembershipSummary(Guid Id, WorkspaceRole Role, WorkspaceMembershipStatus Status);

/// <summary>
/// The raw outcome of a slug + membership lookup, before disclosure policy collapses it. <see cref="Workspace"/>
/// null means an unknown slug; <see cref="Membership"/> null means the workspace exists but the caller has
/// no membership row in it.
/// </summary>
public sealed record WorkspaceMembershipLookup(WorkspaceSummary? Workspace, MembershipSummary? Membership);

/// <summary>A workspace's own fields, independent of any caller's membership in it.</summary>
public sealed record WorkspaceRecord(Guid Id, string Name, string Slug, DateTimeOffset CreatedAt);

/// <summary>The result of atomically creating a workspace and its creator's owner membership.</summary>
public sealed record CreatedWorkspace(WorkspaceRecord Workspace, Guid MembershipId, WorkspaceRole Role);

/// <summary>One of the caller's own workspace memberships, joined with that workspace's own fields.</summary>
public sealed record WorkspaceMembershipRow(
    WorkspaceRecord Workspace, Guid MembershipId, WorkspaceRole Role, WorkspaceMembershipStatus Status);
