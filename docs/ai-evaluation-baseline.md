# AI Evaluation Baseline

*Status: Accepted — 2026-09-27. Recorded by microprompt 8.15 ("AI foundation audit and evaluation harness").*

This record establishes the first versioned AI evaluation set `.claude/rules/ai.md`'s Evaluation section
requires, and the harness that runs it. No product code is decided here; this is a status snapshot, the same
kind `docs/architecture-decisions/baseline.md` keeps for B-numbered decisions.

## Why this baseline is scoped to the foundation, not a capability

`.claude/rules/ai.md` requires each *capability* to carry its own evaluation set. At the time this baseline
was recorded, no real generation capability exists yet: `AiTaskType` has only the inert `Diagnostic` member,
and no `.prompt.md` template exists anywhere in the repository. There is nothing capability-shaped to evaluate.

What does exist, and what a future capability will be built on, is the shared AI foundation: `AiOutputValidator`,
`PromptEnvelopeBuilder`, `AiDiffCalculator`/`AiProposalAssembler`, and the `IAiOperationWorker` pipeline. This
baseline's fixtures — all namespaced `foundation.*` — exercise that shared machinery directly. When a real
capability arrives, it adds its own `<capability-id>.*`-namespaced fixtures beside its prompt template,
following the same format; see the `add-ai-capability` skill's **Writing an evaluation fixture** section.

## The requirement citations this task names are undefined in the repository

This task's own CONSTRAINT cites **AI-012** and **TEST-003/004/010**. Neither exists as recorded requirement
text anywhere in this repository — they appear only as bare id citations in
`docs/prompts/creatorpantry-scrub-microprompts.md`, never as a defined requirement body. This is the same gap
`docs/architecture-decisions/baseline.md`'s **B-12** records for AI-001 through AI-011 and DEC-001–012: "a
prompt that meets an unrecorded product choice stops and asks instead of choosing." Rather than inventing
requirement text for an undefined id, this baseline treats `.claude/rules/ai.md`'s Evaluation paragraph and
the microprompt's own SCOPE/RESTRICTION/BEHAVIOR text as the operative spec — the same resolution B-16/B-18/
B-19/B-20 already apply to their own undefined citations.

## The harness

`src/CreatorPantry.Tests/Ai/Evaluation/` — fixtures, loader, and four case runners (`OutputValidation`,
`PromptEnvelope`, `ProposalAssembly`, `WorkerOperation`), each dispatching a fixture's `input`/`expect` JSON to
the real deterministic domain code it names, never a reimplementation of it. Every fixture runs against fakes
only — an in-memory SQLite database, a scripted `IChatClient` — never a network call, a provider credential,
or a paid model, satisfying this task's own RESTRICTION. Format, loader mechanics, and the four kinds are
documented in the `add-ai-capability` skill so a future capability author has a followable convention rather
than four internal C# files to reverse-engineer.

Each fixture runs as its own named `[Theory]` case in the ordinary `dotnet run --project src/CreatorPantry.Tests`
pipeline — no separate command to remember, no opt-in flag to forget.

## Fixture inventory

| Id | Category | Kind | Version |
| --- | --- | --- | --- |
| `foundation.schema-validity.unknown-field` | SchemaValidity | OutputValidation | 1.0.0 |
| `foundation.schema-validity.empty-changes-is-valid` | SchemaValidity | OutputValidation | 1.0.0 |
| `foundation.schema-validity.malformed-json` | SchemaValidity | OutputValidation | 1.0.0 |
| `foundation.schema-validity.schema-version-mismatch` | SchemaValidity | OutputValidation | 1.0.0 |
| `foundation.refusal-safety.outside-scope` | RefusalSafety | OutputValidation | 1.0.0 |
| `foundation.refusal-safety.provider-safety-block` | RefusalSafety | WorkerOperation | 1.0.0 |
| `foundation.prompt-injection.untrusted-text-stays-fenced` | PromptInjection | PromptEnvelope | 1.0.0 |
| `foundation.deterministic-routing.no-before-value-field` | DeterministicToolRouting | OutputValidation | 1.0.0 |
| `foundation.stale-source.recipe-moved-on` | StaleSource | ProposalAssembly | 1.0.0 |
| `foundation.proposal-acceptance.valid-diff-assembles` | ProposalAcceptance | ProposalAssembly | 1.0.0 |
| `foundation.proposal-acceptance.numeric-field-round-trips` | ProposalAcceptance | ProposalAssembly | 1.0.0 |
| `foundation.workspace-isolation.operation-not-visible-cross-workspace` | WorkspaceIsolation | WorkerOperation | 1.0.0 |

