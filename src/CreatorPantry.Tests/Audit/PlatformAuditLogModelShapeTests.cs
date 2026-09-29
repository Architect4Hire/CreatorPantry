using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Tests.Recipes;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CreatorPantry.Tests.Audit;

/// <summary>
/// The shape of <see cref="PlatformAuditLog"/> in the built EF model (USAGE-009).
/// </summary>
/// <remarks>
/// Borrows <c>RecipeAggregateFixture</c> because it builds the whole <c>CreatorPantryDbContext</c> model,
/// which is all these assertions read.
/// </remarks>
public sealed class PlatformAuditLogModelShapeTests : IDisposable
{
    private readonly RecipeAggregateFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    /// <summary>
    /// The decision this table exists for. An action taken outside any workspace has no <c>WorkspaceId</c> to
    /// filter by, and the obvious "fix" — making it workspace-owned like <see cref="AuditLog"/> — would need a
    /// sentinel workspace whose deletion would then cascade the operator's audit trail away.
    /// </summary>
    [Fact]
    public void Is_deliberately_not_workspace_owned_and_carries_no_query_filter()
    {
        Assert.False(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(PlatformAuditLog)));
        Assert.Empty(Logs().GetDeclaredQueryFilters());
        Assert.Null(Logs().FindProperty("WorkspaceId"));
    }

    /// <summary>A correction is a new row, never an edit to this one.</summary>
    [Fact]
    public void Is_write_once()
    {
        Assert.True(typeof(IImmutableRecord).IsAssignableFrom(typeof(PlatformAuditLog)));
    }

    /// <summary>
    /// No relationship to <c>Workspace</c>, <c>ApplicationUser</c> or <c>OpsApiClient</c>. Each would let a
    /// deletion erase the record of what was done — and an audit row has to outlive the credential that acted
    /// and the account it acted on.
    /// </summary>
    [Fact]
    public void Declares_no_foreign_keys_so_no_cascade_can_erase_the_operator_trail()
    {
        Assert.Empty(Logs().GetForeignKeys());

        // Named as well as counted, so adding one of these later has to argue with this test.
        Assert.DoesNotContain(
            Logs().GetForeignKeys(),
            key => key.PrincipalEntityType.ClrType == typeof(Workspace)
                || key.PrincipalEntityType.ClrType == typeof(ApplicationUser)
                || key.PrincipalEntityType.ClrType == typeof(OpsApiClient));
    }

    /// <summary>
    /// <strong>Every text column is one this table can name.</strong> The argument for reading a platform-scoped
    /// table with no workspace filter is that its rows hold identifiers, action codes, an operator's own reason
    /// and compact state pointers — never a recipe title, a prompt body or a proposal. A new free-text column
    /// would quietly retire that argument, so adding one has to change this list first.
    /// </summary>
    [Fact]
    public void Has_no_free_text_column_creator_content_could_land_in()
    {
        var textProperties = Logs().GetProperties()
            .Where(property => property.ClrType == typeof(string))
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            [
                nameof(PlatformAuditLog.Action),
                nameof(PlatformAuditLog.ActorId),
                nameof(PlatformAuditLog.ActorName),
                nameof(PlatformAuditLog.AfterReference),
                nameof(PlatformAuditLog.BeforeReference),
                nameof(PlatformAuditLog.Reason),
                nameof(PlatformAuditLog.SubjectId),
                nameof(PlatformAuditLog.SubjectType),
            ],
            textProperties.Order());

        Assert.All(textProperties, name => Assert.NotNull(Logs().FindProperty(name)!.GetMaxLength()));
    }

    /// <summary>
    /// USAGE-009 asks every quota change, suspension and restoration to name a reason. A nullable column would
    /// make that a convention callers could forget; a required one makes it a fact the database holds.
    /// </summary>
    [Fact]
    public void A_reason_is_required()
    {
        Assert.False(Logs().FindProperty(nameof(PlatformAuditLog.Reason))!.IsNullable);
        Assert.False(Logs().FindProperty(nameof(PlatformAuditLog.ActorId))!.IsNullable);
        Assert.False(Logs().FindProperty(nameof(PlatformAuditLog.SubjectId))!.IsNullable);
    }

    private IEntityType Logs()
    {
        using var scope = _fixture.ScopeFor(RecipeAggregateFixture.WorkspaceA);

        return RecipeAggregateFixture.Db(scope).Model.FindEntityType(typeof(PlatformAuditLog))!;
    }
}
