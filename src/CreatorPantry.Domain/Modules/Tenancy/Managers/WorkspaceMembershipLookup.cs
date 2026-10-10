using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Measurement.Managers;

namespace CreatorPantry.Domain.Modules.Tenancy.Managers;

/// <summary>A workspace found by slug, independent of any caller's membership in it.</summary>
public sealed record WorkspaceSummary(Guid Id, string Slug);

/// <summary>One caller's membership in a workspace, independent of whether it is active.</summary>
/// <param name="UserId">
/// The Identity account behind this membership. Carried so that resolving a workspace also resolves who the
/// caller is platform-wide, which per-account AI usage accounting needs (USAGE-001) and a workspace-scoped
/// <paramref name="Id"/> cannot answer. It costs nothing: the row it comes from is already being read.
/// </param>
public sealed record MembershipSummary(
    Guid Id, WorkspaceRole Role, WorkspaceMembershipStatus Status, string UserId);

/// <summary>
/// The raw outcome of a slug + membership lookup, before disclosure policy collapses it. <see cref="Workspace"/>
/// null means an unknown slug; <see cref="Membership"/> null means the workspace exists but the caller has
/// no membership row in it.
/// </summary>
public sealed record WorkspaceMembershipLookup(WorkspaceSummary? Workspace, MembershipSummary? Membership);

/// <summary>A workspace's own fields, independent of any caller's membership in it.</summary>
public sealed record WorkspaceRecord(
    Guid Id,
    string Name,
    string Slug,
    DateTimeOffset CreatedAt,
    MeasurementSystem DefaultMeasurementSystem = WorkspacePolicy.InitialMeasurementSystem);

/// <summary>The result of atomically creating a workspace and its creator's owner membership.</summary>
public sealed record CreatedWorkspace(WorkspaceRecord Workspace, Guid MembershipId, WorkspaceRole Role);

/// <summary>One of the caller's own workspace memberships, joined with that workspace's own fields.</summary>
public sealed record WorkspaceMembershipRow(
    WorkspaceRecord Workspace, Guid MembershipId, WorkspaceRole Role, WorkspaceMembershipStatus Status);
