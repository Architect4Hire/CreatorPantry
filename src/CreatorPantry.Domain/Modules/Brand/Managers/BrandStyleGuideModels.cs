namespace CreatorPantry.Domain.Modules.Brand.Managers;

public sealed record BrandStyleGuideSectionServiceModel(
    BrandStyleGuideSectionKey SectionKey, string? ChannelKey, string Body);

public sealed record BrandStyleGuideRuleServiceModel(BrandStyleGuideRuleKind Kind, string Text);

/// <summary>A cited source, by the document and the exact version number — never an object key.</summary>
public sealed record BrandStyleGuideSourceServiceModel(Guid DocumentId, int VersionNumber);

public sealed record BrandStyleGuideVersionServiceModel(
    Guid Id,
    int VersionNumber,
    string? ChangeReason,
    DateTimeOffset CreatedAt,
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> SourceDocuments);

/// <summary>A brand style guide with the version just created. Not approved and not the workspace default.</summary>
/// <param name="ConcurrencyToken">What a later edit has to quote.</param>
public sealed record BrandStyleGuideServiceModel(
    Guid Id,
    string DisplayName,
    string? Purpose,
    BrandStyleGuideStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken,
    BrandStyleGuideVersionServiceModel Version);

public sealed record BrandStyleGuideApprovalServiceModel(DateTimeOffset ApprovedAt, string? Reason);

public sealed record BrandStyleGuideActivationServiceModel(DateTimeOffset ActivatedAt, string? Reason);

/// <summary>One complete version of a guide, as read.</summary>
/// <param name="ParentVersionNumber">The version this was edited from; null for version 1.</param>
/// <param name="Approval">Null while the version is a draft.</param>
/// <param name="Activation">Present only on the workspace's active version.</param>
public sealed record BrandStyleGuideVersionDetailServiceModel(
    Guid Id,
    int VersionNumber,
    int? ParentVersionNumber,
    string? ChangeReason,
    DateTimeOffset CreatedAt,
    BrandStyleGuideApprovalServiceModel? Approval,
    BrandStyleGuideActivationServiceModel? Activation,
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceServiceModel> SourceDocuments);

/// <summary>One guide with its working version and, if it holds the workspace default, its active one.</summary>
/// <param name="WorkingVersion">The highest version number: the one an edit builds on.</param>
/// <param name="ActiveVersion">
/// The workspace's default version, only when it belongs to this guide. Null otherwise — never a fallback to
/// the latest approved version.
/// </param>
public sealed record BrandStyleGuideDetailServiceModel(
    Guid Id,
    string DisplayName,
    string? Purpose,
    BrandStyleGuideStatus Status,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ConcurrencyToken,
    BrandStyleGuideVersionDetailServiceModel WorkingVersion,
    BrandStyleGuideVersionDetailServiceModel? ActiveVersion);

/// <summary>
/// The guide version this workspace has made its default, whichever guide holds it.
/// </summary>
/// <remarks>
/// <para>
/// What a caller gets when it asks what the workspace writes in without already knowing which guide answers
/// that. <see cref="BrandStyleGuideDetailServiceModel"/> cannot: it is keyed on a guide and reports an active
/// version only when the default happens to belong to the guide that was asked about.
/// </para>
/// <para>
/// There is no working version here. A generation is grounded on what the creator activated, never on the
/// version somebody is still editing — and offering both would invite picking the wrong one.
/// </para>
/// </remarks>
/// <param name="Status">
/// The guide's own state. An archived guide can still hold the default — archiving is a shelf and does not
/// deactivate — so a caller that cares has to be able to see it.
/// </param>
public sealed record BrandActiveStyleGuideServiceModel(
    Guid GuideId,
    string DisplayName,
    string? Purpose,
    BrandStyleGuideStatus Status,
    BrandStyleGuideVersionDetailServiceModel Version);

/// <summary>The creator's input after blanks were dropped: what will actually be written.</summary>
public sealed record BrandStyleGuideDraft(
    string DisplayName,
    string? Purpose,
    IReadOnlyList<BrandStyleGuideSectionServiceModel> Sections,
    IReadOnlyList<BrandStyleGuideRuleServiceModel> Rules,
    IReadOnlyList<BrandStyleGuideSourceInput> Sources);
