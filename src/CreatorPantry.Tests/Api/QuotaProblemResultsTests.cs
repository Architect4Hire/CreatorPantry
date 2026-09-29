extern alias ApiService;

using System.Globalization;
using CreatorPantry.Domain.Managers.Results;
using CreatorPantry.Domain.Modules.Ai.Managers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace CreatorPantry.Tests.Api;

/// <summary>
/// How a quota refusal reaches the wire (USAGE-007): which status each code selects, the structured facts it
/// carries, and the <c>Retry-After</c> that says when to come back.
/// </summary>
/// <remarks>
/// The refusal <em>decision</em> — who is refused and why — is
/// <see cref="AiUsage.AiRequestQuotaRefusalTests"/>. This is the mapping between that decision and HTTP, which
/// is the part a client actually depends on.
/// </remarks>
public sealed class QuotaProblemResultsTests
{
    /// <summary>
    /// The suffix convention doing its job: a new refusal maps itself by being named consistently, rather than
    /// by someone remembering to add a case.
    /// </summary>
    [Theory]
    [InlineData(AiProposalErrors.QuotaExhausted, StatusCodes.Status429TooManyRequests)]
    [InlineData(AiProposalErrors.QuotaSuspended, StatusCodes.Status403Forbidden)]
    public void Each_quota_code_maps_to_the_status_its_remedy_implies(string code, int expected) =>
        Assert.Equal(expected, ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(code));

    /// <summary>
    /// A spent allowance is not a role failure. The two arrive as different statuses so a client that only
    /// looks at the status still behaves sensibly, and as different codes so one that reads them can say
    /// which.
    /// </summary>
    [Fact]
    public void An_exhausted_allowance_and_a_suspension_are_not_the_same_answer()
    {
        Assert.NotEqual(
            ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(AiProposalErrors.QuotaExhausted),
            ApiService::CreatorPantry.ApiService.Http.ProblemResults.StatusFor(AiProposalErrors.QuotaSuspended));

        Assert.NotEqual(AiProposalErrors.QuotaExhausted, AiProposalErrors.QuotaSuspended);
    }

    [Fact]
    public void The_refusals_structured_facts_reach_the_body_beside_the_code()
    {
        var resetsAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var problem = Refuse(new OperationError(
            AiProposalErrors.QuotaExhausted,
            "Your AI allowance for this period is spent.",
            new Dictionary<string, string[]>(),
            new Dictionary<string, object?>
            {
                ["unit"] = "Credits",
                ["allowance"] = 1000m,
                ["remaining"] = 5m,
                ["required"] = 20m,
                ["resetsAt"] = resetsAt,
            }));

        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);

        Assert.Equal(StatusCodes.Status429TooManyRequests, problem.StatusCode);
        Assert.Equal(AiProposalErrors.QuotaExhausted, details.Extensions["code"]);
        Assert.Equal("Credits", details.Extensions["unit"]);
        Assert.Equal(1000m, details.Extensions["allowance"]);
        Assert.Equal(5m, details.Extensions["remaining"]);
        Assert.Equal(20m, details.Extensions["required"]);
        Assert.Equal(resetsAt, details.Extensions["resetsAt"]);
    }

    /// <summary>
    /// A 429 without a <c>Retry-After</c> leaves a client guessing, and guessing wrong is a retry storm. An
    /// HTTP-date rather than delta-seconds: the reset is a stored instant, and both sides already agree about
    /// instants.
    /// </summary>
    [Fact]
    public void A_spent_allowance_says_when_to_come_back()
    {
        var resetsAt = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var controller = Controller();

        Refuse(
            new OperationError(
                AiProposalErrors.QuotaExhausted,
                "spent",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["resetsAt"] = resetsAt }),
            controller);

        Assert.Equal(
            resetsAt.ToString("R", CultureInfo.InvariantCulture),
            controller.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>
    /// A suspension carries no <c>Retry-After</c>, because there is no instant at which it stops being true.
    /// Telling a client to come back would send it round a loop that cannot end differently.
    /// </summary>
    [Fact]
    public void A_suspension_never_says_when_to_come_back()
    {
        var controller = Controller();

        Refuse(
            new OperationError(
                AiProposalErrors.QuotaSuspended,
                "switched off",
                new Dictionary<string, string[]>(),
                new Dictionary<string, object?> { ["unit"] = "Credits" }),
            controller);

        Assert.True(StringValuesIsEmpty(controller.Response.Headers.RetryAfter.ToString()));
    }

    /// <summary>
    /// An application error cannot overwrite the two extensions this layer owns. One that could would be able
    /// to lie about which refusal it is, or about which request it belongs to.
    /// </summary>
    [Theory]
    [InlineData("code")]
    [InlineData("traceId")]
    public void A_refusal_cannot_overwrite_the_extensions_the_edge_owns(string reserved)
    {
        var problem = Refuse(new OperationError(
            AiProposalErrors.QuotaSuspended,
            "switched off",
            new Dictionary<string, string[]>(),
            new Dictionary<string, object?> { [reserved] = "spoofed" }));

        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);

        details.Extensions.TryGetValue(reserved, out var value);

        Assert.NotEqual("spoofed", value);
    }

    /// <summary>An error with no extensions still maps exactly as it did before they existed.</summary>
    [Fact]
    public void An_error_carrying_no_extensions_is_unaffected()
    {
        var problem = Refuse(new OperationError(
            AiProposalErrors.RecipeNotFound, "gone", new Dictionary<string, string[]>()));

        var details = Assert.IsAssignableFrom<ProblemDetails>(problem.Value);

        Assert.Equal(StatusCodes.Status404NotFound, problem.StatusCode);
        Assert.Equal(AiProposalErrors.RecipeNotFound, details.Extensions["code"]);
    }

    private static bool StringValuesIsEmpty(string value) => string.IsNullOrEmpty(value);

    private static ObjectResult Refuse(OperationError error, ControllerBase? controller = null) =>
        ApiService::CreatorPantry.ApiService.Http.ProblemResults.ProblemFor(controller ?? Controller(), error);

    private static ControllerBase Controller() => new StubController
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        ProblemDetailsFactory = new StubProblemDetailsFactory(),
    };

    private sealed class StubController : ControllerBase;

    /// <summary>
    /// MVC's own factory is internal, so this stands in for it: enough of a <see cref="ProblemDetails"/> for
    /// the mapping under test, and nothing else.
    /// </summary>
    private sealed class StubProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new() { Status = statusCode, Title = title, Detail = detail, Instance = instance, Type = type };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext,
            ModelStateDictionary modelStateDictionary,
            int? statusCode = null,
            string? title = null,
            string? type = null,
            string? detail = null,
            string? instance = null) =>
            new(modelStateDictionary)
            {
                Status = statusCode,
                Title = title,
                Detail = detail,
                Instance = instance,
                Type = type,
            };
    }
}