12 fixtures across all 7 categories this task's SCOPE names. `AiEvaluationHarnessTests.Every_category_has_at_least_one_fixture`
and `The_fixture_store_loaded_every_known_fixture` make both claims self-enforcing rather than a manual audit
item — a future fixture that fails to load, or a category that loses its last fixture, fails the build.

## Reviewer pass

Per this task's RESTRICTION ("reviewers report before fixes"), five reviewers ran read-only against the
implementation before any fix was made: `ai-safety-reviewer`, `workspace-isolation-auditor`,
`architecture-reviewer`, `test-gap-analyzer`, `skills-evals`. **No reviewer found a BLOCKER.** What was found
and how it was resolved:

| Finding | Reviewer | Resolution |
| --- | --- | --- |
| The safety-block fixture's "never retried" claim wasn't actually exercised — the test's own resilience pipeline had no retry strategy configured at all, so the claim held regardless of classification. | ai-safety-reviewer | **Fixed.** `WorkerOperationCase.BuildGateway` now wires a retry strategy mirroring `AddAiResilience`'s own `ShouldHandle` predicate exactly; the fixture now proves the safety-refused exception is excluded from retry by classification, not by the pipeline's inability to retry anything. |
| The fixture parser (`AiEvaluationFixtureFile`) had zero dedicated failure-mode tests, unlike the prompt-template loader it mirrors. | test-gap-analyzer | **Fixed.** Added `AiEvaluationFixtureFileTests.cs` (one case per `Require*` branch, malformed JSON, a mistyped property, filename/manifest mismatch) and `AiEvaluationFixtureStoreTests.cs` (duplicate id+version, an empty-matching filter), mirroring `PromptTemplateFileTests`/`PromptTemplateStoreTests`. |
| Two `switch`-default `throw` branches (an unrecognized `expect.outcome`, an unrecognized worker scenario) were untested dead code. | test-gap-analyzer | **Fixed.** Added `AiEvaluationCaseRobustnessTests.cs` exercising both directly. |
| Reason-code coverage was thin (2 of 16 `AiOutputReason` values exercised); no malformed-payload or schema-version-mismatch fixture existed despite those being the most likely real-world failure shapes; `ProposalAcceptance` had only one shape (a text field) despite recipes.md's emphasis on quantity/yield/time/temperature fields. | test-gap-analyzer | **Fixed.** Added `foundation.schema-validity.malformed-json`, `foundation.schema-validity.schema-version-mismatch`, and `foundation.proposal-acceptance.numeric-field-round-trips` (a `yieldQuantity` change, proving the before/after values round-trip as plain invariant-culture numbers, not text the model composed). |
| An `Expect` record with an omitted `reasonCode` could pass a `Rejected`/`Refused` fixture regardless of *why* the rejection happened, silently weakening the regression check. | test-gap-analyzer | **Fixed.** `OutputValidationCase` and `ProposalAssemblyCase` now throw `AiEvaluationException` at run time if a fixture declares `Rejected`/`Refused` with no `reasonCode` — a fixture-authoring mistake is caught, not silently accepted. |
| `add-ai-capability`'s evaluation checklist item was one bare bullet with no location, format, or worked example, unlike the fully-documented prompt-template workflow beside it. | skills-evals | **Fixed.** Added the **Writing an evaluation fixture** section to `add-ai-capability/SKILL.md` and a pointer sentence in `ai.md`'s Evaluation section (see both files). |
| No fixture kind proves an arbitrary capability's real `IAiTaskHandler`/prompt-id/gateway call end to end — only the inert `Diagnostic` handler is exercised; "expected quality" has no fixture kind at all. | skills-evals | **Documented, not fixed.** Both are real, structural gaps rather than defects — see **Known gaps**, below, and the explicit disclosure now in `SKILL.md`'s new section. |
| The fixture *format* (loader, no test-double dependency) arguably belongs in `CreatorPantry.Domain`'s shared kernel beside `Managers/Prompts/`, which it closely mirrors and already borrows `PromptTemplateVersion` from; the four case *runners* correctly stay in Tests since they need test doubles. | architecture-reviewer | **Deferred.** Advisory, not a defect — the reviewer's own words: "if no such [non-test] caller is actually planned, the current all-in-Tests placement is defensible as-is." No such caller exists today. Revisit if the harness ever needs a caller outside the test assembly (e.g. a baseline-report generator run as a standalone tool). |
| `WorkerOperationCase.cs` is the third file to duplicate the SQLite/DI-seeding scaffold and a throwing-`IChatClient` fake (after `AiOperationWorkerTests.cs` and `AiCompletionGatewayTests.cs`). | architecture-reviewer | **Deferred.** Test-hygiene suggestion, not a boundary violation — extracting a shared `AiTestFixtures.cs` would also touch the two pre-existing files, which is broader than this task's scope. Worth doing the next time any of the three files changes. |
| A food-safety-content refusal fixture (distinct from the generic domain/out-of-scope and provider-refusal cases already covered) would round out RefusalSafety per recipes.md's Safety and claims section. | test-gap-analyzer | **Not added.** No `AiOutputValidator` rule for food-safety-specific content exists yet to exercise — per the reviewer's own caution, faking a fixture for a rule that doesn't exist would test nothing real. Add it once such a rule (or a real capability that needs one) exists. |
| A pre-existing (not newly-added) `IgnoreQueryFilters()` use in `AiOperationWorkerTests.cs`'s `LoadAsync` test helper, noted for completeness. | workspace-isolation-auditor | **Out of scope.** Predates this task, does not weaken the new harness's isolation proof, not touched here. |

