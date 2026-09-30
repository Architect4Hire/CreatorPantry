using CreatorPantry.Domain.Modules.Content.Managers;

namespace CreatorPantry.Tests.Content;

/// <summary>What an export reads back from a stored editorial package.</summary>
public sealed class EditorialPackageDocumentReaderTests
{
    private static string Package(string storage) =>
        "{\"schemaVersion\":\"content.editorial-package.v1\",\"sections\":{\"storageReheating\":{\"text\":\"" + storage + "\"}}}";

    [Theory]
    [InlineData("no storage guidance supplied")]
    [InlineData("No storage guidance supplied.")]
    [InlineData("  NO STORAGE GUIDANCE SUPPLIED!  ")]
    public void The_no_storage_marker_is_read_as_no_section_so_an_export_never_prints_it(string marker)
    {
        var read = EditorialPackageDocumentReader.Read(1, Package(marker), isCurrent: true);

        Assert.Null(read.StorageReheating);
    }

    [Fact]
    public void Real_storage_guidance_is_kept_verbatim()
    {
        var read = EditorialPackageDocumentReader.Read(1, Package("Keeps two days in a tin."), isCurrent: true);

        Assert.Equal("Keeps two days in a tin.", read.StorageReheating);
    }

    [Fact]
    public void Guidance_that_merely_contains_the_marker_is_kept_because_it_is_not_the_marker()
    {
        var read = EditorialPackageDocumentReader.Read(
            1, Package("Keeps 5 days. No storage guidance supplied."), isCurrent: true);

        Assert.Equal("Keeps 5 days. No storage guidance supplied.", read.StorageReheating);
    }
}
