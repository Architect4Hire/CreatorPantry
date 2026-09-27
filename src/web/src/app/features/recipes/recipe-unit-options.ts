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
  const prefix = MEASUREMENT_SYSTEM_NAME_PREFIXES[unit.system];
  if (prefix === null || !unit.displayName.toLowerCase().startsWith(prefix.toLowerCase())) return unit.displayName;

  const stripped = unit.displayName.slice(prefix.length).trim();

  // A unit whose whole name *is* the prefix would be left blank, which is worse than the prefix.
  return stripped.length === 0 ? unit.displayName : stripped;
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
