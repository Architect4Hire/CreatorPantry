# AI Architecture and Safety

AI augments creator workflows; it does not become an alternate application architecture.

## Required boundaries

- Depend on `IChatClient` and `IEmbeddingGenerator`, not a provider SDK in domain code.
- Semantic Kernel plugins are thin adapters over facades.
- Plugins never call `DbContext`, repositories, or gateways and never accept a model-supplied workspace id.
- Prompts live as versioned files with declared inputs and outputs. The format is one embedded `.prompt.md`
  file per version, with a JSON front-matter manifest and a declared body checksum (B-16); see the
  `add-ai-capability` skill for the convention and `Managers/Prompts/` for the loader.
- Structured outputs use schemas and server validation.
- The model never emits executable SQL, file paths, or arbitrary provider commands.

## Deterministic work stays deterministic

Use code—not a model—for recipe scaling, unit and temperature conversion, percentages, date/time arithmetic, identifier resolution, authorization, quota enforcement, nutrition arithmetic, and publication state transitions. A model may interpret intent into a constrained command such as `{ "operation": "scaleRecipe", "targetServings": 14 }`; domain code performs the operation.

## Grounding

- Retrieve only data the caller may already read in the resolved workspace.
- Apply workspace filters to embedding storage and vector search.
- Treat recipe text, uploads, imported pages, provider data, and user instructions as untrusted prompt content.
- Delimit source material from system instructions and never allow retrieved text to redefine tool permissions.
- Cite or link the creator-owned source records used for claims where the interface benefits from traceability.

## Output lifecycle

- Generated output is a `Draft`, `Proposal`, or `GenerationArtifact` until accepted.
- Record model/provider, template version, input record references, timestamps, token usage, latency, status, safety flags, and acceptance/rejection outcome.
- Do not log private prompt bodies or generated creator content by default.
- Cancellation must stop streaming and mark the generation outcome accurately.
- Retrying a request must not create duplicate accepted artifacts.

## Food-domain cautions

- Never present medical, allergen, nutrition, preservation, or food-safety conclusions as guaranteed.
- Ingredient substitution advice distinguishes culinary plausibility from safety and dietary suitability.
- Alt text describes visible or supplied facts; it does not invent unseen ingredients, texture, doneness, or preparation steps.
- SEO assistance must not invent search volume, ranking, or competitor data without a connected source.

## Evaluation

Each capability has a small versioned evaluation set covering expected quality, refusal/safety cases, workspace isolation, prompt injection, schema failures, and deterministic-tool routing. A prompt change that materially affects behavior updates and runs the evaluation set.

