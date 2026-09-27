using System.Text.Json;
using CreatorPantry.Domain.Managers.Prompts;

namespace CreatorPantry.Tests.Ai.Evaluation;

/// <summary>The property this task's own SCOPE line names, and what a fixture demonstrates about it.</summary>
public enum AiEvaluationCategory
{
    SchemaValidity,
    RefusalSafety,
    WorkspaceIsolation,
    PromptInjection,
    StaleSource,
    DeterministicToolRouting,
    ProposalAcceptance,
}

/// <summary>Which pipeline entry point a fixture's <c>input</c>/<c>expect</c> shape is written for.</summary>
public enum AiEvaluationKind
{
    /// <summary><see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiOutputValidator.Validate"/> directly.</summary>
    OutputValidation,

    /// <summary><see cref="CreatorPantry.Domain.Modules.Ai.Managers.PromptEnvelopeBuilder"/> directly.</summary>
    PromptEnvelope,

    /// <summary>
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiDiffCalculator"/> and
    /// <see cref="CreatorPantry.Domain.Modules.Ai.Managers.AiProposalAssembler"/> together.
    /// </summary>
    ProposalAssembly,

    /// <summary>The full SQLite-backed <c>IAiOperationWorker</c> pipeline, against a fake provider.</summary>
    WorkerOperation,
}

/// <summary>
/// One versioned evaluation fixture: a scenario against the AI foundation, and the outcome it must produce.
/// </summary>
/// <remarks>
/// Reuses <see cref="PromptTemplateVersion"/> for <see cref="Version"/> rather than inventing a parallel
/// major.minor.patch type — a fixture is a versioned artifact loaded at startup for the same reason a prompt
/// template is, and the two should not drift into two ways of spelling the same concept.
/// </remarks>
public sealed record AiEvaluationFixture(
    string Id,
    PromptTemplateVersion Version,
    AiEvaluationCategory Category,
    AiEvaluationKind Kind,
    string Description,
    JsonElement Input,
    JsonElement Expect)
{
    public string Identity => $"{Id}-{Version}";
}

/// <summary>A fixture that cannot be trusted to describe a scenario: malformed, mis-declared, or duplicated.</summary>
/// <remarks>
/// Mirrors <see cref="PromptTemplateException"/>'s reasoning exactly: a fixture is read once, at harness
/// start, so a bad one is a fail-fast authoring error, not a per-run runtime condition.
/// </remarks>
public sealed class AiEvaluationException : Exception
{
    public AiEvaluationException(string origin, string message)
        : base($"AI evaluation fixture '{origin}': {message}") => Origin = origin;

    public string Origin { get; }
}
