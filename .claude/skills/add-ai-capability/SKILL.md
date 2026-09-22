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
2. Create a versioned prompt file and typed input/output schema.
3. Implement through `IChatClient`/`IEmbeddingGenerator` and configured provider adapters.
4. If tools are needed, write thin Semantic Kernel plugins that call facades only.
5. Derive workspace/user context outside the model and retrieve only authorized data.
6. Delimit untrusted source text and keep tool permissions outside retrieved content.
7. Route scaling/conversion/arithmetic/date math to deterministic domain functions.
8. Persist `AiGeneration` and `GenerationArtifact` provenance, status, model, prompt version, input references, usage, latency, and acceptance outcome.
9. Support cancellation and idempotent retries.
10. Add an evaluation set covering quality, schema failure, injection, isolation, unsafe food claims, and deterministic routing.

## Capability cautions

- Recipe prose generation may not silently change quantities, temperatures, yield, timing, or canonical steps.
- Substitution advice distinguishes taste/texture behavior from allergen or health suitability.
- SEO output does not invent metrics.
- Image prompts and alt text do not alter recipe truth or describe unseen pixels as facts.
- Editorial planning does not publish.

Run `@ai-safety-reviewer`, `@workspace-isolation-auditor`, and focused evals before completion.

