using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Outbox.Events;
using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Recipes.Managers;
using CreatorPantry.Domain.Modules.Tenancy.Facade;
using CreatorPantry.Domain.Modules.Tenancy.Managers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CreatorPantry.Tests.Content;

/// <summary>The fan-out handler's own behaviour, with the workspace and consumers replaced by fakes.</summary>
public sealed class RecipeVersionChangedOutboxHandlerTests
{
    private static readonly RecipeVersionChangedEvent Change = new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 2, RecipeVersionChangeCause.Edit, Guid.NewGuid());

    private static OutboxMessageEnvelope Envelope(string payload) =>
        new(Guid.NewGuid(), RecipeVersionChangedEvent.MessageType, payload, Guid.NewGuid(), 1);

    private sealed class Consumer(Action? onCall = null) : IRecipeChangeConsumer
    {
        public int Calls { get; private set; }

        public Task OnRecipeVersionChangedAsync(RecipeVersionChangedEvent change, CancellationToken cancellationToken)
        {
            Calls++;
            onCall?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class Resolution(bool found) : IWorkspaceResolutionFacade
    {
        public int ServiceResolutions { get; private set; }

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForServiceAsync(Guid workspaceId, CancellationToken cancellationToken)
        {
            ServiceResolutions++;
            return Task.FromResult(found
                ? OperationResult<ResolvedWorkspaceServiceModel>.Success(new ResolvedWorkspaceServiceModel(
                    workspaceId, "ws", WorkspaceServiceIdentity.MembershipId, WorkspaceServiceIdentity.Role, WorkspaceServiceIdentity.AccountId))
                : OperationResult<ResolvedWorkspaceServiceModel>.Failure(new OperationError("not-found", "gone", new Dictionary<string, string[]>())));
        }

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveAsync(string userId, ResolveWorkspaceViewModel model, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OperationResult<ResolvedWorkspaceServiceModel>> ResolveForOperationAsync(Guid workspaceId, Guid membershipId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static IOutboxMessageHandler Handler(Resolution resolution, params IRecipeChangeConsumer[] consumers)
    {
        var services = new ServiceCollection().AddSingleton<IWorkspaceResolutionFacade>(resolution);

        foreach (var consumer in consumers)
        {
            services.AddSingleton(consumer);
        }

        var provider = services.BuildServiceProvider();

        return new RecipeVersionChangedOutboxHandler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RecipeVersionChangedOutboxHandler>.Instance);
    }

    [Fact]
    public async Task Every_consumer_is_called_even_when_one_fails_and_the_failure_is_raised_afterwards()
    {
        var failing = new Consumer(() => throw new InvalidOperationException("boom"));
        var healthy = new Consumer();

        var handler = Handler(new Resolution(found: true), failing, healthy);

        await Assert.ThrowsAsync<AggregateException>(() => handler.HandleAsync(Envelope(Change.Serialize()), TestContext.Current.CancellationToken));

        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, healthy.Calls);
    }

    [Fact]
    public async Task A_workspace_that_no_longer_exists_completes_without_calling_a_consumer()
    {
        var consumer = new Consumer();

        await Handler(new Resolution(found: false), consumer)
            .HandleAsync(Envelope(Change.Serialize()), TestContext.Current.CancellationToken);

        Assert.Equal(0, consumer.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"workspaceId":"00000000-0000-0000-0000-000000000000","recipeId":"00000000-0000-0000-0000-000000000001","recipeVersionId":"00000000-0000-0000-0000-000000000002"}""")]
    public async Task A_payload_that_is_not_a_complete_event_is_refused_before_any_workspace_is_resolved(string payload)
    {
        var resolution = new Resolution(found: true);
        var consumer = new Consumer();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Handler(resolution, consumer).HandleAsync(Envelope(payload), TestContext.Current.CancellationToken));

        Assert.Equal(0, resolution.ServiceResolutions);
        Assert.Equal(0, consumer.Calls);
    }

    [Fact]
    public async Task The_workspace_is_resolved_once_per_delivery_from_the_payload_and_handed_to_every_consumer()
    {
        var resolution = new Resolution(found: true);
        var consumer = new Consumer();

        await Handler(resolution, consumer).HandleAsync(Envelope(Change.Serialize()), TestContext.Current.CancellationToken);

        Assert.Equal(1, resolution.ServiceResolutions);
        Assert.Equal(1, consumer.Calls);
    }
}