## Pass rate at this baseline

- `dotnet run --project src/CreatorPantry.Tests -- --filter-class "CreatorPantry.Tests.Ai.Evaluation.*"` — 34/34 passing (12 fixture cases plus loader/case-runner unit tests).
- `dotnet run --project src/CreatorPantry.Tests` (full suite) — 2771/2771 passing.
- `dotnet build` (whole solution) — 0 errors.

100% at baseline, as a merged baseline should be. A prompt or foundation change that materially affects
behavior updates and re-runs this set, per `ai.md`.

## Known gaps (forward guidance, not defects)

1. **No fixture kind proves a real capability's end-to-end path** — its own `IAiTaskHandler`, its own real
   prompt-template id, a scripted answer routed through its actual gateway call. `WorkerOperationCase` today
   only exercises the inert `Diagnostic` handler. A future capability author writes a new case modeled on
   `WorkerOperationCase.cs`; the structural kinds (`OutputValidation`, `PromptEnvelope`, `ProposalAssembly`)
   already reuse cleanly with no new C#, only new `.eval.json` files.
2. **"Expected quality" is not mechanized here and cannot be**, against fakes alone. A future capability needs
   a separate quality-grading step (human review, or an LLM-judge pass) outside this structural harness.
3. **Prompt-injection coverage proves structural containment only** — that untrusted text stays fenced and out
   of the system message. It does not and cannot prove a model declines an instruction it finds there; that is
   behaviour no fixture against a fake provider can demonstrate.
