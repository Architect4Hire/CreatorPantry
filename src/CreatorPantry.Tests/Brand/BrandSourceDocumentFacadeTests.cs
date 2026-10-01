using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Brand.Business;
using CreatorPantry.Domain.Modules.Brand.Facade;
using CreatorPantry.Domain.Modules.Brand.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using FluentValidation;

namespace CreatorPantry.Tests.Brand;

/// <summary>
/// The facade's own part of the compensation: a failure that surfaces after the store returned — the
/// idempotent unit's commit — which no layer below can see. The endpoint tests cover every failure that
/// happens inside the store.
/// </summary>
public sealed class BrandSourceDocumentFacadeTests
{
    private static readonly UploadBrandSourceDocumentViewModel Model = new()
    {
        Title = "House style",
        DocumentType = BrandSourceDocumentType.StyleGuide,
        Purpose = BrandSourcePurpose.Voice,
    };

    private static BrandSourceDocumentFacade FacadeOver(StubBusiness business, IIdempotentCommandExecutor executor, WorkspaceRole role = WorkspaceRole.Editor) =>
        new(
            new InlineValidator<UploadBrandSourceDocumentViewModel>(),

            // The real one, so these tests see the token rule the route actually enforces.
            new ReplaceBrandSourceDocumentViewModelValidator(),
            new BrandSourceDocumentLifecycleViewModelValidator(),
            business,
            new StubWorkspace(role),
            executor);

    private static BrandSourceUploadFile File() => new(new MemoryStream(BrandSourceSampleFiles.Pdf()), "house-style.pdf");
    /// <summary>A token this API could have issued. Which row it belongs to is for Business to decide.</summary>
    private const string Token = "AAAAAAAAAAE=";

    private const string OtherToken = "AAAAAAAAAAI=";

    private static readonly ReplaceBrandSourceDocumentViewModel Replacement = new() { ExpectedConcurrencyToken = Token };

