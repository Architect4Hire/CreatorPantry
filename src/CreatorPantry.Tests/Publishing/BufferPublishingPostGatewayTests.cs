using System.Net;
using System.Text;
using System.Text.Json;
using CreatorPantry.Domain.Modules.Publishing.Gateways;
using CreatorPantry.Domain.Modules.Publishing.Integration.Buffer;
using CreatorPantry.Tests.Auth;
using Microsoft.Extensions.Logging;

namespace CreatorPantry.Tests.Publishing;

/// <summary>
/// The Buffer post adapter against recorded answers (INT-031): the mutation it sends, the one attempt it makes,
/// and what each kind of answer becomes.
/// </summary>
/// <remarks>
/// No test here calls Buffer. The responses are the shapes developers.buffer.com documents for
/// <c>createPost</c>: a <c>PostActionSuccess</c>, the error members of the payload union, the <c>errors</c>
/// array with <c>extensions.code</c>, and a 429 with <c>Retry-After</c>.
/// </remarks>
public sealed class BufferPublishingPostGatewayTests
{
    private const string ApiKey = "KEY_THAT_MUST_NOT_APPEAR";
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly DateTimeOffset DueAt = new(2026, 10, 16, 13, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PublishingSubmission Post(string text = "Soda bread, out of the oven at last.") =>
        new("ch_EXTERNAL_ig", text, DueAt);

    // ---- What it sends ----

    [Fact]
    public async Task It_asks_for_a_custom_scheduled_post_with_every_value_as_a_variable()
    {
        var buffer = new StubBuffer(Scripted.Ok(Success("update_EXTERNAL_1")));

        await buffer.Gateway().SubmitAsync(Post("Pumpkin bread \"today\", {not tomorrow}"), Ct);

        var sent = Assert.Single(buffer.Requests);
        var query = sent.Body.GetProperty("query").GetString()!;

        Assert.Contains("createPost(input: $input)", query, StringComparison.Ordinal);
        Assert.Contains("... on PostActionSuccess", query, StringComparison.Ordinal);

        // The creator's text never reaches the document. A post body that could close the input object and
        // add fields of its own is the injection this guards against.
        Assert.DoesNotContain("Pumpkin", query, StringComparison.Ordinal);

        var input = sent.Body.GetProperty("variables").GetProperty("input");
        Assert.Equal("ch_EXTERNAL_ig", input.GetProperty("channelId").GetString());
        Assert.Equal("Pumpkin bread \"today\", {not tomorrow}", input.GetProperty("text").GetString());

        // Buffer's own words for "its workers publish it" and "at this exact time".
        Assert.Equal("automatic", input.GetProperty("schedulingType").GetString());
        Assert.Equal("customScheduled", input.GetProperty("mode").GetString());

        // ISO 8601, UTC, to the millisecond, as the documented examples show.
        Assert.Equal("2026-10-16T13:30:00.000Z", input.GetProperty("dueAt").GetString());
    }

    [Fact]
    public async Task A_due_time_in_another_offset_is_sent_as_the_same_instant_in_utc()
    {
        var buffer = new StubBuffer(Scripted.Ok(Success("update_EXTERNAL_1")));
        var chicago = new DateTimeOffset(2026, 10, 16, 8, 30, 0, TimeSpan.FromHours(-5));

        await buffer.Gateway().SubmitAsync(new PublishingSubmission("ch_EXTERNAL_ig", "Morning.", chicago), Ct);

        Assert.Equal(
            "2026-10-16T13:30:00.000Z",
            Assert.Single(buffer.Requests).Body.GetProperty("variables").GetProperty("input")
                .GetProperty("dueAt").GetString());
    }

    // ---- What it makes of each answer ----

    [Fact]
    public async Task A_created_post_comes_back_scheduled_with_the_providers_id_for_it()
    {
        var buffer = new StubBuffer(Scripted.Ok(Success("update_EXTERNAL_1")));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Scheduled, result.Outcome);
        Assert.Equal("update_EXTERNAL_1", result.ExternalUpdateId);
        Assert.Null(result.FailureCategory);
        Assert.Null(result.FailureSummary);
    }

    [Fact]
    public async Task A_typed_error_is_a_refusal_categorised_by_its_type_and_never_by_its_message()
    {
        const string providerSays = "PROVIDER_PROSE_THAT_MUST_NOT_TRAVEL";
        var buffer = new StubBuffer(Scripted.Ok(
            $"{{\"data\":{{\"createPost\":{{\"__typename\":\"LimitReachedError\",\"message\":\"{providerSays}\"}}}}}}"));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Refused, result.Outcome);
        Assert.Equal("provider_limit_reached", result.FailureCategory);
        Assert.Null(result.ExternalUpdateId);

