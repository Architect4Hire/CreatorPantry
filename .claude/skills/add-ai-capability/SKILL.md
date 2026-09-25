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

## Capability cautions

- Recipe prose generation may not silently change quantities, temperatures, yield, timing, or canonical steps.
- Substitution advice distinguishes taste/texture behavior from allergen or health suitability.
- SEO output does not invent metrics.
- Image prompts and alt text do not alter recipe truth or describe unseen pixels as facts.
- Editorial planning does not publish.

Run `@ai-safety-reviewer`, `@workspace-isolation-auditor`, and focused evals before completion.

