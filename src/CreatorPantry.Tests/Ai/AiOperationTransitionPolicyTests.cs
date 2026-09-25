using CreatorPantry.Domain.Modules.Ai.Managers;

namespace CreatorPantry.Tests.Ai;

/// <summary>
/// The whole state machine, asserted directly rather than through whichever paths a seam happens to
/// exercise. <see cref="Every_ordered_pair_of_statuses_matches_the_table"/> is the exhaustive one; the rest
/// state the properties that table is supposed to have, so a future edit that breaks one of them fails with
/// the reason rather than with a list of pairs.
/// </summary>
public sealed class AiOperationTransitionPolicyTests
{
    /// <summary>
    /// The transition table, written out independently of the implementation. A change to the policy has to
    /// be made here too, deliberately — that is the point of restating it rather than reading it back.
    /// </summary>
    private static readonly (AiOperationStatus From, AiOperationStatus To)[] Expected =
    [
        (AiOperationStatus.Requested, AiOperationStatus.Running),
        (AiOperationStatus.Requested, AiOperationStatus.Failed),
        (AiOperationStatus.Requested, AiOperationStatus.Expired),

        (AiOperationStatus.Running, AiOperationStatus.Proposed),
        (AiOperationStatus.Running, AiOperationStatus.Failed),
        (AiOperationStatus.Running, AiOperationStatus.Requested),

        (AiOperationStatus.Proposed, AiOperationStatus.Accepted),
        (AiOperationStatus.Proposed, AiOperationStatus.PartiallyAccepted),
        (AiOperationStatus.Proposed, AiOperationStatus.Rejected),
        (AiOperationStatus.Proposed, AiOperationStatus.Expired),
    ];

    private static readonly AiOperationStatus[] ExpectedTerminal =
    [
        AiOperationStatus.Accepted,
        AiOperationStatus.PartiallyAccepted,
        AiOperationStatus.Rejected,
        AiOperationStatus.Failed,
        AiOperationStatus.Expired,
    ];

    [Fact]
    public void Every_ordered_pair_of_statuses_matches_the_table()
    {
        var statuses = AiOperationTransitionPolicy.AllStatuses;
        var wrong = new List<string>();

        foreach (var from in statuses)
        {
            foreach (var to in statuses)
            {
                var expected = Expected.Contains((from, to));
                var actual = AiOperationTransitionPolicy.IsAllowed(from, to);

                if (expected != actual)
                {
                    wrong.Add($"{from} -> {to}: expected {expected}, policy said {actual}");
                }
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        Assert.Equal(64, statuses.Count * statuses.Count);
    }

    [Fact]
    public void Nothing_leaves_a_terminal_status()
    {
        Assert.All(ExpectedTerminal, terminal =>
        {
            Assert.True(AiOperationTransitionPolicy.IsTerminal(terminal));
            Assert.Empty(AiOperationTransitionPolicy.AllowedFrom(terminal));
        });
    }

    [Fact]
    public void The_terminal_set_is_exactly_the_five_end_states()
    {
        Assert.Equal(
            ExpectedTerminal.Order().ToArray(),
            AiOperationTransitionPolicy.TerminalStatuses.Order().ToArray());
    }

    /// <summary>
    /// The restriction this state machine exists to enforce: once a creator has disposed of a proposal, the
    /// operation cannot be put back to work and quietly produce something else.
    /// </summary>
    [Fact]
    public void An_accepted_or_rejected_operation_never_returns_to_running()
    {
        AiOperationStatus[] disposed =
        [
            AiOperationStatus.Accepted,
            AiOperationStatus.PartiallyAccepted,
            AiOperationStatus.Rejected,
        ];

        Assert.All(disposed, status =>
            Assert.False(AiOperationTransitionPolicy.IsAllowed(status, AiOperationStatus.Running)));
    }

    [Fact]
    public void Only_a_requested_operation_can_start_running()
    {
        var sources = AiOperationTransitionPolicy.AllStatuses
            .Where(from => AiOperationTransitionPolicy.IsAllowed(from, AiOperationStatus.Running))
            .ToArray();

        Assert.Equal([AiOperationStatus.Requested], sources);
    }

    /// <summary>
    /// Lease recovery, and the only cycle in the table. It exists so a worker crash costs the creator a
    /// retry rather than their whole request.
    /// </summary>
    [Fact]
    public void A_running_operation_can_return_to_the_queue()
    {
        Assert.True(AiOperationTransitionPolicy.IsAllowed(
            AiOperationStatus.Running, AiOperationStatus.Requested));
    }

    [Fact]
    public void Requeueing_is_a_different_audit_event_from_requesting()
    {
        var requeued = AiOperationTransitionPolicy.AuditAction(
            AiOperationStatus.Running, AiOperationStatus.Requested);

        var started = AiOperationTransitionPolicy.AuditAction(
            AiOperationStatus.Requested, AiOperationStatus.Running);

        Assert.Equal("ai.operation.requeued", requeued);
        Assert.NotEqual(started, requeued);
    }

    [Fact]
    public void A_failed_operation_is_never_revived()
    {
        Assert.Empty(AiOperationTransitionPolicy.AllowedFrom(AiOperationStatus.Failed));
    }

    [Fact]
    public void A_status_never_transitions_to_itself()
    {
        Assert.All(AiOperationTransitionPolicy.AllStatuses, status =>
            Assert.False(AiOperationTransitionPolicy.IsAllowed(status, status)));
    }

    [Fact]
    public void Every_allowed_transition_has_a_distinct_stable_audit_action()
    {
        Assert.All(Expected, transition =>
        {
            var action = AiOperationTransitionPolicy.AuditAction(transition.From, transition.To);

            Assert.StartsWith("ai.operation.", action, StringComparison.Ordinal);
            Assert.NotEqual("ai.operation.", action);
        });
    }

    [Fact]
    public void A_disallowed_transition_has_no_audit_action()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AiOperationTransitionPolicy.AuditAction(
            AiOperationStatus.Accepted, AiOperationStatus.Running));
    }

    /// <summary>
    /// Audit references carry status names and nothing else — never a prompt body, recipe body, or provider
    /// payload.
    /// </summary>
    [Fact]
    public void Audit_references_are_the_two_status_names()
    {
        var (before, after) = AiOperationTransitionPolicy.AuditReferences(
            AiOperationStatus.Running, AiOperationStatus.Proposed);

        Assert.Equal("Running", before);
        Assert.Equal("Proposed", after);
    }

    /// <summary>
    /// Every status is reachable from the one an operation starts in, so no state is dead on arrival.
    /// </summary>
    [Fact]
    public void Every_status_is_reachable_from_requested()
    {
        var reached = new HashSet<AiOperationStatus> { AiOperationStatus.Requested };
        var frontier = new Queue<AiOperationStatus>([AiOperationStatus.Requested]);

        while (frontier.TryDequeue(out var status))
        {
            foreach (var next in AiOperationTransitionPolicy.AllowedFrom(status))
            {
                if (reached.Add(next))
                {
                    frontier.Enqueue(next);
                }
            }
        }

        Assert.Equal(AiOperationTransitionPolicy.AllStatuses.Order().ToArray(), reached.Order().ToArray());
    }
}
