using CreatorPantry.Domain.Managers.Persistence;
using CreatorPantry.Domain.Managers.Audit;
using CreatorPantry.Domain.Managers.Outbox;
using CreatorPantry.Domain.Managers.Idempotency;
using CreatorPantry.Domain.Modules.Tenancy.Data.Entities;
using CreatorPantry.Domain.Modules.Auth.Data.Entities;
using CreatorPantry.Domain.Modules.Measurement.Data.Entities;
using CreatorPantry.Domain.Modules.Vocabulary.Data.Entities;
using CreatorPantry.Domain.Modules.Ingredients.Data.Entities;
using CreatorPantry.Domain.Managers.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace CreatorPantry.Tests.Outbox;

/// <summary>Commit, replay, restart (crash recovery), and poison — the four behaviors the outbox exists for.</summary>
public sealed class OutboxDispatcherTests : IAsyncLifetime
{
    private const string MessageType = "test.message";

    private static readonly DateTimeOffset Start = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly FakeTimeProvider _time = new(Start);
    private readonly RecordingHandler _handler = new();
    private ServiceProvider _services = null!;

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(_time)
            .AddApplicationTime()
            .AddDbContext<CreatorPantryDbContext>(options => options.UseSqlite(_connection).ReplaceService<IModelCustomizer, SqliteModelCustomizer>())
            .AddOutbox()
            .AddKeyedSingleton<IOutboxMessageHandler>(MessageType, _handler)
            .BuildServiceProvider(validateScopes: true);

        using var scope = _services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>().Database.EnsureCreatedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Commit_the_outbox_row_commits_with_the_domain_write_that_enqueued_it()
    {
        var workspaceId = Guid.NewGuid();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Sam's Kitchen", Slug = "sams-kitchen", CreatedAt = Start });
            scope.ServiceProvider.GetRequiredService<IOutboxWriter>()
                .Enqueue(MessageType, "{}", Guid.NewGuid());

            // One SaveChangesAsync over both: the domain write and the event either both land or neither does.
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using var readScope = _services.CreateAsyncScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        Assert.NotNull(await readDb.Workspaces.FindAsync([workspaceId], TestContext.Current.CancellationToken));
        var message = await readDb.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(MessageType, message.Type);
        Assert.Equal(OutboxMessageStatus.Pending, message.Status);
    }

    [Fact]
    public async Task Replay_a_completed_message_is_never_delivered_again()
    {
        await EnqueueAsync();

        var first = await DispatchAsync();
        Assert.Equal(1, first.Completed);
        Assert.Equal(1, _handler.CallCount);

        // A later dispatch pass (the next poll tick) must not redeliver a message already Completed.
        var second = await DispatchAsync();

        Assert.Equal(0, second.Claimed);
        Assert.Equal(1, _handler.CallCount);
    }

    [Fact]
    public async Task Restart_a_message_whose_lease_expired_without_completing_is_reclaimed()
    {
        var messageId = await EnqueueAsync();

        // Simulate a Worker process that claimed the message and then died before recording an outcome:
        // set the row Leased with an already-past LeaseExpiresAt, bypassing the dispatcher entirely.
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
            message.Status = OutboxMessageStatus.Leased;
            message.LeasedBy = Guid.NewGuid();
            message.LeaseExpiresAt = _time.GetUtcNow() - TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var summary = await DispatchAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(1, summary.Completed);
        Assert.Equal(1, _handler.CallCount);
    }

    [Fact]
    public async Task A_still_leased_message_within_its_lease_is_not_reclaimed()
    {
        var messageId = await EnqueueAsync();

        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
            message.Status = OutboxMessageStatus.Leased;
            message.LeasedBy = Guid.NewGuid();
            message.LeaseExpiresAt = _time.GetUtcNow() + TimeSpan.FromMinutes(1);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var summary = await DispatchAsync();

        Assert.Equal(0, summary.Claimed);
        Assert.Equal(0, _handler.CallCount);
    }

    [Fact]
    public async Task Poison_a_message_that_always_fails_stops_retrying_after_the_attempt_limit()
    {
        _handler.OnHandle = _ => throw new InvalidOperationException("handler always fails");
        await EnqueueAsync();

        for (var attempt = 1; attempt <= OutboxPolicy.MaxAttempts; attempt++)
        {
            var summary = await DispatchAsync();
            Assert.Equal(1, summary.Claimed);

            if (attempt < OutboxPolicy.MaxAttempts)
            {
                Assert.Equal(1, summary.Retrying);
                // Advance past the backoff window so the next pass can reclaim it immediately.
                _time.Advance(OutboxPolicy.BackoffFor(attempt) + TimeSpan.FromSeconds(1));
            }
            else
            {
                Assert.Equal(1, summary.Poisoned);
            }
        }

        Assert.Equal(OutboxPolicy.MaxAttempts, _handler.CallCount);

        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var message = await db.OutboxMessages.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OutboxMessageStatus.Poisoned, message.Status);
        Assert.Contains("handler always fails", message.LastError);

