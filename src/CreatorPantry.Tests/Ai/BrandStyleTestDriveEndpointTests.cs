using System.Net;
using System.Net.Http.Json;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Business;
using CreatorPantry.Domain.Modules.Ai.Facade;
using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// 11A.24's request route: what a client may ask for, and what it cannot reach.
/// </summary>
public sealed class BrandStyleTestDriveEndpointTests
{
    private const string Route = "/api/v1/workspaces/workspace-a/brand-style-test-drives";

    // ---- field shape ----

    /// <summary>
    /// Three fields, and the contract's real content is what is absent: no task, no scope, no channel, no
    /// prompt, no model, no provider parameter, no tool list and no workspace, because the type has nowhere to
    /// put one.
    /// </summary>
    [Fact]
    public void The_request_carries_only_the_guide_its_version_and_a_subject()
    {
        var fields = typeof(RequestBrandStyleTestDriveViewModel).GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["GuideId", "Subject", "VersionNumber"], fields);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("scope")]
    [InlineData("recipe")]
    [InlineData("channel")]
    [InlineData("prompt")]
    [InlineData("template")]
    [InlineData("model")]
    [InlineData("provider")]
    [InlineData("tool")]
    [InlineData("workspace")]
    [InlineData("system")]
    public void The_request_cannot_name_a_task_a_channel_or_a_provider_concern(string forbidden)
    {
        Assert.DoesNotContain(
            typeof(RequestBrandStyleTestDriveViewModel).GetProperties(),
            property => property.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
    }

    // ---- validator ----

    [Fact]
    public void A_guide_and_a_version_are_enough()
    {
        var result = Validate(new RequestBrandStyleTestDriveViewModel
        {
            GuideId = Guid.NewGuid(),
            VersionNumber = 1,
        });

        Assert.True(result.IsValid);
    }

    /// <summary>
    /// There is no default version here: trying a draft out before activating it is the point of the route, so
    /// a request that named none would be answered about a different guide than the creator is looking at.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_request_without_a_version_number_is_refused(int versionNumber)
    {
        var result = Validate(new RequestBrandStyleTestDriveViewModel
        {
            GuideId = Guid.NewGuid(),
            VersionNumber = versionNumber,
        });

        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(RequestBrandStyleTestDriveViewModel.VersionNumber));
    }

    [Fact]
    public void A_request_without_a_guide_is_refused()
    {
        var result = Validate(new RequestBrandStyleTestDriveViewModel { VersionNumber = 1 });

        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(RequestBrandStyleTestDriveViewModel.GuideId));
    }

    /// <summary>
    /// The subject is a short phrase, not a brief. A field long enough to hold instructions would be a way to
    /// steer a generation this contract has no other way to steer.
    /// </summary>
    [Fact]
    public void A_subject_over_its_bound_is_refused()
    {
        var result = Validate(new RequestBrandStyleTestDriveViewModel
        {
            GuideId = Guid.NewGuid(),
            VersionNumber = 1,
            Subject = new string('x', AiPolicy.StyleSampleSubjectMaxLength + 1),
        });

        Assert.Contains(
            result.Errors,
            error => error.PropertyName == nameof(RequestBrandStyleTestDriveViewModel.Subject));
    }

    [Fact]
    public void A_request_with_no_subject_uses_the_platforms_own_example()
    {
        Assert.True(Validate(new RequestBrandStyleTestDriveViewModel
        {
            GuideId = Guid.NewGuid(),
            VersionNumber = 1,
        }).IsValid);

        Assert.Equal(BrandStyleTestDriveSubject.Default, BrandStyleTestDriveSubject.Resolve(null));
        Assert.Equal(BrandStyleTestDriveSubject.Default, BrandStyleTestDriveSubject.Resolve("   "));
        Assert.Equal("a plum cake", BrandStyleTestDriveSubject.Resolve("  a plum cake  "));
    }

    // ---- routes ----

    [Fact]
    public async Task Asking_for_a_test_drive_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            Route,
            new RequestBrandStyleTestDriveViewModel { GuideId = Guid.NewGuid(), VersionNumber = 1 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Reading_a_test_drive_without_a_session_is_refused()
    {
        await using var host = await SqliteApiHost.StartAsync();
        var client = host.Factory.CreateClient();

        var response = await client.GetAsync($"{Route}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- idempotency ----

    /// <summary>
    /// A missing key is refused at the Facade, before Business and therefore before anything is queued — proven
    /// by a business stub that throws if it is ever reached. A test drive is two generations, so a caller with
    /// no natural key must not be allowed to omit one.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_idempotency_key_is_refused_before_business_is_ever_called(string? key)
    {
        IAiBrandStyleTestDriveFacade facade = new AiBrandStyleTestDriveFacade(
            new NeverCalledBusiness(), new RequestBrandStyleTestDriveViewModelValidator());

        var outcome = await facade.RequestAsync(
            new RequestBrandStyleTestDriveViewModel { GuideId = Guid.NewGuid(), VersionNumber = 1 },
            key,
            TestContext.Current.CancellationToken);

        Assert.False(outcome.Replayed);
        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(IdempotencyPolicy.KeyRequiredCode, outcome.Result.Error!.Code);
    }

    /// <summary>An invalid request is refused before Business too, and before a key is even looked for.</summary>
    [Fact]
    public async Task An_invalid_request_is_refused_before_business_is_ever_called()
    {
        IAiBrandStyleTestDriveFacade facade = new AiBrandStyleTestDriveFacade(
            new NeverCalledBusiness(), new RequestBrandStyleTestDriveViewModelValidator());

        var outcome = await facade.RequestAsync(
            new RequestBrandStyleTestDriveViewModel(), "a-key", TestContext.Current.CancellationToken);

        Assert.False(outcome.Result.Succeeded);
        Assert.Equal(AiBrandStyleTestDriveRequestErrors.RequestInvalid, outcome.Result.Error!.Code);
    }

    private static FluentValidation.Results.ValidationResult Validate(
        RequestBrandStyleTestDriveViewModel model) =>
        new RequestBrandStyleTestDriveViewModelValidator().Validate(model);

    private sealed class NeverCalledBusiness : IAiBrandStyleTestDriveBusiness
    {
        public Task<IdempotentOutcome<BrandStyleTestDriveServiceModel>> RequestAsync(
            RequestBrandStyleTestDriveViewModel model, string idempotencyKey, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Business must not be reached with an invalid request.");

        public Task<OperationResult<BrandStyleTestDriveServiceModel>> GetAsync(
            Guid requestId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Business must not be reached by these tests.");
    }
}
