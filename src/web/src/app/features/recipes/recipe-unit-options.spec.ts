import { MeasurementDimension, MeasurementSystem, MeasurementUnit } from '../../models/reference.models';
import { recipeUnitDetail, recipeUnitName, recipeUnitOptions } from './recipe-unit-options';

function unit(
  id: string,
  displayName: string,
  dimension: MeasurementDimension,
  system: MeasurementSystem,
  abbreviation = 'x',
): MeasurementUnit {
  return {
    id,
    code: id,
    displayName,
    pluralName: `${displayName}s`,
    abbreviation,
    dimension,
    system,
    baseUnitFactor: 1,
    displayPrecision: 2,
  };
}

/**
 * How a unit reads when someone is writing a recipe, as against converting one.
 *
 * The catalogue's own names are precise on purpose — a US cup is 236.6ml and an imperial one is 284ml — and
 * nothing here renames them. This is the presentation a picker uses, and the seed data stays the authority.
 */
describe('recipeUnitOptions', () => {
  it('takes the region off the name and says it on the second line instead', () => {
    const cup = unit('cup-us', 'US cup', 'Volume', 'UsCustomary', 'cup');

    expect(recipeUnitName(cup)).toBe('cup');
    expect(recipeUnitDetail(cup)).toBe('Volume · US customary');
  });

  it('leaves a name that carries no region alone', () => {
    expect(recipeUnitName(unit('g', 'gram', 'Mass', 'Metric'))).toBe('gram');
    expect(recipeUnitDetail(unit('g', 'gram', 'Mass', 'Metric'))).toBe('Weight · metric');
  });

  // A clove belongs to no system, and saying "Count · neutral" would be answering a question nobody asked.
  it('says nothing about the system of a unit that has none', () => {
    expect(recipeUnitDetail(unit('clove', 'clove', 'Count', 'Neutral'))).toBe('Count');
  });

  // Prefix matching, not string surgery on a hardcoded list: an imperial unit is handled the same way.
  it('handles every system that prefixes its names', () => {
    expect(recipeUnitName(unit('pint-imp', 'Imperial pint', 'Volume', 'Imperial'))).toBe('pint');
    expect(recipeUnitDetail(unit('pint-imp', 'Imperial pint', 'Volume', 'Imperial'))).toBe('Volume · imperial');
  });

  // Stripping to nothing would leave a blank row, which is worse than the prefix it was trying to remove.
  it('keeps the name when the prefix is the whole of it', () => {
    expect(recipeUnitName(unit('us', 'US', 'Volume', 'UsCustomary'))).toBe('US');
  });

  /**
   * The defect this function exists to fix. An ingredient line is not measured in degrees, and a yield in
   * degrees is refused by Business outright — so a picker listing "degree Celsius" beside "gram" is offering
   * either nonsense or an error. Step temperatures are a separate field with its own vocabulary.
   */
  it('never offers a temperature', () => {
    const options = recipeUnitOptions([
      unit('celsius', 'degree Celsius', 'Temperature', 'Metric'),
      unit('fahrenheit', 'degree Fahrenheit', 'Temperature', 'UsCustomary'),
      unit('g', 'gram', 'Mass', 'Metric'),
    ]);

    expect(options.map((option) => option.id)).toEqual(['g']);
  });

  // "pinch", "dash" and "to taste" are how creators write some lines; only degrees are excluded.
  it('keeps descriptive units', () => {
    const options = recipeUnitOptions([
      unit('pinch', 'pinch', 'Qualitative', 'Neutral'),
      unit('to-taste', 'to taste', 'Qualitative', 'Neutral'),
    ]);

    expect(options.map((option) => option.label)).toEqual(['pinch', 'to taste']);
  });

  // Alphabetical by what is shown, not by the catalogue name — otherwise "cup" sorts under U.
  it('sorts by the name the creator will read', () => {
    const options = recipeUnitOptions([
      unit('cup-us', 'US cup', 'Volume', 'UsCustomary'),
      unit('tbsp', 'tablespoon', 'Volume', 'UsCustomary'),
      unit('g', 'gram', 'Mass', 'Metric'),
    ]);

    expect(options.map((option) => option.label)).toEqual(['cup', 'gram', 'tablespoon']);
  });
});
