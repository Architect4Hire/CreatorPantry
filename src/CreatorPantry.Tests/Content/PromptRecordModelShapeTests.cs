using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Content.Data.Entities;

namespace CreatorPantry.Tests.Content;

/// <summary>
/// The two promises <c>PromptRecord</c> makes about its own shape, held by a test rather than by a comment.
/// </summary>
/// <remarks>
/// Both are rules about what must <em>not</em> be added, which no ordinary test would ever notice breaking: a URL
/// column or a byte column would work perfectly and quietly defeat the design. 12.3's RESTRICTION is explicit that
/// a blob URL is not an authority and that stable object identity is what gets stored.
/// </remarks>
public sealed class PromptRecordModelShapeTests
{
    private static IReadOnlyList<string> PropertyNames =>
        [.. typeof(PromptRecord).GetProperties().Select(property => property.Name)];

    [Fact]
    public void No_property_holds_a_url_or_an_object_path()
    {
        // Asset lineage is an id, because a blob URL is a rendering of where bytes happen to live today. If a later
        // prompt needs to resolve an id to a location, that is a gateway's job at read time, not a column here.
        var offenders = PropertyNames
            .Where(name => new[] { "Url", "Uri", "Path", "BlobName", "ObjectKey", "Location", "Href" }
                .Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_property_holds_bytes()
    {
        // A prompt is text. Bytes live in object storage and their metadata lives on the asset rows (media.md).
        var offenders = typeof(PromptRecord).GetProperties()
            .Where(property => property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(Stream))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_record_is_workspace_owned_and_write_once()
    {
        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(typeof(PromptRecord)));
        Assert.True(typeof(IImmutableRecord).IsAssignableFrom(typeof(PromptRecord)));
    }

    [Fact]
    public void There_is_no_updated_timestamp_because_there_is_no_update()
    {
        // A write-once row with an UpdatedAt column would be telling two different stories about itself.
        Assert.Contains("CreatedAt", PropertyNames);
        Assert.DoesNotContain("UpdatedAt", PropertyNames);
        Assert.DoesNotContain("RowVersion", PropertyNames);
    }

    [Fact]
    public void The_creators_words_and_the_models_draft_are_both_kept()
    {
        // Two columns rather than a "was edited" flag: a flag records that an edit happened and loses what it was.
        Assert.Contains("Text", PropertyNames);
        Assert.Contains("GeneratedText", PropertyNames);
        Assert.DoesNotContain("WasEdited", PropertyNames);
        Assert.DoesNotContain("IsEdited", PropertyNames);
    }

    [Fact]
    public void The_template_pin_is_the_full_triple()
    {
        // A version string alone is a claim: a template body can change under a version.
        Assert.Contains("PromptTemplateId", PropertyNames);
        Assert.Contains("PromptTemplateVersion", PropertyNames);
        Assert.Contains("PromptTemplateBodyChecksum", PropertyNames);
    }
}
