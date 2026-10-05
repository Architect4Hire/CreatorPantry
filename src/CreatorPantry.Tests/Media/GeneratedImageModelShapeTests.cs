using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Modules.Media.Data.Entities;
using CreatorPantry.Domain.Modules.Media.Managers;

namespace CreatorPantry.Tests.Media;

/// <summary>
/// What the generated-image tables promise about their own shape, held by a test rather than by a comment.
/// </summary>
/// <remarks>
/// These are rules about what must <em>not</em> be added, which no ordinary test would notice breaking: a URL
/// column, a byte column or a prompt-record pin would each work perfectly and quietly defeat the design. 12.6's
/// RESTRICTION is explicit — the database stores metadata, the bytes stay in private staging storage, there is no
/// public permanent URL, and a rejected image is not a DAM asset.
/// </remarks>
public sealed class GeneratedImageModelShapeTests
{
    private static readonly string[] AddressLikeNames =
        ["Url", "Uri", "Href", "Link", "Address", "Cdn", "Sas"];

    private static readonly string[] CredentialLikeNames =
        ["Secret", "Password", "Token", "Credential", "ConnectionString", "AccessKey", "Signature"];

    private static IReadOnlyList<string> NamesOf(Type entity) =>
        [.. entity.GetProperties().Select(property => property.Name)];

    private static IReadOnlyList<string> ImageNames => NamesOf(typeof(GeneratedImage));

    private static IReadOnlyList<string> OperationNames => NamesOf(typeof(GeneratedImageOperation));

    [Theory]
    [InlineData(typeof(GeneratedImage))]
    [InlineData(typeof(GeneratedImageOperation))]
    public void No_property_is_an_address(Type entity)
    {
        // Staging identity is an opaque key, not a location: a URL stored on a row outlives the access window it
        // was minted in, and a row holding one invites a caller to hand it to a browser. Reaching the bytes is an
        // authorized read through a facade (12.8), where the address is built and where it expires.
        var offenders = NamesOf(entity)
            .Where(name => AddressLikeNames.Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(GeneratedImage))]
    [InlineData(typeof(GeneratedImageOperation))]
    public void No_property_holds_bytes(Type entity)
    {
        // media.md: bytes live in object storage and only their metadata lives in SQL. A blob column would also
        // put generated images inside every backup of the metadata database, which is not where they belong.
        var offenders = entity.GetProperties()
            .Where(property => property.PropertyType == typeof(byte[]) || property.PropertyType == typeof(Stream))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(GeneratedImage))]
    [InlineData(typeof(GeneratedImageOperation))]
    public void No_property_holds_a_credential(Type entity)
    {
        // external.md keeps provider credentials out of rows entirely. Named here as well as reviewed, because
        // "store the key that minted this" is a plausible-sounding addition to a table about provider output.
        var offenders = NamesOf(entity)
            .Where(name => CredentialLikeNames.Any(banned => name.Contains(banned, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData(typeof(GeneratedImage))]
    [InlineData(typeof(GeneratedImageOperation))]
    public void Both_tables_are_workspace_owned_and_neither_is_write_once(Type entity)
    {
        Assert.True(typeof(IWorkspaceOwned).IsAssignableFrom(entity));

        // Unlike PromptRecord: a status moves here, which is the point of the rows. An IImmutableRecord marker
        // would make the first status change throw, so the interceptor is deliberately not involved.
        Assert.False(typeof(IImmutableRecord).IsAssignableFrom(entity));
    }

    [Fact]
    public void The_staged_image_names_its_object_and_its_bytes_but_not_their_location()
    {
        // The identity side of the same rule: an opaque key, the type established from the bytes, the size, the
        // dimensions and a checksum — all that is needed to serve or verify the object without naming where it is.
        Assert.Contains("ObjectKey", ImageNames);
        Assert.Contains("MediaType", ImageNames);
        Assert.Contains("ContentChecksum", ImageNames);
        Assert.Contains("SizeBytes", ImageNames);
        Assert.Contains("Width", ImageNames);
        Assert.Contains("Height", ImageNames);
    }

    [Fact]
    public void The_staged_image_carries_a_retention_deadline()
    {
        // Stored rather than computed, so 12.8's sweep is a keyed read on a filtered index rather than a scan.
        Assert.Contains("RetentionExpiresAt", ImageNames);
    }

    [Fact]
    public void A_staged_image_is_not_a_dam_asset_and_holds_no_link_to_one()
    {
        // IMG-006 and this prompt's RESTRICTION. 12.9 makes an asset from a kept image and the link belongs
        // there; a nullable id here would be a column nothing could write or verify until then — exactly what
        // PromptRecord.GeneratedImageId was until this prompt, and the reason it stayed unwritable.
        Assert.DoesNotContain("DamAssetId", ImageNames);
        Assert.DoesNotContain("MediaAssetId", ImageNames);
    }

    [Fact]
    public void The_operation_names_no_prompt_record()
    {
        // Not an omission. A prompt record is write-once, so its GeneratedImageId can only be set at insert,
        // which means the prompt row is written after an image is committed. A foreign key here would close a
        // cycle — prompt to image to operation to prompt — that could never be fully populated in either
        // direction. The prompt as sent is stored as text instead, and AiProposalId is where it came from.
        Assert.DoesNotContain("PromptRecordId", OperationNames);
        Assert.Contains("PromptText", OperationNames);
        Assert.Contains("AiProposalId", OperationNames);
    }

    [Fact]
    public void The_operation_keeps_a_sanitized_failure_summary_and_not_a_provider_body()
    {
        // An image provider's error can quote the prompt back, and a prompt is private creator content that
        // ai.md keeps out of logs and out of rows that are not the prompt itself.
        Assert.Contains("FailureCategory", OperationNames);
        Assert.Contains("FailureSummary", OperationNames);
        Assert.DoesNotContain("ProviderResponseBody", OperationNames);
        Assert.DoesNotContain("RawResponse", OperationNames);
    }

    [Fact]
    public void The_operation_carries_the_key_that_stops_a_retry_buying_a_second_generation()
    {
        // Image generation is the most expensive thing this product does, so a lost response must not become a
        // second charge. Required and unique within the workspace, which the configuration enforces.
        Assert.Contains("IdempotencyKey", OperationNames);
    }

    [Fact]
    public void An_omitted_status_is_not_a_real_status_on_either_table()
    {
        // Both are stored as their number, and an enum column accepts any integer: a zero that meant something
        // real would make a write that went round the seam look like a legitimate state. The check constraints
        // refuse Unspecified, and these two members are what there is to refuse.
        Assert.Equal(0, (int)GeneratedImageStatus.Unspecified);
        Assert.Equal(0, (int)GeneratedImageOperationStatus.Unspecified);
    }
}