    [Fact]
    public async Task A_failure_after_the_store_returned_abandons_the_upload_and_still_propagates()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new CommitFailsExecutor());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.UploadAsync("user", Model, File(), "key", TestContext.Current.CancellationToken));

        Assert.Equal(1, business.Stores);
        Assert.Equal((business.Prepared!.DocumentId, business.Prepared.VersionId), Assert.Single(business.Abandoned));
    }

    [Fact]
    public async Task A_replay_neither_stores_nor_abandons()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new ReplayingExecutor());

        var outcome = await facade.UploadAsync("user", Model, File(), "key", TestContext.Current.CancellationToken);

        Assert.True(outcome.Replayed);
        Assert.Equal(0, business.Stores);
        Assert.Empty(business.Abandoned);
    }

    [Fact]
    public async Task The_same_upload_fingerprints_alike_and_any_difference_in_bytes_or_description_does_not()
    {
        var executor = new RecordingExecutor();

        async Task<string> FingerprintAsync(UploadBrandSourceDocumentViewModel model, string checksum)
        {
            var business = new StubBusiness { Checksum = checksum };
            await FacadeOver(business, executor).UploadAsync("user", model, File(), "key", TestContext.Current.CancellationToken);
            return System.Text.Json.JsonSerializer.Serialize(executor.Last!.Fingerprint);
        }

        var first = await FingerprintAsync(Model with { Title = "  House style ", Tags = ["Launch"] }, "sha256:a");
        var same = await FingerprintAsync(Model with { Tags = ["launch"] }, "sha256:a");
        var otherBytes = await FingerprintAsync(Model with { Tags = ["Launch"] }, "sha256:b");
        var otherTitle = await FingerprintAsync(Model with { Title = "Another", Tags = ["Launch"] }, "sha256:a");

        // Two attempts generate different identifiers; the fingerprint must not carry them.
        Assert.Equal(first, same);
        Assert.NotEqual(first, otherBytes);
        Assert.NotEqual(first, otherTitle);
        Assert.Equal("brand.source.upload", executor.Last!.Operation);
        Assert.True(executor.Last.KeyRequired);
    }

    [Fact]
    public async Task Below_editor_nothing_is_read_or_stored()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor(), WorkspaceRole.Contributor);

        var outcome = await facade.UploadAsync("user", Model, File(), "key", TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SourceForbidden, outcome.Result.Error!.Code);
        Assert.Equal(0, business.Prepares);
    }

    // ---- Replacement ----

    [Fact]
    public async Task A_failure_after_the_store_returned_abandons_the_replacement_and_still_propagates()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new CommitFailsExecutor());
        var documentId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            facade.ReplaceAsync("user", documentId, Replacement, File(), "key", TestContext.Current.CancellationToken));

        Assert.Equal(1, business.Stores);

        // The version this attempt created, under the document it was for. Never the version it replaced:
        // that object has to survive a failed replacement.
        var abandoned = Assert.Single(business.Abandoned);
        Assert.Equal(documentId, abandoned.DocumentId);
        Assert.Equal(business.PreparedReplacement!.VersionId, abandoned.VersionId);
    }

    [Fact]
    public async Task A_replayed_replacement_neither_stores_nor_abandons()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new ReplayingExecutor());

        var outcome = await facade.ReplaceAsync(
            "user", Guid.NewGuid(), Replacement, File(), "key", TestContext.Current.CancellationToken);

        Assert.True(outcome.Replayed);
        Assert.Equal(0, business.Stores);
        Assert.Empty(business.Abandoned);
    }

    [Fact]
    public async Task The_same_replacement_fingerprints_alike_and_a_different_document_token_or_file_does_not()
    {
        var executor = new RecordingExecutor();
        var documentId = Guid.NewGuid();

        async Task<string> FingerprintAsync(Guid id, string token, string checksum, string fileName = "house-style.pdf")
        {
            var business = new StubBusiness { Checksum = checksum };
            await FacadeOver(business, executor).ReplaceAsync(
                "user",
                id,
                new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = token },
                new BrandSourceUploadFile(new MemoryStream(BrandSourceSampleFiles.Pdf()), fileName),
                "key",
                TestContext.Current.CancellationToken);

            return System.Text.Json.JsonSerializer.Serialize(executor.Last!.Fingerprint);
        }

        var first = await FingerprintAsync(documentId, Token, "sha256:a");
        var same = await FingerprintAsync(documentId, Token, "sha256:a");
        var otherDocument = await FingerprintAsync(Guid.NewGuid(), Token, "sha256:a");
        var otherToken = await FingerprintAsync(documentId, OtherToken, "sha256:a");
        var otherBytes = await FingerprintAsync(documentId, Token, "sha256:b");
        var otherName = await FingerprintAsync(documentId, Token, "sha256:a", "renamed.pdf");

        // The version id is generated per attempt, so it must not be in there.
        Assert.Equal(first, same);
        Assert.NotEqual(first, otherDocument);

        // The same file sent again after someone else's edit is a different request about a different state.
        Assert.NotEqual(first, otherToken);
        Assert.NotEqual(first, otherBytes);
        Assert.NotEqual(first, otherName);

        Assert.Equal("brand.source.replace", executor.Last!.Operation);
        Assert.True(executor.Last.KeyRequired);
    }

    [Fact]
    public async Task Below_editor_a_replacement_reads_nothing_and_stores_nothing()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor(), WorkspaceRole.Contributor);

        var outcome = await facade.ReplaceAsync(
            "user", Guid.NewGuid(), Replacement, File(), "key", TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SourceForbidden, outcome.Result.Error!.Code);
        Assert.Equal(0, business.Prepares);
        Assert.Equal(0, business.Stores);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-token")]
    public async Task A_replacement_without_a_usable_token_is_refused_before_the_file_is_read(string? token)
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor());

        var outcome = await facade.ReplaceAsync(
            "user",
            Guid.NewGuid(),
            new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = token },
            File(),
            "key",
            TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, outcome.Result.Error!.Code);
        Assert.True(outcome.Result.Error.FieldErrors.ContainsKey("expectedConcurrencyToken"));
        Assert.Equal(0, business.Prepares);
    }

    [Fact]
    public async Task A_replacement_with_no_file_is_refused_by_field_alongside_the_token()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor());

        var outcome = await facade.ReplaceAsync(
            "user",
            Guid.NewGuid(),
            new ReplaceBrandSourceDocumentViewModel { ExpectedConcurrencyToken = null },
            file: null,
            "key",
            TestContext.Current.CancellationToken);

        // Both failures in one answer: a client fixes one round trip, not two.
        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, outcome.Result.Error!.Code);
        Assert.Contains("expectedConcurrencyToken", outcome.Result.Error.FieldErrors.Keys);
        Assert.Contains("file", outcome.Result.Error.FieldErrors.Keys);
        Assert.Equal(0, business.Prepares);
    }


    // ---- Lifecycle role gate ----

    /// <summary>
    /// The gate that the route's own <c>[Authorize]</c> would hide: an Editor must not reach a restore. Both
    /// land a document on <c>Archived</c>, so a role decided from the target state rather than from the
    /// command would let one through — and a worker or an AI plugin calling this boundary has no
    /// <c>[Authorize]</c> in front of it at all.
    /// </summary>
    [Theory]
    [InlineData(BrandSourceDocumentLifecycleCommand.Archive, true)]
    [InlineData(BrandSourceDocumentLifecycleCommand.Unarchive, true)]
    [InlineData(BrandSourceDocumentLifecycleCommand.Remove, true)]
    [InlineData(BrandSourceDocumentLifecycleCommand.Restore, false)]
    public async Task An_editor_may_issue_every_lifecycle_command_except_a_restore(
        BrandSourceDocumentLifecycleCommand command, bool allowed)
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor(), WorkspaceRole.Editor);

        var outcome = await facade.TransitionAsync(
            "user", Guid.NewGuid(), command, new BrandSourceDocumentLifecycleViewModel { ExpectedConcurrencyToken = Token },
            TestContext.Current.CancellationToken);

        if (allowed)
        {
            Assert.True(outcome.Succeeded, outcome.Error?.Code);
            Assert.Equal(command, Assert.Single(business.Transitions));
        }
        else
        {
            Assert.Equal(BrandErrorCodes.SourceForbidden, outcome.Error!.Code);
            Assert.Empty(business.Transitions);
        }
    }

    [Fact]
    public async Task An_owner_may_restore()
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor(), WorkspaceRole.Owner);

        var outcome = await facade.TransitionAsync(
            "user", Guid.NewGuid(), BrandSourceDocumentLifecycleCommand.Restore,
            new BrandSourceDocumentLifecycleViewModel { ExpectedConcurrencyToken = Token },
            TestContext.Current.CancellationToken);

        Assert.True(outcome.Succeeded, outcome.Error?.Code);
        Assert.Equal(BrandSourceDocumentLifecycleCommand.Restore, Assert.Single(business.Transitions));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-token")]
    public async Task A_lifecycle_command_without_a_usable_token_never_reaches_business(string? token)
    {
        var business = new StubBusiness();
        var facade = FacadeOver(business, new RecordingExecutor());

        var outcome = await facade.TransitionAsync(
            "user", Guid.NewGuid(), BrandSourceDocumentLifecycleCommand.Archive,
            new BrandSourceDocumentLifecycleViewModel { ExpectedConcurrencyToken = token },
            TestContext.Current.CancellationToken);

        Assert.Equal(BrandErrorCodes.SourceInvalidRequest, outcome.Error!.Code);
        Assert.True(outcome.Error.FieldErrors.ContainsKey("expectedConcurrencyToken"));
        Assert.Empty(business.Transitions);
    }

    private sealed class StubBusiness : IBrandSourceDocumentBusiness
    {
        public string Checksum { get; init; } = "sha256:a";

        public int Prepares { get; private set; }

        public int Stores { get; private set; }

        public BrandSourcePreparedUpload? Prepared { get; private set; }

        public BrandSourcePreparedReplacement? PreparedReplacement { get; private set; }

        public List<(Guid DocumentId, Guid VersionId)> Abandoned { get; } = [];

        public Task<OperationResult<BrandSourcePreparedUpload>> PrepareAsync(
            UploadBrandSourceDocumentViewModel model, BrandSourceUploadFile file, CancellationToken cancellationToken)
        {
            Prepares++;
            Prepared = new BrandSourcePreparedUpload(
                Guid.NewGuid(), Guid.NewGuid(), "application/pdf", file.Content.Length, Checksum, file.FileName!, file.Content);

            return Task.FromResult(OperationResult<BrandSourcePreparedUpload>.Success(Prepared));
        }

        public Task<OperationResult<BrandSourceDocumentServiceModel>> StoreAsync(
            string actorUserId, UploadBrandSourceDocumentViewModel model, BrandSourcePreparedUpload upload, CancellationToken cancellationToken)
        {
            Stores++;

            return Task.FromResult(OperationResult<BrandSourceDocumentServiceModel>.Success(ServiceModel(upload)));
        }

        public Task AbandonAsync(Guid documentId, Guid versionId)
        {
            Abandoned.Add((documentId, versionId));
            return Task.CompletedTask;
        }

        public Task<OperationResult<BrandSourcePreparedReplacement>> PrepareReplacementAsync(
            Guid documentId, string? expectedConcurrencyToken, BrandSourceUploadFile file, CancellationToken cancellationToken)
        {
            Prepares++;
            PreparedReplacement = new BrandSourcePreparedReplacement(
                documentId, Guid.NewGuid(), "application/pdf", file.Content.Length, Checksum, file.FileName!, file.Content);

            return Task.FromResult(OperationResult<BrandSourcePreparedReplacement>.Success(PreparedReplacement));
        }

        public Task<OperationResult<BrandSourceDocumentServiceModel>> StoreReplacementAsync(
            string actorUserId, string? expectedConcurrencyToken, BrandSourcePreparedReplacement replacement, CancellationToken cancellationToken)
        {
            Stores++;

            return Task.FromResult(OperationResult<BrandSourceDocumentServiceModel>.Success(ServiceModel()));
        }

        public Task<CreatorPantry.Domain.Managers.Paging.CursorPageServiceModel<BrandSourceDocumentSummaryServiceModel>> ListAsync(
            BrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException("An upload has no business listing the library.");

        // These tests are about what the upload facade does around an upload. The two reads pass straight
        // through the facade, so there is nothing for a stub of them to observe.
        public Task<OperationResult<BrandSourceDocumentDetailServiceModel>> GetAsync(
            Guid documentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("An upload has no business reading a document back.");

        public Task<OperationResult<BrandSourceDownload>> OpenVersionAsync(
            Guid documentId, int versionNumber, CancellationToken cancellationToken) =>
            throw new NotSupportedException("An upload has no business downloading a document.");

        public List<BrandSourceDocumentLifecycleCommand> Transitions { get; } = [];

        public Task<OperationResult<BrandSourceDocumentDetailServiceModel?>> TransitionAsync(
            string actorUserId,
            Guid documentId,
            BrandSourceDocumentLifecycleCommand command,
            string? expectedConcurrencyToken,
            CancellationToken cancellationToken)
        {
            Transitions.Add(command);

            return Task.FromResult(OperationResult<BrandSourceDocumentDetailServiceModel?>.Success(null));
        }

        public Task<OperationResult<BrandSourceDocumentUsageServiceModel>> UsageAsync(
            Guid documentId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The usage read passes straight through the facade.");

        public Task<OperationResult<CreatorPantry.Domain.Managers.Paging.CursorPageServiceModel<RemovedBrandSourceDocumentServiceModel>>> ListRemovedAsync(
            RemovedBrandSourceDocumentListCriteria criteria, CancellationToken cancellationToken) =>
            throw new NotSupportedException("The bin's own role gate is Business's, and is tested through the endpoint.");

    }

    private static BrandSourceDocumentServiceModel ServiceModel(BrandSourcePreparedUpload? upload = null) => new(
        upload?.DocumentId ?? Guid.NewGuid(),
        "House style",
        BrandSourceDocumentType.StyleGuide,
        BrandSourcePurpose.Voice,
        ChannelKey: null,
        Audience: null,
        Tags: [],
        BrandSourceDocumentStatus.Active,
        new BrandSourceDocumentVersionServiceModel(
            upload?.VersionId ?? Guid.NewGuid(), 1, "application/pdf", 1, "house-style.pdf", DateTimeOffset.UnixEpoch),
        DateTimeOffset.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        ConcurrencyToken: string.Empty);

    /// <summary>Runs the operation, then fails the way a commit that could not complete does.</summary>
    private sealed class CommitFailsExecutor : IIdempotentCommandExecutor
    {
        public async Task<IdempotentOutcome<T>> ExecuteAsync<T>(
            IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
        {
            await operation(cancellationToken);
            throw new InvalidOperationException("The commit failed.");
        }
    }

    private sealed class ReplayingExecutor : IIdempotentCommandExecutor
    {
        public Task<IdempotentOutcome<T>> ExecuteAsync<T>(
            IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken) =>
            Task.FromResult(new IdempotentOutcome<T>(OperationResult<T>.Success((T)(object)ServiceModel()), Replayed: true));
    }

    private sealed class RecordingExecutor : IIdempotentCommandExecutor
    {
        public IdempotentCommand? Last { get; private set; }

        public async Task<IdempotentOutcome<T>> ExecuteAsync<T>(
            IdempotentCommand command, Func<CancellationToken, Task<OperationResult<T>>> operation, CancellationToken cancellationToken)
        {
            Last = command;
            return new IdempotentOutcome<T>(await operation(cancellationToken), Replayed: false);
        }
    }

    private sealed class StubWorkspace(WorkspaceRole role) : IWorkspaceContext
    {
        public bool IsResolved => true;

        public Guid WorkspaceId { get; } = Guid.NewGuid();

        public string WorkspaceSlug => "workspace";

        public Guid MembershipId { get; } = Guid.NewGuid();

        public string AccountId => "acct";

        public WorkspaceRole Role { get; } = role;
    }
}
