---
name: add-ai-capability
description: Add grounded CreatorPantry generation, semantic search, or structured AI actions using application abstractions and safe workspace boundaries.
---

# Add an AI Capability

## The shared lifecycle every capability plugs into

Every recipe AI capability in this codebase (`src/CreatorPantry.Domain/Modules/Ai/`) is one more discriminator
on the same operation/proposal lifecycle, not a standalone feature with its own storage or its own accept/reject
plumbing. Extend these types; do not reinvent them:

- **`AiTaskType`** (`Managers/AiTaskType.cs`) — the enum member your capability adds, and **`AiTaskCatalog`**
  (`Managers/AiTaskCatalog.cs`) — the allow-list that maps a client-facing discriminator string to it, gates it
  behind deployment configuration (`AiTaskOptions`), and declares via `RequiresTaskInputs` whether it can run
  from the generic proposal route or needs its own request contract/route (see **Request intake** below).
- **`IAiTaskHandler`** (`Managers/AiTaskHandler.cs`) — what you implement. Registered keyed by discriminator and
  dispatched by `AiOperationWorker` after it claims a workspace-validated operation. Returns an
  `AiTaskHandlerOutcome` — a stored `AiProposal` or a typed failure — never anything else.
- **`AiOperation`** (`Data/Entities/AiOperation.cs`) — the workspace-owned aggregate root: status
  (`AiOperationStatus`: Requested→Running→Proposed→Accepted/PartiallyAccepted/Rejected/Failed/Expired, governed
  by `AiOperationTransitionPolicy`), scope, the pinned recipe/version when there is one, lease/claim fields for
  the worker.
- **`AiProposal`** + **`AiStructuredChange`** + **`AiWarning`** — what a handler produces, assembled by
  **`AiProposalAssembler.Assemble(...)`** (see **Structured output** below) — never constructed by hand. Each
  `AiStructuredChange` carries an **`AiChangeDisposition`** (Pending/Accepted/Rejected), set only by the shared
  disposition flow (`AiDraftAcceptanceBusiness`, `AiProposalsController`), never by a handler.

There is no `AiGeneration` or `GenerationArtifact` entity in this codebase — that was an earlier design the
`AiOperation`/`AiProposal` pair superseded. If you see either name in an older doc or comment, it is stale.

## Choose the shape

- **Generation:** grounded source → draft artifact (e.g. AIREC-001 concepts, AIREC-002 first draft).
- **Refinement:** existing draft + requested changes → proposed revision/diff (e.g. AIREC-003 revision,
  AIREC-005 adaptation).
- **Advisory analysis:** a recipe read, changed nowhere → structured findings/advice a creator judges themselves
  (e.g. AIREC-004 substitution, AIREC-006 review). Runs at `AiOperationScope.Advisory`, fixed server-side, never
  chosen by the client, and its `AiChangeTargetKind` is deliberately absent from `AiChangeApplicability` so
  there is no code path from a stored row to a recipe edit.
- **Deterministic explanation of existing data, no model call:** a read projection over another operation's
  already-stored `AiProposal`/`AiStructuredChange`/`AiWarning` rows (AIREC-008's `AiProposalExplanationAiTaskHandler`
  is the only example today). Skip the prompt-template, provider-call, and prompt-injection guidance below
  entirely — none of it applies to a handler that never calls `IAiCompletionGateway`.
- **Semantic search:** authorized workspace/reference corpus → ranked records.
- **Tool use:** model invokes a constrained facade-backed function.
- **Natural-language command:** text → validated command → preview → explicit confirmation → deterministic write.

## Request intake

A capability that a client requests directly (i.e. not Tool use/NL command, which enter through a plugin) needs
a `Modules/Ai/Facade/<Capability>RequestFacade.cs` and a `Modules/Ai/Business/<Capability>RequestBusiness.cs`,
matching every existing capability (e.g. `AiConceptRequestFacade`/`AiConceptRequestBusiness`,
`AiSubstitutionRequestFacade`/`AiSubstitutionRequestBusiness`) — the ordinary `Controller → Facade → Business →
DataLayer` seam (backend.md), not a shortcut that calls the gateway or the worker's plumbing directly.

Decide up front whether the generic `POST /recipes/{recipeId}/ai-proposals` route can carry your request:
tolerable when the task needs no field beyond scope and a source version, and the client may reasonably choose
the scope. Add your task to `AiTaskCatalog.RequiresTaskInputs` (and give it its own controller/route/ViewModel)
the moment either is false — a capability-specific field with nowhere to travel, or a scope that must be fixed
server-side. Every capability implemented after AIREC-003 needed its own route for exactly one of those two
reasons; read the remarks on `RequiresTaskInputs` before assuming the generic route is enough.

