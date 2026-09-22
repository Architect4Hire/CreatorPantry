---
name: add-recipe-feature
description: Implement CreatorPantry recipe editing, versioning, ingredients, steps, scaling, conversion, or related domain behavior.
---

# Add a Recipe Feature

1. Identify the canonical recipe/version fields affected and whether the operation creates a draft, accepted edit, or immutable version.
2. Preserve creator-entered ingredient and instruction text. Normalized ingredient/unit references are additive.
3. Model ordering, grouping, optionality, preparation notes, yields, time, temperatures, equipment, storage, and substitutions explicitly where relevant.
4. Put arithmetic and conversions in deterministic Business/domain services with documented rounding and non-scalable flags.
5. Use optimistic concurrency for collaborative editing and return recoverable conflicts.
6. When a canonical version changes, mark linked derivatives `NeedsReview`; do not rewrite them silently.
7. Treat dietary/allergen/nutrition/safety metadata as sourced claims with uncertainty, not guarantees.
8. Add workspace isolation, version immutability, concurrency, rounding, unit-dimension, and affected-derivative tests.

For AI involvement, also use `add-ai-capability`: output is a proposal/diff and cannot silently change recipe facts while rewriting prose.

