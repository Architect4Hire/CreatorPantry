using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// What a <see cref="BrandContextPackage"/> may carry — asserted as a property list, because 11A.19's safety
/// restriction is a statement about the shape rather than about anybody's discipline.
/// </summary>
/// <remarks>
/// <para>
/// <strong>"No profile field switches a platform safety rule, warning or check on or off" is enforced by there
/// being nowhere to put one.</strong> A flag, a threshold or a provider setting anywhere in this type is how
/// that guarantee would be lost, and it would be lost quietly: the field would simply start being read. These
/// tests are what make adding one argue with a test first, the same technique
/// <c>RequestAiProposalViewModelTests</c> uses on the request contract.
/// </para>
/// <para>
/// Reflection over the property list rather than a review convention, for the reason that file records: a
/// widening nobody notices is the failure mode worth catching mechanically.
/// </para>
/// </remarks>
public sealed class BrandContextPackageShapeTests
{
    [Fact]
    public void The_package_carries_exactly_the_declared_fields()
    {
        var fields = typeof(BrandContextPackage).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "AssembledAt",
                "Audience",
                "AudienceOrigin",
                "ChannelKey",
                "Checksum",
                "Conflicts",
                "EstimatedTokens",
                "Excerpts",
                "Guidance",
                "GuideId",
                "GuideIsActiveVersion",
                "GuideVersionId",
                "GuideVersionNumber",
                "Omissions",
                "Profile",
                "Rules",
                "TaskType",
            ],
            fields);
    }

    /// <summary>
    /// The one boolean is the server's own statement about which version it used. Nothing a creator writes
    /// feeds it, and nothing else in the package is a flag at all.
    /// </summary>
    [Fact]
    public void The_package_has_no_flag_a_creator_field_could_set()
    {
        var booleans = typeof(BrandContextPackage).GetProperties()
            .Where(property => property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?))
            .Select(property => property.Name)
            .ToList();

        Assert.Equal([nameof(BrandContextPackage.GuideIsActiveVersion)], booleans);
    }

    [Theory]
    [InlineData(typeof(BrandContextProfile))]
    [InlineData(typeof(BrandContextGuidance))]
    [InlineData(typeof(BrandContextRule))]
    [InlineData(typeof(BrandContextExcerpt))]
    public void Nothing_the_package_holds_is_a_flag(Type type)
    {
        Assert.DoesNotContain(
            type.GetProperties(),
            property => property.PropertyType == typeof(bool) || property.PropertyType == typeof(bool?));
    }

    /// <summary>
    /// Names that would signal a value the platform acts on rather than material it reads. A field called
    /// <c>allowUnsafe</c> or <c>safetyThreshold</c> is the thing this restriction is about, and so is a provider
    /// or model setting arriving by the back door.
    /// </summary>
    [Theory]
    [InlineData("safety")]
    [InlineData("allow")]
    [InlineData("disable")]
    [InlineData("enable")]
    [InlineData("override")]
    [InlineData("bypass")]
    [InlineData("skip")]
    [InlineData("threshold")]
    [InlineData("policy")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("temperature")]
    [InlineData("prompt")]
    [InlineData("workspace")]
    public void The_package_cannot_name_anything_the_platform_acts_on(string forbidden)
    {
        foreach (var type in (Type[])
            [
                typeof(BrandContextPackage),
                typeof(BrandContextProfile),
                typeof(BrandContextGuidance),
                typeof(BrandContextRule),
                typeof(BrandContextExcerpt),
            ])
        {
            Assert.DoesNotContain(
                type.GetProperties(),
                property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The request is the other half: a caller names what to write for, never where to read it from or how.
    /// </summary>
    [Fact]
    public void The_request_carries_exactly_the_declared_fields()
    {
        var fields = typeof(BrandContextRequest).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Audience", "ChannelKey", "Guide", "SourceDocumentIds", "TaskType"], fields);
    }

    /// <summary>
    /// <strong>No workspace, no object key, no passage id and no prompt.</strong> The workspace comes from the
    /// resolved context; documents are named by id and their versions pinned server-side; passages are found,
    /// never supplied.
    /// </summary>
    [Theory]
    [InlineData("workspace")]
    [InlineData("objectKey")]
    [InlineData("blob")]
    [InlineData("passage")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("version")]
    public void The_request_cannot_name_anything_the_server_decides(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(BrandContextRequest).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A guide is selected by number within a guide, never by version id: the number is what a creator sees, and
    /// resolving it server-side is what stops a caller naming a row by an id it should not have been able to
    /// guess.
    /// </summary>
    [Fact]
    public void A_guide_selection_names_a_guide_and_a_version_number()
    {
        var fields = typeof(BrandGuideSelection).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GuideId", "VersionNumber"], fields);
    }
}
