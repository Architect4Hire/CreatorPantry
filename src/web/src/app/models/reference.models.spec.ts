import { decodeCursorPage, decodeMeasurementUnit } from './reference.models';

function gram(): Record<string, unknown> {
  return {
    id: 'unit-gram',
    code: 'gram',
    displayName: 'gram',
    pluralName: 'grams',
    abbreviation: 'g',
    dimension: 'Mass',
    system: 'Metric',
    baseUnitFactor: 1,
    displayPrecision: 2,
  };
}

describe('reference.models', () => {
  describe('decodeMeasurementUnit', () => {
    it('decodes a unit with a base factor', () => {
      expect(decodeMeasurementUnit(gram())).toEqual({
        id: 'unit-gram',
        code: 'gram',
        displayName: 'gram',
        pluralName: 'grams',
        abbreviation: 'g',
        dimension: 'Mass',
        system: 'Metric',
        baseUnitFactor: 1,
        displayPrecision: 2,
      });
    });

    // Temperature is affine and Qualitative has no numeric relationship at all, so both carry no factor.
    // A null there is the catalogue being correct, not a field that failed to arrive.
    it('decodes a unit with no base factor', () => {
      const celsius = { ...gram(), id: 'unit-c', code: 'celsius', dimension: 'Temperature', baseUnitFactor: null };

      const decoded = decodeMeasurementUnit(celsius);
      expect(decoded?.baseUnitFactor).toBeNull();
      expect(decoded?.dimension).toBe('Temperature');
    });

    it('refuses a dimension it does not recognise', () => {
      expect(decodeMeasurementUnit({ ...gram(), dimension: 'Luminance' })).toBeNull();
    });

    it('refuses a system it does not recognise', () => {
      expect(decodeMeasurementUnit({ ...gram(), system: 'Ancient' })).toBeNull();
    });

    it('refuses a unit missing a field', () => {
      const partial = gram();
      delete partial['abbreviation'];

      expect(decodeMeasurementUnit(partial)).toBeNull();
    });

    it('refuses a value that is not an object', () => {
      expect(decodeMeasurementUnit(null)).toBeNull();
      expect(decodeMeasurementUnit('gram')).toBeNull();
    });
  });

  describe('decodeCursorPage', () => {
    it('decodes a page and its cursor', () => {
      const page = decodeCursorPage({ items: [gram()], nextCursor: 'abc' }, decodeMeasurementUnit);

      expect(page?.items.length).toBe(1);
      expect(page?.nextCursor).toBe('abc');
    });

    it('decodes a last page, whose cursor is null', () => {
      expect(decodeCursorPage({ items: [], nextCursor: null }, decodeMeasurementUnit)).toEqual({
        items: [],
        nextCursor: null,
      });
    });

    // One unreadable row makes the whole page unreadable: a picker quietly missing a unit reads as a unit
    // the catalogue does not have.
    it('refuses a page holding one undecodable item', () => {
      const page = decodeCursorPage(
        { items: [gram(), { ...gram(), dimension: 'Luminance' }], nextCursor: null },
        decodeMeasurementUnit,
      );

      expect(page).toBeNull();
    });

    it('refuses a page whose items are not an array', () => {
      expect(decodeCursorPage({ items: 'gram', nextCursor: null }, decodeMeasurementUnit)).toBeNull();
    });
  });
});