## Required workflow

1. Define user value, authoritative sources, prohibited claims, and acceptance workflow.
2. Add the `AiTaskType` member and its `AiTaskCatalog` discriminator/route decision (see **Request intake**).
3. Create a versioned prompt file and typed input/output schema. See **Writing a prompt template** below. Skip
   this and the next two steps entirely for a no-model-call capability (see **Choose the shape**).
4. Implement through `IChatClient`/`IEmbeddingGenerator` and configured provider adapters, via
   `IAiCompletionGateway` only (see **Calling the provider**).
5. If tools are needed, write thin Semantic Kernel plugins that call facades only.
6. Derive workspace/user context outside the model and retrieve only authorized data.
7. Delimit untrusted source text and keep tool permissions outside retrieved content.
8. Route scaling/conversion/arithmetic/date math to deterministic domain functions.
9. Implement `IAiTaskHandler.HandleAsync`, translate your validated answer into `AiResolvedChange` rows, and
   call `AiProposalAssembler.Assemble(...)` — this is what persists provenance, status, model, prompt version,
   token usage, latency, and (via the shared disposition flow) acceptance outcome. Do not invent a parallel
   persistence path.
10. Support cancellation and idempotent retries.
11. Add an evaluation set (see **Writing an evaluation fixture** below) covering schema failure, injection,
    isolation, unsafe food claims, and deterministic routing. See that section for what "quality" does and does
    not mean here.

## Writing a prompt template

One file per version, embedded (B-16). Put it beside the module that owns it —
`Modules/<Context>/Prompts/<id>-<version>.prompt.md` — and the `.prompt.md` suffix is what the loader keys on;
`CreatorPantry.Domain.csproj` already globs them as `EmbeddedResource`.

```markdown
---
{
  "id": "recipe.concepts",
  "version": "1.0.0",
  "outputSchemaVersion": "recipe.concepts.v1",
  "safetyClass": "CulinaryAdvice",
  "inputs": [
    { "name": "recipeTitle", "required": true, "description": "Creator-entered title, as entered." }
  ],
  "bodyChecksum": "sha256:<64 lowercase hex>"
}
---
Propose three variations on {{recipeTitle}}.
```

The body carries **task instructions only** — no system policy, no retrieved references, no untrusted text.
Those are segments the context envelope assembles around it, and it owns the delimiting.

`safetyClass` is required and has no default. Pick the caution that applies: `None`, `CulinaryAdvice`,
`DietaryOrAllergen`, `NutritionEstimate`, `FoodSafety`.

`bodyChecksum` is SHA-256 over the body, LF-normalized with trailing whitespace removed. The loader refuses a
mismatch, so a body edit and a manifest edit always land in the same diff. To get the value, write the template
with a wrong checksum and read the correct one out of the failure message — it states what the body hashes to —
or call `PromptTemplateFile.ComputeChecksum`. **If the change alters behaviour, bump `version` too**: the
version is what a stored generation records, and a body that changed underneath an unchanged version makes
every earlier proposal unexplainable.

The loader rejects, at startup: a placeholder no input declares, an input the body never uses, an unclosed or
whitespaced `{{ }}`, a file name that disagrees with its manifest, a mistyped manifest property, an unknown or
absent `safetyClass`, and two files claiming one id and version. Several versions of one id may coexist —
`Get(id)` takes the highest, `Get(id, version)` takes an exact one.

## Structured output

**First decide: is your answer a diff against an existing recipe, or does it originate/analyze something that
isn't a recipe edit?** The two are held to different contracts, and picking wrong means force-fitting a shape
that doesn't describe your answer or silently reinventing validation from scratch.

