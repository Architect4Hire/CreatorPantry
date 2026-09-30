using CreatorPantry.Domain.Managers.Patching;
using CreatorPantry.Domain.Managers.Reference;
using CreatorPantry.Domain.Modules.Brand.Managers;

namespace CreatorPantry.Tests.Brand;

public sealed class BrandProfileValidatorTests
{
    private static readonly CreateBrandProfileViewModelValidator Create = new(new ContentChannelCatalog());

    private static readonly UpdateBrandProfileViewModelValidator Update = new(new ContentChannelCatalog());

    private static readonly string ValidToken = Convert.ToBase64String(new byte[8]);

    private static UpdateBrandProfileViewModel Patch() => new() { ExpectedConcurrencyToken = ValidToken };

    private static IReadOnlyList<string> FieldsOf(FluentValidation.Results.ValidationResult result) =>
        [.. result.Errors.Select(error => error.PropertyName)];

    [Fact]
    public void A_name_alone_is_a_valid_profile() =>
        Assert.True(Create.Validate(new CreateBrandProfileViewModel { BrandName = "Sam's Kitchen" }).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_needs_a_name(string? name) =>
        Assert.Contains("BrandName", FieldsOf(Create.Validate(new CreateBrandProfileViewModel { BrandName = name })));

    [Fact]
    public void Name_length_is_measured_after_trimming()
    {
        var atLimit = "  " + new string('a', BrandPolicy.BrandNameMaxLength) + "  ";
        var over = new string('a', BrandPolicy.BrandNameMaxLength + 1);

        Assert.True(Create.Validate(new CreateBrandProfileViewModel { BrandName = atLimit }).IsValid);
        Assert.False(Create.Validate(new CreateBrandProfileViewModel { BrandName = over }).IsValid);
    }

    [Theory]
    [InlineData("en", true)]
    [InlineData("en-US", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("english", false)]
    [InlineData("en_US", false)]
    [InlineData("e", false)]
    public void Locale_must_be_a_language_tag(string locale, bool valid) =>
        Assert.Equal(valid, Create.Validate(new CreateBrandProfileViewModel { BrandName = "B", Locale = locale }).IsValid);

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("http://example.com/about", true)]
    [InlineData("ftp://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://user:pass@example.com", false)]
    [InlineData("/relative", false)]
    [InlineData("", false)]
    public void Links_must_be_plain_http_addresses(string url, bool valid)
    {
        var model = new CreateBrandProfileViewModel
        {
            BrandName = "B",
            Links = [new BrandLinkInput { Kind = BrandLinkKind.Website, Url = url }],
        };

        Assert.Equal(valid, Create.Validate(model).IsValid);
    }

    [Fact]
    public void Duplicate_links_and_channels_are_refused()
    {
        var model = new CreateBrandProfileViewModel
        {
            BrandName = "B",
            Links =
            [
                new BrandLinkInput { Kind = BrandLinkKind.Website, Url = "https://example.com" },
                new BrandLinkInput { Kind = BrandLinkKind.Reference, Url = "HTTPS://EXAMPLE.COM" },
            ],
            ChannelDefaults =
            [
                new BrandChannelDefaultInput { ChannelKey = "instagram" },
                new BrandChannelDefaultInput { ChannelKey = "instagram" },
            ],
        };

        var fields = FieldsOf(Create.Validate(model));

        Assert.Contains("Links[1].Url", fields);
        Assert.Contains("ChannelDefaults[1].ChannelKey", fields);
    }

    [Fact]
    public void List_sizes_are_capped()
    {
        var channels = Enumerable.Range(0, BrandPolicy.MaxChannelDefaults + 1)
            .Select(i => (BrandChannelDefaultInput?)new BrandChannelDefaultInput { ChannelKey = $"c{i}" }).ToList();
        var links = Enumerable.Range(0, BrandPolicy.MaxLinks + 1)
            .Select(i => (BrandLinkInput?)new BrandLinkInput { Kind = BrandLinkKind.Reference, Url = $"https://example.com/{i}" }).ToList();

        var fields = FieldsOf(Create.Validate(new CreateBrandProfileViewModel
        {
            BrandName = "B",
            ChannelDefaults = channels,
            Links = links,
        }));

        Assert.Contains("ChannelDefaults", fields);
        Assert.Contains("Links", fields);
    }

    [Fact]
    public void A_profile_has_one_primary_logo_and_unique_assets()
    {
        var id = Guid.NewGuid();
        var model = new CreateBrandProfileViewModel
        {
            BrandName = "B",
            Assets =
            [
                new BrandAssetInput { MediaAssetId = id, Role = BrandAssetRole.PrimaryLogo },
                new BrandAssetInput { MediaAssetId = Guid.NewGuid(), Role = BrandAssetRole.PrimaryLogo },
                new BrandAssetInput { MediaAssetId = id, Role = BrandAssetRole.AlternateLogo },
            ],
        };

        var fields = FieldsOf(Create.Validate(model));

        Assert.Contains("Assets[1].Role", fields);
        Assert.Contains("Assets[2].MediaAssetId", fields);
    }

    // ---- Update ----

    [Fact]
    public void An_empty_patch_with_a_token_is_valid() => Assert.True(Update.Validate(Patch()).IsValid);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public void Update_needs_a_token_this_api_could_have_issued(string? token) =>
        Assert.Contains(
            "ExpectedConcurrencyToken",
            FieldsOf(Update.Validate(new UpdateBrandProfileViewModel { ExpectedConcurrencyToken = token })));

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void A_submitted_name_can_be_changed_but_never_cleared(string? name) =>
        Assert.Contains(
            "BrandName",
            FieldsOf(Update.Validate(Patch() with { BrandName = PatchField<string?>.Submitted(name) })));

    [Fact]
    public void Optional_text_may_be_cleared_with_null_or_omitted()
    {
        var cleared = Patch() with
        {
            ShortDescription = PatchField<string?>.Submitted(null),
            DefaultAudience = PatchField<string?>.Submitted(null),
            Locale = PatchField<string?>.Submitted(null),
            TimeZoneId = PatchField<string?>.Submitted(null),
        };

        Assert.True(Update.Validate(cleared).IsValid);
    }

    [Fact]
    public void Only_submitted_fields_are_checked()
    {
        var model = Patch() with { Locale = PatchField<string?>.Submitted("english") };

        Assert.Equal(["Locale"], FieldsOf(Update.Validate(model)));
    }

    [Fact]
    public void The_reason_is_capped()
    {
        var model = Patch() with { Reason = new string('r', BrandPolicy.ReasonMaxLength + 1) };

        Assert.Contains("Reason", FieldsOf(Update.Validate(model)));
    }

    [Fact]
    public void The_reason_is_accepted_at_its_limit_and_refused_one_over()
    {
        Assert.True(Update.Validate(Patch() with { Reason = new string('r', BrandPolicy.ReasonMaxLength) }).IsValid);
        Assert.False(Update.Validate(Patch() with { Reason = new string('r', BrandPolicy.ReasonMaxLength + 1) }).IsValid);
    }

    [Fact]
    public void Links_may_carry_query_strings_and_fragments()
    {
        var model = new CreateBrandProfileViewModel
        {
            BrandName = "B",
            Links = [new BrandLinkInput { Kind = BrandLinkKind.Reference, Url = "https://example.com/p?q=1#section" }],
        };

        Assert.True(Create.Validate(model).IsValid);
    }

    [Theory]
    [InlineData("_blog", false)]
    [InlineData("-blog", false)]
    [InlineData("Instagram", false)]
    [InlineData("has space", false)]
    [InlineData("", false)]
    [InlineData("instagram", true)]
    [InlineData("x", true)]
    [InlineData("blog", false)]
    public void Channel_keys_must_be_well_formed_and_known(string key, bool valid) =>
        Assert.Equal(valid, Create.Validate(new CreateBrandProfileViewModel
        {
            BrandName = "B",
            ChannelDefaults = [new BrandChannelDefaultInput { ChannelKey = key }],
        }).IsValid);

    [Fact]
    public void A_channel_key_is_accepted_at_its_length_limit_and_refused_one_over()
    {
        var atLimit = new string('a', BrandPolicy.ChannelKeyMaxLength);
        var validator = new CreateBrandProfileViewModelValidator(
            new ContentChannelCatalog([new ContentChannel(atLimit, "Long")]));

        BrandChannelDefaultInput Key(string key) => new() { ChannelKey = key };

        Assert.True(validator.Validate(new CreateBrandProfileViewModel { BrandName = "B", ChannelDefaults = [Key(atLimit)] }).IsValid);
        Assert.False(validator.Validate(new CreateBrandProfileViewModel { BrandName = "B", ChannelDefaults = [Key(atLimit + "a")] }).IsValid);
    }
}
