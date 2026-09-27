# Recipe Domain

`Recipe` is workspace-owned intellectual property and the canonical source for recipe facts.

## Model principles

- Preserve creator-entered titles, ingredient lines, instructions, headnotes, and notes.
- A recognized `Ingredient` reference enriches a `RecipeIngredient`; it never replaces the entered display text.
- Keep the creator's own words for a line's ingredient name and unit (`IngredientNameText`, `UnitText`) whether or
  not either matched the vocabulary. Nothing matched means no id to store, so those spans are then the only
  record of what the creator called it.
- A line records where its display text came from (`DisplayTextSource`: `Creator` or `Composed`). An editor that
  offers a creator fields and no line control has to assemble a line from those fields, and may keep it in step
  with them — only for a line marked `Composed`, and only out of the creator's own words. A line they wrote or
  pasted stays verbatim. Recorded rather than inferred: a line that happens to read exactly like its own spans
  may still be one they typed, and re-deriving it would be the rewrite this rule forbids.
- Grouping is opt-in. A recipe has one ingredient list and one method until the creator asks for sections. The
  untitled group each travels in is a wire requirement — lines submit inside an `IngredientGroupInput` and steps
  inside an `InstructionGroupInput` — not a heading to show them.
- Store quantity, unit, preparation note, optionality, grouping, and display text separately when known.
- Steps have stable identifiers and explicit ordering.
- Model prep, cook, rest, total time, yield, serving unit, equipment, storage, substitutions, and notes explicitly when required.
- A yield has three separable facts and the model keeps them separate: the creator's own wording (`YieldText`,
  canonical), the measured batch (`YieldQuantity` in `YieldUnitId`), and the servings (`ServingCount`, unitless,
  plus `ServingSize` in the same yield unit). "Makes 2 loaves, serves 12" needs all three; a recipe that knows
  only how many it serves records `ServingCount` alone, with no unit and no batch.
- Total time is stored independently of prep, cook and rest, and no write path sums them — except the recipe
  editor, which derives it, offers no control for it, and says before a save that it will replace a stored
  total that disagrees.
- Explicitly published or named `RecipeVersion` records are immutable.

## Scaling and conversions

- Domain code performs arithmetic with decimal/rational-friendly representations and documented rounding.
- Preserve non-scalable language such as “to taste,” package sizes, pan constraints, and discrete items as review flags.
- Unit conversion distinguishes mass, volume, count, and temperature; never convert across dimensions without ingredient-specific density data.
- A scaled recipe is a proposal until accepted or saved as a version.

## Safety and claims

- Dietary tags and allergen information are descriptive metadata, not guarantees.
- Do not infer allergen absence from missing data.
- Preservation, canning, fermentation, internal-temperature, pregnancy, and medical claims require vetted references or an explicit caution.
- Nutrition values record method and source; AI estimation alone is labeled as an estimate.

## AI edits

AI receives the selected version plus explicit requested changes and returns a structured proposal/diff. It may not silently change quantities, yield, times, temperatures, or safety-critical instructions while generating prose derivatives.