**Diff-shaped** (revision, adaptation — a change to one pinned recipe version): reuse the generic contract.
`AiOutputDocument` in the AI module's `Managers/` is the shape every diff-shaped answer is held to, and it *is*
the schema — `AiOutputSchema.Json` exports it for the prompt, so the shape a model is asked for and the shape it
is judged against are one definition. Put the exported schema in the template body rather than describing it in
prose. Validate with `AiOutputValidator.Validate(payload, expectedSchemaVersion, scope)` (or pass
`AiOutputValidator.AsDelegate` as the gateway call's `Validate` argument — see **Calling the provider**). It
returns a typed document or an `AiOutputFailure` carrying a stable `AiOutputReason` code, a sanitized message,
and whether a bounded re-ask could fix it.

**Origination or analysis-shaped** (concepts, first draft, substitution, review — nothing to diff against, or a
finding/advice shape a recipe diff can't express): define your own triad, mirroring an existing one exactly
(e.g. `AiSubstitutionOutputDocument`/`AiSubstitutionOutputSchema`/`AiSubstitutionOutputValidator`, or
`AiRecipeReviewOutputDocument`/`...OutputSchema`/`...OutputValidator`) — same reject-don't-repair discipline,
same stable `AiOutputReason` codes, your own domain rules. Your handler then translates the validated answer
into `AiResolvedChange` rows itself (see `RecipeConceptsAiTaskHandler.Translate` or
`IngredientSubstitutionAiTaskHandler.Translate` for the pattern) and still calls
`AiProposalAssembler.Assemble(...)` — which always takes an `AiOutputDocument`, so pass a bare
`new AiOutputDocument { SchemaVersion = template.OutputSchemaVersion, Warnings = warnings }` carrying only the
warnings, alongside your own `IReadOnlyList<AiResolvedChange>` diff. The assembler doesn't care which path
produced either input.

Either way: nothing is repaired. A truncated or mis-shaped answer fails; it is never patched into something the
creator never reviewed and the model never said. An **empty** answer is valid — "nothing needs changing" is a
result.

Extending either contract means editing the document type, which changes the exported schema, which means
bumping the template's `outputSchemaVersion` — a template and an answer that disagree on version fail before
the body is even examined. Extending the *origination/analysis* path also means adding a matching
`AiEvaluationKind` and case-runner class for its own `SchemaValidity` fixtures — see **Writing an evaluation
fixture**.

## Building the prompt

Never concatenate a prompt by hand. `PromptEnvelopeBuilder` puts instructions in the system message and data
in the user message, fences every segment with a per-envelope token, and refuses content from another
workspace:

```csharp
var envelope = new PromptEnvelopeBuilder(workspace.Id)
    .WithTask(template.Render(inputs))
    .WithOutputSchema(AiOutputSchema.Json)
    .WithSource(workspace.Id, snapshotJson)          // where the content was READ from
    .AddReference(workspace.Id, retrievedPassage)    // retrieval is where leaks originate
    .WithUntrustedText(workspace.Id, creatorRequest)
    .Build();
```

The workspace argument on each is not ceremony — it is the last place a retrieval bug that returned a
neighbour's row can be caught before the content reaches a model.

Trust comes from the segment kind, so there is no argument to get wrong. Creator material is `CreatorData`,
never `Instruction`: a headnote can carry an injection as easily as an import can. Log only
`envelope.Describe()`, which carries names, trust levels and sizes — never content, the nonce, or a workspace
id.

**These are structural guarantees, not behavioural ones.** The envelope ensures untrusted bytes arrive inside
an untrusted fence and nowhere else. Whether a model declines an instruction it finds there is a question for
the evaluation set, and a green test suite is not an answer to it.

## Calling the provider

Never call `IChatClient` directly from a capability. `IAiCompletionGateway` is the only place a model is
invoked: it times the call, classifies what came back, retries what is worth retrying and nothing else, and
returns a validated `AiOutputDocument` plus one `AiAttemptRecord` per provider call for you to persist as
`AiExecutionMetadata`.

```csharp
var outcome = await gateway.CompleteAsync(
    new AiCompletionRequest<AiOutputDocument>(
        envelope, template.OutputSchemaVersion, context.Scope,
        template.Id, template.Version.ToString(), context.CorrelationId,
        AiOutputValidator.AsDelegate),
    cancellationToken);
```

`AiCompletionRequest<TDocument>` is generic in the document type and takes the validator as its last argument —
`AiOutputValidator.AsDelegate` for a diff-shaped capability, your own `Ai<Capability>OutputValidator.Validate`
otherwise (see **Structured output**). `context` here is the `AiTaskExecutionContext` your `HandleAsync`
receives, not a local you construct.

Persist **every** record in `outcome.Attempts`, not just the last: a retried transport failure and a corrected
schema failure are separate rows, and an attempt with `WasSchemaCorrection` set came from the template *plus* a
correction, which provenance must not misreport as the template alone.

Do not add your own retry. Transport retries live in the ServiceDefaults pipeline; a schema failure gets exactly
one corrective re-ask; a domain failure gets none. Adding another layer multiplies attempts against a creator's
budget.

Register the provider-specific `IAiFailureClassifier` before `AddAiModule`. The domain fallback cannot read an
SDK's status code, so with it a rate limit and a safety block both look like generic transient faults — and a
safety block that is retried is the failure `ai.md` specifically forbids.

## Writing an evaluation fixture

The versioned evaluation set step 11 asks for has a working precedent: `src/CreatorPantry.Tests/Ai/Evaluation/`
runs a small harness of fixtures against the AI foundation's own deterministic machinery, using fakes only —
never a network call, a provider credential, or a paid model.

One JSON file per fixture, `Fixtures/<Category>/<id>-<version>.eval.json`, embedded the same way a prompt
template is. Required fields: `id`, `version` (`major.minor.patch`, reusing `PromptTemplateVersion`),
`category`, `kind`, `description`, `input`, `expect`. `AiEvaluationFixtureStore` loads and validates every
fixture at test-collection time, fail-fast, the same way `EmbeddedPromptTemplateStore` does for prompts.

`kind` picks which case runner the fixture's `input`/`expect` shape is written for. There are nine today
(`AiEvaluationKind` in `AiEvaluationFixture.cs`), not four — the count grew every time a capability took the
origination/analysis-shaped path in **Structured output** above, and it will grow again the next time one does:

- `OutputValidation` — `AiOutputValidator.Validate` directly. Schema-validity and schema-failure fixtures for
  any **diff-shaped** capability (revision, adaptation) are new `.eval.json` files here, no new C#: supply a
  `payload`, `expectedSchemaVersion` and `scope`, and assert the `reasonCode`/`category` a rejection carries (or
  that a sound answer validates).
- `ConceptOutputValidation`, `RecipeDraftOutputValidation`, `SubstitutionOutputValidation`,
  `AdaptationOutputValidation`, `RecipeReviewOutputValidation` — one per capability that defined its own
  document/validator triad, each running that capability's own `Ai<Capability>OutputValidator.Validate`
  directly. **A new origination/analysis-shaped capability needs a new kind and a new case-runner class here,
  modeled on an existing one (e.g. `SubstitutionOutputValidationCase.cs`) — not just a new `.eval.json` file.**
  This is the most commonly missed step: it is easy to add fixtures under an existing kind and end up validating
  nothing, because that kind's case runner calls a different capability's validator.
- `PromptEnvelope` — `PromptEnvelopeBuilder` directly. Proves untrusted text lands fenced in the user message
  and never in the system message — structural containment only. It does not and cannot prove a model declines
  an injected instruction; that is behaviour a fixture against a fake provider cannot demonstrate.
- `ProposalAssembly` — `AiDiffCalculator` + `AiProposalAssembler` together, over a hand-built snapshot and
  answer, no persistence. Stale-source and proposal-acceptance fixtures for any capability plug in here as new
  `.eval.json` files too, since both are generic over an arbitrary `AiOutputChange` list.
- `WorkerOperation` — the full SQLite-backed `IAiOperationWorker` pipeline, against a fake `IChatClient` when a
  scenario needs one. Proves workspace isolation and provider-classification behaviour (e.g. a safety block is
  recorded once and never retried). **This is the one kind with no generic fixture path today**: proving a
  specific capability's real `IAiTaskHandler`, calling the gateway with its own real prompt-template id, needs
  a new hand-written case modeled on `WorkerOperationCase.cs`, not just a new fixture file. A no-model-call
  capability (AIREC-008's shape) has nothing for this kind to prove and can skip it.

**"Expected quality" has no fixture kind.** Every case above is structural/deterministic — schema shape,
envelope fencing, diff math, workspace scoping — against fakes, and none judges whether generated prose is
actually good, on-topic, or non-hallucinated. That is a real, open gap, not an oversight: judge quality some
other way (a human review pass, or an LLM-judge step outside this harness) and do not assume a passing
`.eval.json` set has covered it.

## Capability cautions

- Recipe prose generation may not silently change quantities, temperatures, yield, timing, or canonical steps.
- Substitution advice distinguishes taste/texture behavior from allergen or health suitability.
- SEO output does not invent metrics.
- Image prompts and alt text do not alter recipe truth or describe unseen pixels as facts.
- Editorial planning does not publish.

Run `@ai-safety-reviewer`, `@workspace-isolation-auditor`, and focused evals before completion.

