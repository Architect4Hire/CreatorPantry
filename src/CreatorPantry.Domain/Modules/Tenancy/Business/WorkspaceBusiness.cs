using CreatorPantry.Domain.Managers.Persistence;
using System.Text.RegularExpressions;
using CreatorPantry.Domain.Modules.Tenancy.Data;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using CreatorPantry.Domain.Managers.Time;

namespace CreatorPantry.Domain.Modules.Tenancy.Business;

internal sealed partial class WorkspaceBusiness(IWorkspaceDataLayer dataLayer, IClock clock, IWorkspaceContext workspaceContext) : IWorkspaceBusiness
{
    /// <summary>How many "name", "name-2", "name-3", ... candidates to try before giving up.</summary>
    private const int MaxSlugAttempts = 25;

    public async Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(
        string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var lookup = await dataLayer.FindBySlugAsync(model.WorkspaceSlug.Trim(), userId, cancellationToken);

        // Unknown workspace, no membership, and an inactive (invited or removed) membership all return the
        // same result, so none of them discloses whether the slug exists or who belongs to it.
        if (lookup is not { Workspace: { } workspace, Membership: { Status: WorkspaceMembershipStatus.Active } membership })
        {
            return NotFound();
        }

        return OperationResult<ResolvedWorkspaceServiceModel>.Success(
            new ResolvedWorkspaceServiceModel(workspace.Id, workspace.Slug, membership.Id, membership.Role));
    }

    public async Task<IReadOnlyList<MyWorkspaceMembershipServiceModel>> GetMyMembershipsAsync(
        string userId, CancellationToken cancellationToken)
    {
        var rows = await dataLayer.FindMembershipsForUserAsync(userId, cancellationToken);
        return rows
            .Select(row => new MyWorkspaceMembershipServiceModel(
                row.Workspace.Id, row.Workspace.Slug, row.Workspace.Name, row.MembershipId, row.Role, row.Status))
            .ToList();
    }

    public async Task<OperationResult<WorkspaceServiceModel>> CreateAsync(
        string userId, CreateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var name = model.Name.Trim();
        var slug = await AllocateSlugAsync(name, cancellationToken);
        if (slug is null)
        {
            return OperationResult<WorkspaceServiceModel>.Failure(new OperationError(
                TenancyErrorCodes.WorkspaceSlugUnavailable,
                "Could not allocate a workspace address for that name. Try a different name.",
                new Dictionary<string, string[]>()));
        }

        var created = await dataLayer.CreateWithOwnerAsync(name, slug, userId, clock.UtcNow, cancellationToken);
        return OperationResult<WorkspaceServiceModel>.Success(new WorkspaceServiceModel(
            created.Workspace.Id, created.Workspace.Name, created.Workspace.Slug, created.Workspace.CreatedAt,
            created.MembershipId, created.Role));
    }

    public async Task<WorkspaceServiceModel> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var workspace = await dataLayer.FindByIdAsync(workspaceContext.WorkspaceId, cancellationToken)
            ?? throw new InvalidOperationException("The resolved workspace no longer exists.");

        return new WorkspaceServiceModel(
            workspace.Id, workspace.Name, workspace.Slug, workspace.CreatedAt, workspaceContext.MembershipId, workspaceContext.Role);
    }

    public async Task<WorkspaceServiceModel> RenameCurrentAsync(UpdateWorkspaceViewModel model, CancellationToken cancellationToken)
    {
        var workspace = await dataLayer.RenameAsync(workspaceContext.WorkspaceId, model.Name.Trim(), cancellationToken);
        return new WorkspaceServiceModel(
            workspace.Id, workspace.Name, workspace.Slug, workspace.CreatedAt, workspaceContext.MembershipId, workspaceContext.Role);
    }

    /// <summary>Tries the base slug, then <c>base-2</c>, <c>base-3</c>, ... until one is free or attempts run out.</summary>
    private async Task<string?> AllocateSlugAsync(string name, CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(name);
        for (var attempt = 0; attempt < MaxSlugAttempts; attempt++)
        {
            var candidate = SlugCandidate(baseSlug, attempt);
            if (!await dataLayer.SlugExistsAsync(candidate, cancellationToken))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Slugify(string name)
    {
        // Apostrophes join rather than separate ("Sam's Kitchen" -> "sams-kitchen", not "sam-s-kitchen");
        // every other run of non-slug characters becomes one hyphen.
        var withoutApostrophes = name.Trim().ToLowerInvariant().Replace("'", string.Empty).Replace("’", string.Empty);
        var slug = NonSlugCharacters().Replace(withoutApostrophes, "-").Trim('-');
        return Truncate(slug.Length == 0 ? "workspace" : slug);
    }

    private static string SlugCandidate(string baseSlug, int attempt) =>
        attempt == 0 ? baseSlug : Truncate($"{baseSlug}-{attempt + 1}");

    private static string Truncate(string slug) =>
        slug.Length > WorkspacePolicy.SlugMaxLength ? slug[..WorkspacePolicy.SlugMaxLength].Trim('-') : slug;

    private static OperationResult<ResolvedWorkspaceServiceModel> NotFound() =>
        OperationResult<ResolvedWorkspaceServiceModel>.Failure(new OperationError(
            TenancyErrorCodes.WorkspaceNotFound, "Workspace not found.", new Dictionary<string, string[]>()));

    /// <summary>Any run of characters outside the slug alphabet becomes one hyphen.</summary>
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
