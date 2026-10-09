using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The promises the creative context makes about its own shape, held by a test rather than by a comment.
/// </summary>
/// <remarks>
/// All are rules about what must <em>not</em> be added, which no ordinary test would notice breaking: a copied
/// recipe title, a step counter or a blob path would each work perfectly and quietly defeat the design. AF.1.2's
/// RESTRICTION is that a context holds references and never copies, carries no progress state, and never becomes
/// a competing source of truth.
/// </remarks>
public sealed class CreativeContextModelShapeTests
{
    private static readonly Type[] Entities =
        [typeof(CreativeContext), typeof(CreativeContextChannel), typeof(CreativeContextReference)];

    private static IEnumerable<(string Entity, string Name, Type Type)> Properties =>
        Entities.SelectMany(entity => entity.GetProperties()
            .Select(property => (entity.Name, property.Name, property.PropertyType)));

    [Fact]
    public void Every_entity_is_workspace_owned_and_none_is_write_once()
    {
        Assert.All(Entities, entity => Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(entity)));

        // A context is edited for as long as the work goes on; references are added and removed.
        Assert.All(Entities, entity => Assert.False(typeof(IImmutableRecord).IsAssignableFrom(entity)));
    }

    [Fact]
    public void A_reference_holds_identifiers_and_nothing_a_copy_could_live_in()
    {
        // No string and no bytes: there is nowhere on this row to put a recipe title, alt text or a prompt body.
        var offenders = typeof(CreativeContextReference).GetProperties()
            .Where(property => property.PropertyType == typeof(string)
                || property.PropertyType == typeof(byte[])
                || property.PropertyType == typeof(Stream))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_only_free_text_on_a_context_is_the_creators_own_words()
    {
        // Adding a string here should mean arguing with this list first: it is either the creator's words about
        // the work, or it is a copy of something another module owns.
        var strings = typeof(CreativeContext).GetProperties()
            .Where(property => property.PropertyType == typeof(string))
            .Select(property => property.Name)
            .Order()
            .ToList();

        // WorkingBrief (AF.3.2) is the brief they chose to work from and may edit. It can begin as a picked
        // idea's wording, which no module owns — a seed is assembled, never stored — so it copies no record.
        Assert.Equal(["PictureBrief", "WeeklyThemeKey", "WorkingBrief", "WorkingTitle"], strings);
    }

    [Fact]
    public void No_property_holds_a_url_or_an_object_path()
    {
        var offenders = Properties
            .Where(property => new[] { "Url", "Uri", "Path", "BlobName", "ObjectKey", "Location", "Href" }
                .Any(banned => property.Name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.Entity}.{property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_property_records_a_step_or_progress()
    {
        // Where a creator has got to belongs to 13A's WorkflowRun, which links here by id.
        var offenders = Properties
            .Where(property => new[] { "Step", "Progress", "Status", "Stage", "Route", "Completed" }
                .Any(banned => property.Name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.Entity}.{property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_property_names_a_provider()
    {
        var offenders = Properties
            .Where(property => new[] { "Provider", "Model", "Deployment", "External" }
                .Any(banned => property.Name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .Select(property => $"{property.Entity}.{property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Only_the_root_carries_the_concurrency_token()
    {
        Assert.Contains(typeof(CreativeContext).GetProperties(), property => property.Name == "RowVersion");
        Assert.DoesNotContain(typeof(CreativeContextChannel).GetProperties(), property => property.Name == "RowVersion");
        Assert.DoesNotContain(typeof(CreativeContextReference).GetProperties(), property => property.Name == "RowVersion");
    }
}
