import { ComponentFixture, TestBed } from '@angular/core/testing';

import { MeasurementUnit } from '../../models/reference.models';
import { UnitMatchCandidate } from '../../models/ingredient-parse.models';
import { IngredientUnitPickerComponent } from './ingredient-unit-picker.component';

function unit(id: string, displayName: string, dimension: MeasurementUnit['dimension'], system: MeasurementUnit['system']): MeasurementUnit {
  return {
    id,
    code: id,
    displayName,
    pluralName: `${displayName}s`,
    abbreviation: id,
    dimension,
    system,
    baseUnitFactor: 1,
    displayPrecision: 2,
  };
}

const UNITS: readonly MeasurementUnit[] = [
  unit('cup-us', 'US cup', 'Volume', 'UsCustomary'),
  unit('g', 'gram', 'Mass', 'Metric'),
  unit('celsius', 'degree Celsius', 'Temperature', 'Metric'),
];

/**
 * The unit control on one ingredient line — a separate component from the yield field's on purpose: this box is
 * free text because the creator's own wording is the record of what they wrote (recipes.md), and a yield unit is
 * an id or nothing.
 */
describe('IngredientUnitPickerComponent', () => {
  async function createFixture(): Promise<ComponentFixture<IngredientUnitPickerComponent>> {
    await TestBed.configureTestingModule({ imports: [IngredientUnitPickerComponent] }).compileComponents();

    const fixture = TestBed.createComponent(IngredientUnitPickerComponent);
    fixture.componentRef.setInput('inputId', 'row-unit-1');
    fixture.componentRef.setInput('catalogue', { status: 'ready', units: UNITS });
    fixture.detectChanges();
    return fixture;
  }

  const host = (fixture: ComponentFixture<unknown>) => fixture.nativeElement as HTMLElement;
  const box = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector<HTMLInputElement>('input')!;
  const rows = (fixture: ComponentFixture<unknown>) =>
    Array.from(host(fixture).querySelectorAll<HTMLElement>('[role="option"]'));
  const emptyText = (fixture: ComponentFixture<unknown>) => host(fixture).querySelector('.empty')?.textContent?.trim() ?? '';

  it('offers cooking names with the region on the second line, and no temperature', async () => {
    const fixture = await createFixture();

    expect(rows(fixture).map((row) => row.querySelector('.label')?.textContent?.trim())).toEqual(['cup', 'gram']);
    expect(rows(fixture).map((row) => row.querySelector('.detail')?.textContent?.trim())).toEqual([
      'Volume · US customary',
      'Weight · metric',
    ]);
  });

  /**
   * The wording a pick hands back is the label that was shown, not the catalogue's own: someone who picks "cup"
   * off a list that said "cup" has written "cup", and reporting "US cup" as their wording would put words in
   * their mouth.
   */
  it('reports a pick by the name the creator was shown', async () => {
    const fixture = await createFixture();
    const picked: UnitMatchCandidate[] = [];
    fixture.componentInstance.unitPicked.subscribe((candidate) => picked.push(candidate));

    host(fixture).querySelector('[id="row-unit-1-option-cup-us"]')!.dispatchEvent(
      new MouseEvent('mousedown', { bubbles: true, cancelable: true }),
    );
    fixture.detectChanges();

    expect(picked).toEqual([{ measurementUnitId: 'cup-us', displayName: 'cup', kind: 'DisplayName' }]);
  });

  it('reports what was typed, and keeps showing it', async () => {
    const fixture = await createFixture();
    const typed: string[] = [];
    fixture.componentInstance.textChanged.subscribe((text) => typed.push(text));

    box(fixture).value = 'glugs';
    box(fixture).dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(typed).toEqual(['glugs']);
    // Free text, so an unrecognised unit is not refused and not reverted.
    expect(emptyText(fixture)).toContain('saved as you wrote it');
  });

  /**
   * The combobox recomputes its selection whenever the field is left, so a null arriving on blur says nothing
   * about what the creator wants. Swallowed here, so the editor cannot mistake it for "remove the match".
   */
  it('does not report a pick when the field is merely left', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('unitId', 'cup-us');
    fixture.componentRef.setInput('unitLabel', 'cup');
    fixture.detectChanges();

    const picked: UnitMatchCandidate[] = [];
    fixture.componentInstance.unitPicked.subscribe((candidate) => picked.push(candidate));

    box(fixture).dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(picked).toEqual([]);
  });

  it('marks the line’s own unit as chosen', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('unitId', 'g');
    fixture.detectChanges();

    const chosen = rows(fixture).filter((row) => row.getAttribute('aria-selected') === 'true');
    expect(chosen.map((row) => row.querySelector('.label')?.textContent?.trim())).toEqual(['gram']);
  });

  it('says what an empty list means, which is not always “no such unit”', async () => {
    const fixture = await createFixture();

    fixture.componentRef.setInput('catalogue', { status: 'loading' });
    fixture.detectChanges();
    expect(emptyText(fixture)).toBe('Loading the unit list…');

    fixture.componentRef.setInput('catalogue', { status: 'unavailable' });
    fixture.detectChanges();
    expect(emptyText(fixture)).toContain('Unit list unavailable');
    // Typing is never blocked by a list that could not be read.
    expect(box(fixture).disabled).toBeFalse();
  });
});