        // A poisoned message is never claimed again, however far time moves on.
        _time.Advance(TimeSpan.FromDays(1));
        var afterward = await DispatchAsync();
        Assert.Equal(0, afterward.Claimed);
    }

    [Fact]
    public async Task A_failed_attempt_that_has_not_exhausted_retries_is_not_immediately_reclaimable()
    {
        _handler.OnHandle = _ => throw new InvalidOperationException("transient failure");
        await EnqueueAsync();

        var first = await DispatchAsync();
        Assert.Equal(1, first.Retrying);

        // Backoff has not elapsed yet: the message is Pending again but not yet due.
        var tooSoon = await DispatchAsync();
        Assert.Equal(0, tooSoon.Claimed);

        _time.Advance(OutboxPolicy.BackoffFor(1) + TimeSpan.FromSeconds(1));
        var afterBackoff = await DispatchAsync();
        Assert.Equal(1, afterBackoff.Claimed);
    }

    [Fact]
    public async Task An_unregistered_message_type_is_poisoned_on_its_final_attempt_instead_of_throwing_out_of_the_dispatcher()
    {
        await using (var scope = _services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            scope.ServiceProvider.GetRequiredService<IOutboxWriter>().Enqueue("no.such.handler", "{}", Guid.NewGuid());
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        for (var attempt = 1; attempt < OutboxPolicy.MaxAttempts; attempt++)
        {
            await DispatchAsync();
            _time.Advance(OutboxPolicy.BackoffFor(attempt) + TimeSpan.FromSeconds(1));
        }

        var final = await DispatchAsync();
        Assert.Equal(1, final.Poisoned);
    }

    /// <summary>
    /// The guard a second Worker instance relies on (12.10l): two dispatchers that both read a message as due
    /// cannot both claim it. The second claim's UPDATE names the state it read, matches nothing, and fails.
    /// </summary>
    [Fact]
    public async Task Two_dispatchers_that_read_the_same_due_message_cannot_both_claim_it()
    {
        var messageId = await EnqueueAsync();
        var token = TestContext.Current.CancellationToken;

        await using var firstScope = _services.CreateAsyncScope();
        await using var secondScope = _services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();

        // Both read it while it is Pending.
        var seenByFirst = await first.OutboxMessages.SingleAsync(m => m.Id == messageId, token);
        var seenBySecond = await second.OutboxMessages.SingleAsync(m => m.Id == messageId, token);

        foreach (var seen in (OutboxMessage[])[seenByFirst, seenBySecond])
        {
            seen.Status = OutboxMessageStatus.Leased;
            seen.LeasedBy = Guid.NewGuid();
            seen.LeaseExpiresAt = _time.GetUtcNow() + TimeSpan.FromMinutes(1);
        }

        await first.SaveChangesAsync(token);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(token));

        // And the row is the winner's.
        await using var check = _services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == messageId, token);
        Assert.Equal(seenByFirst.LeasedBy, stored.LeasedBy);
    }

    /// <summary>
    /// A handler that outlives its lease does not get to record an outcome over whoever took the message
    /// next: the outcome write quotes this pass's own lease, and is dropped when the row no longer holds it.
    /// </summary>
    [Fact]
    public async Task An_outcome_is_not_recorded_for_a_message_whose_lease_was_taken_while_it_was_handled()
    {
        var messageId = await EnqueueAsync();
        var takenBy = Guid.NewGuid();

        _handler.OnHandle = async _ =>
        {
            // Another dispatcher, finding the lease lapsed, takes the message while this handler is still working.
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Id == messageId);
            message.LeasedBy = takenBy;
            message.LeaseExpiresAt = _time.GetUtcNow() + TimeSpan.FromMinutes(5);
            await db.SaveChangesAsync();
        };

        var summary = await DispatchAsync();

        Assert.Equal(1, summary.Claimed);
        Assert.Equal(0, summary.Completed);
        Assert.Equal(0, summary.Retrying);
        Assert.Equal(0, summary.Poisoned);

        await using var check = _services.CreateAsyncScope();
        var stored = await check.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);

        // Still leased, to the dispatcher that took it — not marked completed by the one that lost it.
        Assert.Equal(OutboxMessageStatus.Leased, stored.Status);
        Assert.Equal(takenBy, stored.LeasedBy);
        Assert.Null(stored.CompletedAt);
    }

    [Fact]
    public async Task A_pass_claims_and_delivers_every_due_message_once()
    {
        await EnqueueAsync(count: 3);

        var summary = await DispatchAsync();
        var again = await DispatchAsync();

        Assert.Equal(3, summary.Claimed);
        Assert.Equal(3, summary.Completed);
        Assert.Equal(0, again.Claimed);
        Assert.Equal(3, _handler.CallCount);
    }

    private async Task EnqueueAsync(int count)
    {
        await using var scope = _services.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter>();

        for (var index = 0; index < count; index++)
        {
            writer.Enqueue(MessageType, "{}", Guid.NewGuid());
        }

        await scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>()
            .SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> EnqueueAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CreatorPantryDbContext>();
        var id = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter>().Enqueue(MessageType, "{}", id);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return await db.OutboxMessages.Select(m => m.Id).SingleAsync(TestContext.Current.CancellationToken);
    }

    private async Task<OutboxDispatchSummary> DispatchAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>()
            .DispatchDueAsync(TestContext.Current.CancellationToken);
    }

    private sealed class RecordingHandler : IOutboxMessageHandler
    {
        public int CallCount { get; private set; }

        public Func<OutboxMessageEnvelope, Task>? OnHandle { get; set; }

        public async Task HandleAsync(OutboxMessageEnvelope message, CancellationToken cancellationToken)
        {
            CallCount++;
            if (OnHandle is not null)
            {
                await OnHandle(message);
            }
        }
    }
}
