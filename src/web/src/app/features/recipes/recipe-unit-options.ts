import { CpComboboxOption } from '@creator-pantry/ui';

import {
  MEASUREMENT_DIMENSION_LABELS,
  MEASUREMENT_SYSTEM_LABELS,
  MEASUREMENT_SYSTEM_NAME_PREFIXES,
  MeasurementUnit,
} from '../../models/reference.models';

/**
 * The shared unit catalogue as a recipe surface holds it.
 *
 * One declaration for every picker that reads it, so "not read yet" and "cannot be read" mean the same thing
 * on the ingredient rows as they do on the yield field. The conversion and normalization panels keep their own,
 * because what they do about an unreadable catalogue is a different question.
 */
export type UnitCatalogueState =
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly units: readonly MeasurementUnit[] }
  | { readonly status: 'unavailable' };

/**
 * How a unit reads when a creator is writing a recipe rather than converting one.
 *
 * The catalogue names a unit for precision — `US cup`, because an imperial cup is a different volume — and that
 * is right where the exact unit is the subject, as in the conversion panels. In a picker it is noise: nobody
 * writing an ingredient line is choosing between cup systems, and a list of "US cup", "US fluid ounce", "US
 * pint" reads as though the app were foreign to its own recipes.
 *
 * So the region comes off the name and is said on the second line instead. Nothing is hidden and nothing is
 * renamed in the catalogue; the prefix is taken from {@link MEASUREMENT_SYSTEM_NAME_PREFIXES} rather than
 * matched by hand, so a unit the seeder names differently later is still handled.
 */
export function recipeUnitName(unit: MeasurementUnit): string {
  return withoutSystemPrefix(unit, unit.displayName);
}

/**
 * Whether a line's own word for its unit already reads as the catalogue unit it is linked to — its name or its
 * plural, ignoring case and the region prefix ("cups" for `US cup`). An abbreviation ("lb") or a model's
 * shorthand ("c.") does not, which is when a closed line reads the unit's name instead (`withRecipeUnitName`).
 */
export function recipeUnitWordMatches(unit: MeasurementUnit, written: string): boolean {
  const word = written.trim().toLowerCase();

  return [unit.displayName, unit.pluralName]
    .flatMap((name) => [name, withoutSystemPrefix(unit, name)])
    .some((name) => name.toLowerCase() === word);
}

/**
 * A line's words as a closed ingredient row reads them: the creator's line with its unit word said by the CreatorPantry
 * unit it is linked to — "2 lb potatoes" reads "2 pounds potatoes", "1 c. flour" reads "1 cup flour".
 *
 * Presentation only. The line itself is the creator's and is saved exactly as written (recipes.md); this is the reading
 * a list shows, so a recognised unit looks recognised. The line comes back as written when its word already names the
 * unit, or when the word cannot be found in it as a word of its own (it is never guessed at elsewhere in the line).
 *
 * Singular or plural follows the amount: one or less reads "cup", more — or a range, or an amount in words — "cups".
 */
export function withRecipeUnitName(
  line: string,
  written: string,
  unit: MeasurementUnit,
  amount: { readonly quantityText: string; readonly quantityUpper: number | null },
): string {
  const word = written.trim();
  if (word === '' || recipeUnitWordMatches(unit, word)) return line;

  const escaped = word.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const pattern = new RegExp(`(?<![\\p{L}\\p{N}])${escaped}(?![\\p{L}\\p{N}])`, 'iu');
  const name = amountIsPlural(amount) ? withoutSystemPrefix(unit, unit.pluralName) : recipeUnitName(unit);

  return line.replace(pattern, () => name);
}

const VULGAR_FRACTIONS: Readonly<Record<string, number>> = {
  '½': 1 / 2,
  '⅓': 1 / 3,
  '⅔': 2 / 3,
  '¼': 1 / 4,
  '¾': 3 / 4,
  '⅛': 1 / 8,
  '⅜': 3 / 8,
  '⅝': 5 / 8,
  '⅞': 7 / 8,
};

/** Whether an amount reads with a plural unit. Grammar only: nothing here is a recipe quantity anyone keeps. */
function amountIsPlural(amount: { readonly quantityText: string; readonly quantityUpper: number | null }): boolean {
  if (amount.quantityUpper !== null) return amount.quantityUpper > 1;

  const text = amount.quantityText.trim();
  if (text === '') return false;

  const value = amountValue(text);
  return value === null || value > 1;
}

function amountValue(text: string): number | null {
  const plain = Number(text);
  if (Number.isFinite(plain)) return plain;

  const mixed = /^(\d+)\s+(\d+)\/(\d+)$/.exec(text);
  if (mixed) return Number(mixed[1]) + Number(mixed[2]) / Number(mixed[3]);

  const fraction = /^(\d+)\/(\d+)$/.exec(text);
  if (fraction) return Number(fraction[1]) / Number(fraction[2]);

  const vulgar = /^(\d*)\s*([½⅓⅔¼¾⅛⅜⅝⅞])$/u.exec(text);
  if (vulgar) return Number(vulgar[1] || 0) + VULGAR_FRACTIONS[vulgar[2]];

  return null;
}

function withoutSystemPrefix(unit: MeasurementUnit, name: string): string {
  const prefix = MEASUREMENT_SYSTEM_NAME_PREFIXES[unit.system];
  if (prefix === null || !name.toLowerCase().startsWith(prefix.toLowerCase())) return name;

  const stripped = name.slice(prefix.length).trim();

  // A unit whose whole name *is* the prefix would be left blank, which is worse than the prefix.
  return stripped.length === 0 ? name : stripped;
}

/** What it measures, and which system it belongs to when it belongs to one. */
export function recipeUnitDetail(unit: MeasurementUnit): string {
  const system = MEASUREMENT_SYSTEM_LABELS[unit.system];
  const dimension = MEASUREMENT_DIMENSION_LABELS[unit.dimension];

  return system.length === 0 ? dimension : `${dimension} · ${system}`;
}

/**
 * The units a recipe may measure something in, as picker rows, alphabetical by the name they will be shown
 * under.
 *
 * Temperature is left out of every one of them. An ingredient line is not measured in degrees, and a yield is
 * refused in degrees outright by Business (`CK_Recipes_YieldUnit_NotTemperature`) — so listing `degree Celsius`
 * beside `gram` and `cup` is at best confusing and at worst a choice that can only come back as an error. Step
 * temperatures are a different field with its own vocabulary, not a use of this one.
 *
 * `Qualitative` stays: "pinch", "dash" and "to taste" are exactly how creators write some lines.
 */
export function recipeUnitOptions(units: readonly MeasurementUnit[]): readonly CpComboboxOption[] {
  return units
    .filter((unit) => unit.dimension !== 'Temperature')
    .map((unit) => ({ id: unit.id, label: recipeUnitName(unit), detail: recipeUnitDetail(unit) }))
    .sort((left, right) => left.label.localeCompare(right.label));
}
