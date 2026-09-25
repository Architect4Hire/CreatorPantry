---
name: add-ai-capability
description: Add grounded CreatorPantry generation, semantic search, or structured AI actions using application abstractions and safe workspace boundaries.
---

# Add an AI Capability

## Choose the shape

- **Generation:** grounded source → draft artifact.
- **Refinement:** existing draft + requested changes → proposed revision/diff.
- **Semantic search:** authorized workspace/reference corpus → ranked records.
- **Tool use:** model invokes a constrained facade-backed function.
- **Natural-language command:** text → validated command → preview → explicit confirmation → deterministic write.

## Required workflow

1. Define user value, authoritative sources, prohibited claims, and acceptance workflow.
2. Create a versioned prompt file and typed input/output schema. See **Writing a prompt template** below.
3. Implement through `IChatClient`/`IEmbeddingGenerator` and configured provider adapters.
4. If tools are needed, write thin Semantic Kernel plugins that call facades only.
5. Derive workspace/user context outside the model and retrieve only authorized data.
6. Delimit untrusted source text and keep tool permissions outside retrieved content.
7. Route scaling/conversion/arithmetic/date math to deterministic domain functions.
8. Persist `AiGeneration` and `GenerationArtifact` provenance, status, model, prompt version, input references, usage, latency, and acceptance outcome.
9. Support cancellation and idempotent retries.
10. Add an evaluation set covering quality, schema failure, injection, isolation, unsafe food claims, and deterministic routing.

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

`AiOutputDocument` in the AI module's `Managers/` is the contract every model answer is held to, and it *is*
the schema — `AiOutputSchema.Json` exports it for the prompt, so the shape a model is asked for and the shape
it is judged against are one definition. Put the exported schema in the template body rather than describing
it in prose.

Validate with `AiOutputValidator.Validate(payload, expectedSchemaVersion, scope)`. It returns a typed document
or an `AiOutputFailure` carrying a stable `AiOutputReason` code, a sanitized message, and whether a bounded
re-ask could fix it. The raw payload is on neither path, which is what keeps model JSON out of Business and
out of the database — do not route around it by parsing a response anywhere else.

Nothing is repaired. A truncated or mis-shaped answer fails; it is never patched into something the creator
never reviewed and the model never said. An **empty** answer is valid — "nothing needs changing" is a result.

Extending the contract means editing the document type, which changes the exported schema, which means
bumping the template's `outputSchemaVersion` — a template and an answer that disagree on version fail before
the body is even examined.

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
    new AiCompletionRequest(envelope, template.OutputSchemaVersion, operation.Scope,
        template.Id, template.Version.ToString(), correlationId),
    cancellationToken);
```

Persist **every** record in `outcome.Attempts`, not just the last: a retried transport failure and a corrected
schema failure are separate rows, and an attempt with `WasSchemaCorrection` set came from the template *plus* a
correction, which provenance must not misreport as the template alone.

Do not add your own retry. Transport retries live in the ServiceDefaults pipeline; a schema failure gets exactly
one corrective re-ask; a domain failure gets none. Adding another layer multiplies attempts against a creator's
budget.

Register the provider-specific `IAiFailureClassifier` before `AddAiModule`. The domain fallback cannot read an
SDK's status code, so with it a rate limit and a safety block both look like generic transient faults — and a
safety block that is retried is the failure `ai.md` specifically forbids.

## Capability cautions

- Recipe prose generation may not silently change quantities, temperatures, yield, timing, or canonical steps.
- Substitution advice distinguishes taste/texture behavior from allergen or health suitability.
- SEO output does not invent metrics.
- Image prompts and alt text do not alter recipe truth or describe unseen pixels as facts.
- Editorial planning does not publish.

Run `@ai-safety-reviewer`, `@workspace-isolation-auditor`, and focused evals before completion.

