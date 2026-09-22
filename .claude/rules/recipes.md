# Recipe Domain

`Recipe` is workspace-owned intellectual property and the canonical source for recipe facts.

## Model principles

- Preserve creator-entered titles, ingredient lines, instructions, headnotes, and notes.
- A recognized `Ingredient` reference enriches a `RecipeIngredient`; it never replaces the entered display text.
- Store quantity, unit, preparation note, optionality, grouping, and display text separately when known.
- Steps have stable identifiers and explicit ordering.
- Model prep, cook, rest, total time, yield, serving unit, equipment, storage, substitutions, and notes explicitly when required.
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