        // The provider writes its own messages and can quote the post back in them, so none of it travels.
        Assert.DoesNotContain(providerSays, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("InvalidInputError", "provider_rejected_input")]
    [InlineData("MutationError", "provider_refused")]
    public async Task The_error_members_this_was_written_against_are_refusals(string typeName, string category)
    {
        var buffer = new StubBuffer(Scripted.Ok(
            $"{{\"data\":{{\"createPost\":{{\"__typename\":\"{typeName}\",\"message\":\"no\"}}}}}}"));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Refused, result.Outcome);
        Assert.Equal(category, result.FailureCategory);
    }

    [Theory]
    [InlineData("{\"data\":{\"createPost\":{\"__typename\":\"SomethingNewBufferAdded\",\"message\":\"?\"}}}")]
    [InlineData("{\"data\":{\"createPost\":{\"message\":\"no type name at all\"}}}")]
    public async Task A_payload_member_this_was_not_written_against_leaves_the_outcome_unknown(string body)
    {
        // A type name is a symbol from a schema this server does not control. Declaring a post failed on the
        // strength of one it has never seen — which could be a new *success* member — is a claim it cannot
        // support.
        var buffer = new StubBuffer(Scripted.Ok(body));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
    }

    [Theory]
    [InlineData("UNAUTHORIZED")]
    [InlineData("FORBIDDEN")]
    public async Task A_refused_credential_is_a_refusal_and_is_never_retried(string code)
    {
        var buffer = new StubBuffer(Scripted.Ok(Errors(code)));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Refused, result.Outcome);
        Assert.Equal("credential_rejected", result.FailureCategory);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_throttle_says_how_long_it_asked_to_be_left_alone()
    {
        var buffer = new StubBuffer(new Scripted(HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 90));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(90), result.RetryAfter);
        Assert.Equal("rate_limited", result.FailureCategory);

        // Nothing was published, and nothing was tried twice.
        Assert.Single(buffer.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "provider_server_error")]
    [InlineData(HttpStatusCode.BadGateway, "provider_server_error")]
    [InlineData(HttpStatusCode.RequestTimeout, "unreadable_answer")]
    public async Task A_server_error_leaves_the_outcome_unknown_rather_than_claiming_nothing_was_published(
        HttpStatusCode status, string category)
    {
        // A 500 is not evidence that the mutation did not run — it could have failed after creating the post,
        // and Buffer documents nothing to the contrary. Calling this "nothing was published" is precisely the
        // reasoning that produces a duplicate post on the retry that follows.
        var buffer = new StubBuffer(new Scripted(status, "{}"));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Equal(category, result.FailureCategory);
    }

    [Fact]
    public async Task An_unexpected_error_code_leaves_the_outcome_unknown_even_though_the_docs_say_to_retry_it()
    {
        // Buffer's error guide advises retrying UNEXPECTED. For a read that is sound; for createPost it is the
        // duplicate-post path, so the outcome is recorded as unknown and reconciliation decides.
        var buffer = new StubBuffer(Scripted.Ok(Errors("UNEXPECTED")));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Equal("provider_unexpected", result.FailureCategory);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_post_that_was_created_and_warned_about_is_still_scheduled()
    {
        // Buffer's error-handling guide: "Even successful mutations can include warnings." Reading the errors
        // array first would record a live post as failed, and the reconciliation that followed would be
        // looking for a post it had just been told did not exist.
        var buffer = new StubBuffer(Scripted.Ok(
            "{\"data\":{\"createPost\":{\"__typename\":\"PostActionSuccess\",\"post\":"
            + "{\"id\":\"update_EXTERNAL_1\",\"dueAt\":\"2026-10-16T13:30:00.000Z\"}}},"
            + "\"errors\":[{\"message\":\"a warning\",\"extensions\":{\"code\":\"UNEXPECTED\"}}]}"));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Scheduled, result.Outcome);
        Assert.Equal("update_EXTERNAL_1", result.ExternalUpdateId);
    }

    [Fact]
    public async Task A_provider_that_never_answers_leaves_the_outcome_unknown()
    {
        var buffer = new StubBuffer(Scripted.Hangs);

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        // The whole point of this contract: a timeout is not a failure. A post may exist.
        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Equal("provider_timeout", result.FailureCategory);

        // And it is not sent again, here or anywhere below the ledger.
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_dropped_connection_leaves_the_outcome_unknown()
    {
        var buffer = new StubBuffer(Scripted.Throws);

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Equal("provider_connection_lost", result.FailureCategory);
        Assert.Single(buffer.Requests);
    }

    [Theory]
    [InlineData("{\"data\":{\"createPost\":{\"__typename\":\"PostActionSuccess\",\"post\":{}}}}")]
    [InlineData("{\"data\":{}}")]
    [InlineData("not json at all")]
    public async Task An_answer_this_server_cannot_read_leaves_the_outcome_unknown(string body)
    {
        // A success without an id, a missing field, or nonsense: in every case a post may have been created
        // and this server cannot say. Guessing "failed" here is what would lose a real post.
        var buffer = new StubBuffer(Scripted.Ok(body));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Equal("unreadable_answer", result.FailureCategory);
    }

    [Fact]
    public async Task A_post_id_too_long_for_the_mapping_record_is_not_truncated_into_one()
    {
        var buffer = new StubBuffer(Scripted.Ok(Success(new string('x', 129))));

        var result = await buffer.Gateway().SubmitAsync(Post(), Ct);

        // Half an id maps to nothing. Unknown is the honest answer: the post exists and we cannot name it.
        Assert.Equal(PublishingSubmissionOutcome.Unknown, result.Outcome);
        Assert.Null(result.ExternalUpdateId);
    }

    // ---- Removing a post ----

    [Fact]
    public async Task A_removal_asks_by_id_and_reads_only_the_type_of_the_answer()
    {
        var buffer = new StubBuffer(Scripted.Ok(DeleteTypeName("DeletePostSuccess")));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.Cancelled, result.Outcome);

        var sent = Assert.Single(buffer.Requests);
        var query = sent.Body.GetProperty("query").GetString()!;

        Assert.Contains("deletePost(input: $input)", query, StringComparison.Ordinal);

        // Field-free on purpose: DeletePostPayload's members are not in the part of Buffer's reference this
        // was written against, and naming one a union does not have would make every removal a validation
        // error — which is to say, an unknown outcome every time.
        Assert.Contains("__typename", query, StringComparison.Ordinal);
        Assert.DoesNotContain("... on", query, StringComparison.Ordinal);

        // And the id travels as a variable, never spliced into the document.
        Assert.DoesNotContain("update_EXTERNAL_1", query, StringComparison.Ordinal);
        Assert.Equal(
            "update_EXTERNAL_1", sent.Body.GetProperty("variables").GetProperty("input").GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_post_the_provider_does_not_have_is_already_gone_which_is_what_was_asked_for()
    {
        var buffer = new StubBuffer(Scripted.Ok(Errors("NOT_FOUND")));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        // The answer a repeated removal gives, and the reason repeating one is safe.
        Assert.Equal(PublishingCancellationOutcome.AlreadyGone, result.Outcome);
        Assert.Equal("provider_post_absent", result.FailureCategory);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task A_post_the_provider_will_not_remove_is_not_cancelled()
    {
        var buffer = new StubBuffer(Scripted.Ok(DeleteTypeName("PostNotDeletableError")));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.NotDeletable, result.Outcome);
        Assert.Equal("provider_post_not_deletable", result.FailureCategory);
    }

    [Fact]
    public async Task A_removal_is_retried_where_a_submit_is_not()
    {
        // Three silences. Asking again cannot remove a second post, so the gateway asks — and only gives up
        // once the budget is spent, which is the opposite of what createPost does with its single attempt.
        var buffer = new StubBuffer(Scripted.Hangs, Scripted.Hangs, Scripted.Hangs);

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.Unknown, result.Outcome);
        Assert.Equal("provider_timeout", result.FailureCategory);
        Assert.Equal(3, buffer.Requests.Count);
    }

    [Fact]
    public async Task A_removal_that_succeeds_on_a_later_attempt_is_cancelled()
    {
        var buffer = new StubBuffer(
            new Scripted(HttpStatusCode.InternalServerError, "{}"), Scripted.Ok(DeleteTypeName("DeletePostSuccess")));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.Cancelled, result.Outcome);
        Assert.Equal(2, buffer.Requests.Count);
    }

    [Fact]
    public async Task A_throttled_removal_says_how_long_and_does_not_keep_asking()
    {
        var buffer = new StubBuffer(new Scripted(HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 120));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(120), result.RetryAfter);

        // A wait longer than a moment is reported rather than held open.
        Assert.Single(buffer.Requests);
    }

    [Theory]
    [InlineData("UNAUTHORIZED")]
    [InlineData("FORBIDDEN")]
    public async Task A_refused_credential_cannot_remove_anything_and_is_not_retried(string code)
    {
        var buffer = new StubBuffer(Scripted.Ok(Errors(code)));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.Refused, result.Outcome);
        Assert.Equal("credential_rejected", result.FailureCategory);
        Assert.Single(buffer.Requests);
    }

    [Fact]
    public async Task An_answer_this_server_cannot_read_leaves_the_removal_unknown()
    {
        var buffer = new StubBuffer(
            Scripted.Ok(DeleteTypeName("SomethingNewBufferAdded")),
            Scripted.Ok(DeleteTypeName("SomethingNewBufferAdded")),
            Scripted.Ok(DeleteTypeName("SomethingNewBufferAdded")));

        var result = await buffer.Gateway().CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.Unknown, result.Outcome);
    }

    [Fact]
    public async Task A_host_with_no_provider_removes_nothing_and_says_so()
    {
        var result = await new UnconfiguredPublishingPostGateway()
            .CancelAsync(new PublishingCancellation("update_EXTERNAL_1"), Ct);

        Assert.Equal(PublishingCancellationOutcome.NotConfigured, result.Outcome);
    }

    // ---- Listing what the provider holds ----

    [Fact]
    public async Task A_listing_asks_for_one_channel_and_filters_by_nothing_else()
    {
        var buffer = new StubBuffer(Scripted.Ok(Page([Node("update_EXTERNAL_1", "sent")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.Equal(PublishingPostLookupOutcome.Listed, result.Outcome);

        var input = Assert.Single(buffer.Requests).Body.GetProperty("variables").GetProperty("input");
        Assert.Equal("org_1", input.GetProperty("organizationId").GetString());
        Assert.Equal("ch_1", input.GetProperty("filter").GetProperty("channelIds")[0].GetString());

        // Deliberately no status filter: a post in a state the filter did not ask for would read as absent,
        // and an absence is what licenses recording a live post as never made.
        Assert.False(input.GetProperty("filter").TryGetProperty("status", out _));
    }

    [Fact]
    public async Task A_listing_read_to_its_end_says_it_is_complete()
    {
        var buffer = new StubBuffer(Scripted.Ok(Page([Node("update_EXTERNAL_1", "scheduled")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.True(result.Complete);
        var post = Assert.Single(result.Posts);
        Assert.Equal("update_EXTERNAL_1", post.ExternalUpdateId);
        Assert.Equal(PublishingPostState.Scheduled, post.State);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 13, 30, 0, TimeSpan.Zero), post.DueAt);
    }

    [Fact]
    public async Task A_listing_with_no_readable_page_info_is_not_a_complete_listing()
    {
        // The one that matters most: completeness is what licenses concluding a post was never made, so a
        // reply this server cannot read it from must not come back saying the listing was whole. A default of
        // "no next page" here would turn a malformed answer into proof of an absence — and a live post into
        // a failed one that a creator then re-confirms, publishing it twice.
        foreach (var body in new[]
                 {
                     "{\"data\":{\"posts\":{\"edges\":[]}}}",
                     "{\"data\":{\"posts\":{\"edges\":[],\"pageInfo\":{}}}}",
                     "{\"data\":{\"posts\":{\"edges\":[],\"pageInfo\":{\"hasNextPage\":\"no\"}}}}",
                 })
        {
            var buffer = new StubBuffer(Scripted.Ok(body), Scripted.Ok(body), Scripted.Ok(body));

            var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

            Assert.Equal(PublishingPostLookupOutcome.Unavailable, result.Outcome);
            Assert.False(result.Complete);
        }
    }

    [Fact]
    public async Task A_listing_follows_the_cursor_and_gathers_every_page()
    {
        var buffer = new StubBuffer(
            Scripted.Ok(Page([Node("update_EXTERNAL_1", "sent")], hasNextPage: true, endCursor: "cursor-1")),
            Scripted.Ok(Page([Node("update_EXTERNAL_2", "error")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.True(result.Complete);
        Assert.Equal(
            ["update_EXTERNAL_1", "update_EXTERNAL_2"],
            result.Posts.Select(post => post.ExternalUpdateId));
        Assert.Equal(PublishingPostState.Failed, result.Posts[1].State);

        Assert.Equal(2, buffer.Requests.Count);
        Assert.Equal("cursor-1", buffer.Requests[1].Body.GetProperty("variables").GetProperty("after").GetString());
    }

    [Fact]
    public async Task A_listing_longer_than_this_reads_says_it_is_not_complete()
    {
        // Four pages, each claiming another: what was read is real, and what lies past it is unknown.
        var page = Page([Node("update_EXTERNAL_1", "scheduled")], hasNextPage: true, endCursor: "cursor");
        var buffer = new StubBuffer(
            Scripted.Ok(page), Scripted.Ok(page), Scripted.Ok(page), Scripted.Ok(page));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.Equal(PublishingPostLookupOutcome.Listed, result.Outcome);
        Assert.False(result.Complete);
        Assert.Equal(4, buffer.Requests.Count);
    }

    [Fact]
    public async Task A_status_the_listing_does_not_recognise_is_read_as_still_to_go_out()
    {
        var buffer = new StubBuffer(Scripted.Ok(Page([Node("update_EXTERNAL_1", "something_new")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        // Conservative on purpose: "sent" and "error" are both terminal here, so an unknown status must not
        // be read as either. Still-to-go-out leaves the record polling.
        Assert.Equal(PublishingPostState.Scheduled, Assert.Single(result.Posts).State);
    }

    [Fact]
    public async Task A_listing_this_server_cannot_read_concludes_nothing()
    {
        foreach (var body in new[] { "{\"data\":{}}", "{\"data\":{\"posts\":{}}}", "not json" })
        {
            var buffer = new StubBuffer(Scripted.Ok(body), Scripted.Ok(body), Scripted.Ok(body));

            var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

            Assert.Equal(PublishingPostLookupOutcome.Unavailable, result.Outcome);
        }
    }

    [Fact]
    public async Task A_throttled_listing_says_so_and_concludes_nothing()
    {
        var buffer = new StubBuffer(new Scripted(HttpStatusCode.TooManyRequests, "{}", RetryAfterSeconds: 45));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.Equal(PublishingPostLookupOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
        Assert.False(result.Complete);
    }

    [Fact]
    public async Task A_listing_is_retried_because_reading_costs_nothing_to_repeat()
    {
        var buffer = new StubBuffer(
            new Scripted(HttpStatusCode.InternalServerError, "{}"),
            Scripted.Ok(Page([Node("update_EXTERNAL_1", "sent")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.Equal(PublishingPostLookupOutcome.Listed, result.Outcome);
        Assert.Equal(2, buffer.Requests.Count);
    }

    [Fact]
    public async Task A_post_id_too_long_for_the_mapping_record_makes_the_listing_unreadable()
    {
        var buffer = new StubBuffer(
            Scripted.Ok(Page([Node(new string('x', 129), "sent")], hasNextPage: false)),
            Scripted.Ok(Page([Node(new string('x', 129), "sent")], hasNextPage: false)),
            Scripted.Ok(Page([Node(new string('x', 129), "sent")], hasNextPage: false)));

        var result = await buffer.Gateway().ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        // Rather than a listing missing one post, which would read as that post being absent.
        Assert.Equal(PublishingPostLookupOutcome.Unavailable, result.Outcome);
    }

    [Fact]
    public async Task A_host_with_no_provider_lists_nothing_and_says_so()
    {
        var result = await new UnconfiguredPublishingPostGateway()
            .ListPostsAsync(new PublishingPostLookup("org_1", "ch_1"), Ct);

        Assert.Equal(PublishingPostLookupOutcome.NotConfigured, result.Outcome);
        Assert.False(result.Complete);
    }

    // ---- What it says about itself ----

    [Fact]
    public async Task Nothing_of_the_providers_and_nothing_of_the_credential_is_logged()
    {
        const string providerSays = "PROVIDER_PROSE_THAT_MUST_NOT_TRAVEL";
        var logs = new CapturingLoggerProvider();
        var buffer = new StubBuffer(Scripted.Ok(
            $"{{\"data\":{{\"createPost\":{{\"__typename\":\"InvalidInputError\",\"message\":\"{providerSays}\"}}}}}}"));

        await buffer.Gateway(logs).SubmitAsync(Post(), Ct);

        var written = string.Join("\n", logs.Entries);
        Assert.Contains("Buffer post submit ended", written, StringComparison.Ordinal);
        Assert.DoesNotContain(providerSays, written, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, written, StringComparison.Ordinal);
    }

    [Fact]
    public void It_describes_the_provider_and_the_one_workspace_the_credential_belongs_to()
    {
        var descriptor = new StubBuffer().Gateway().Describe();

        Assert.True(descriptor.Configured);
        Assert.Equal("buffer", descriptor.ProviderKey);
        Assert.Equal(Workspace, descriptor.WorkspaceId);
    }

    [Fact]
    public async Task A_host_with_no_provider_sends_nothing_and_says_so()
    {
        var result = await new UnconfiguredPublishingPostGateway().SubmitAsync(Post(), Ct);

        Assert.Equal(PublishingSubmissionOutcome.NotConfigured, result.Outcome);
        Assert.Null(result.ExternalUpdateId);
    }

    // ---- Recorded answers ----

    private static string Success(string postId) =>
        $"{{\"data\":{{\"createPost\":{{\"__typename\":\"PostActionSuccess\",\"post\":"
        + $"{{\"id\":\"{postId}\",\"dueAt\":\"2026-10-16T13:30:00.000Z\"}}}}}}}}";

    private static string Node(string externalId, string status, string text = "Soda bread, out of the oven at last.") =>
        $"{{\"node\":{{\"id\":\"{externalId}\",\"text\":\"{text}\",\"dueAt\":\"2026-10-16T13:30:00.000Z\","
        + $"\"channelId\":\"ch_1\",\"status\":\"{status}\"}}}}";

    private static string Page(string[] nodes, bool hasNextPage, string? endCursor = null) =>
        $"{{\"data\":{{\"posts\":{{\"edges\":[{string.Join(",", nodes)}],\"pageInfo\":"
        + $"{{\"hasNextPage\":{(hasNextPage ? "true" : "false")},"
        + $"\"endCursor\":{(endCursor is null ? "null" : $"\"{endCursor}\"")}}}}}}}}}";

    private static string DeleteTypeName(string typeName) =>
        $"{{\"data\":{{\"deletePost\":{{\"__typename\":\"{typeName}\"}}}}}}";

    private static string Errors(string code) =>
        $"{{\"errors\":[{{\"message\":\"no\",\"extensions\":{{\"code\":\"{code}\"}}}}]}}";

    private sealed record Scripted(
        HttpStatusCode Status, string Body, int? RetryAfterSeconds = null, bool Fails = false, bool Never = false)
    {
        public static Scripted Ok(string body) => new(HttpStatusCode.OK, body);

        /// <summary>The connection fails outright.</summary>
        public static readonly Scripted Throws = new(0, string.Empty, Fails: true);

        /// <summary>No answer until the attempt gives up.</summary>
        public static readonly Scripted Hangs = new(0, string.Empty, Never: true);
    }

    private sealed record Sent(HttpMethod Method, string Uri, string Raw, JsonElement Body);

    /// <summary>Buffer, as a queue of recorded responses.</summary>
    private sealed class StubBuffer(params Scripted[] script) : HttpMessageHandler
    {
        private readonly Queue<Scripted> _script = new(script);

        public List<Sent> Requests { get; } = [];

        public BufferPublishingPostGateway Gateway(CapturingLoggerProvider? logs = null)
        {
            var http = new HttpClient(this);
            http.DefaultRequestHeaders.Authorization = new("Bearer", ApiKey);

            return new BufferPublishingPostGateway(
                http,
                new BufferPublishingOptions(new Uri("https://api.buffer.example"), ApiKey, Workspace)
                {
                    AttemptTimeout = TimeSpan.FromMilliseconds(150),
                    RetryBaseDelay = TimeSpan.FromMilliseconds(1),
                },
                (logs is null ? LoggerFactory.Create(_ => { }) : LoggerFactory.Create(builder => builder.AddProvider(logs)))
                    .CreateLogger<BufferPublishingPostGateway>());
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var raw = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new Sent(request.Method, request.RequestUri!.ToString(), raw, JsonDocument.Parse(raw).RootElement.Clone()));

            var next = _script.Count > 0
                ? _script.Dequeue()
                : throw new InvalidOperationException("The gateway asked for more than was scripted.");

            if (next.Fails)
            {
                throw new HttpRequestException("No such host is known. (api.buffer.example:443)");
            }

            if (next.Never)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var response = new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body, Encoding.UTF8, "application/json"),
            };

            if (next.RetryAfterSeconds is { } seconds)
            {
                response.Headers.RetryAfter = new(TimeSpan.FromSeconds(seconds));
            }

            return response;
        }
    }
}
