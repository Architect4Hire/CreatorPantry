import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpFieldComponent } from './field.component';
import { CpFieldRowComponent } from './field-row.component';

@Component({
  standalone: true,
  imports: [CpFieldRowComponent, CpFieldComponent],
  template: `<cp-field-row [minColumn]="minColumn"><span>one</span><span>two</span></cp-field-row>
    <cp-field-row
      ><cp-field label="Prep" forId="prep"><input id="prep" /></cp-field
    ></cp-field-row>`,
})
class HostComponent {
  minColumn = '14rem';
}

describe('CpFieldRowComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    return TestBed.createComponent(HostComponent);
  }

  it('lays its children out as a grid', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('cp-field-row')!;
    expect(getComputedStyle(row).display).toBe('grid');
  });

  it('takes its column floor from the consumer', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.minColumn = '20rem';
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row')!;
    expect(row.style.getPropertyValue('--cp-field-row-min')).toBe('20rem');
  });

  describe('with a column count', () => {
    @Component({
      standalone: true,
      imports: [CpFieldRowComponent],
      // The measure is set here rather than relied on from the token sheet, which a library spec does not load.
      template: `<div [style.width]="width" style="--cp-measure-field: 40rem">
        <cp-field-row [columns]="4" minColumn="8rem">
          <div>a</div>
          <div>b</div>
          <div>c</div>
          <div class="cp-span-2">d</div>
        </cp-field-row>
        <cp-field-row class="lone" [columns]="4" minColumn="8rem"><div>only</div></cp-field-row>
        <cp-field-row class="wide" [columns]="4" minColumn="8rem">
          <div class="cp-span-3 cp-stack-narrow">text</div>
          <div class="cp-stack-narrow">quantity</div>
        </cp-field-row>
      </div>`,
    })
    class FixedHostComponent {
      width = '80rem';
    }

    async function createFixed() {
      await TestBed.configureTestingModule({ imports: [FixedHostComponent] }).compileComponents();
      return TestBed.createComponent(FixedHostComponent);
    }

    const tracks = (row: Element) => {
      const value = getComputedStyle(row).gridTemplateColumns;
      return value === 'none' || value === '' ? 0 : value.split(/\s+/).length;
    };

    it('is held to the measure', async () => {
      const fixture = await createFixed();
      fixture.detectChanges();

      const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row')!;
      expect(row.getBoundingClientRect().width).toBeCloseTo(40 * 16, 0);
    });

    it('divides the measure into that many equal columns', async () => {
      const fixture = await createFixed();
      fixture.detectChanges();
      const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row')!;

      expect(tracks(row)).toBe(4);
      const [a, b, c] = Array.from(row.children).map((child) => child.getBoundingClientRect().width);
      expect(Math.abs(a - b)).toBeLessThan(1);
      expect(Math.abs(b - c)).toBeLessThan(1);
    });

    it('keeps a lone child to one column, not the whole row', async () => {
      const fixture = await createFixed();
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const first = host.querySelector('cp-field-row:not(.lone)')!.children[0].getBoundingClientRect().width;
      const lone = host.querySelector('cp-field-row.lone')!.children[0].getBoundingClientRect().width;

      // The same quarter as in a full row: a row with one quantity does not hand it the line.
      expect(Math.abs(lone - first)).toBeLessThan(1);
    });

    it('lets a lone child take the whole row once the row is too narrow for its columns', async () => {
      const fixture = await createFixed();
      fixture.componentInstance.width = '20rem';
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const row = host.querySelector<HTMLElement>('cp-field-row.lone')!;

      expect(Math.abs(row.children[0].getBoundingClientRect().width - row.getBoundingClientRect().width)).toBeLessThan(
        1,
      );
    });

    it('lets a child take three columns, with a quantity beside it, while the row has room', async () => {
      const fixture = await createFixed();
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const quarter = host
        .querySelector('cp-field-row:not(.lone):not(.wide)')!
        .children[0].getBoundingClientRect().width;
      const [text, quantity] = Array.from(host.querySelector('cp-field-row.wide')!.children).map((child) =>
        child.getBoundingClientRect(),
      );

      expect(text.width).toBeCloseTo(quarter * 3 + 32, 0);
      expect(Math.abs(quantity.width - quarter)).toBeLessThan(1);
      // The same line: the quantity is the last quarter, beside the text and not under it.
      expect(Math.abs(quantity.top - text.top)).toBeLessThan(1);
    });

    it('gives both the whole row, one above the other, once the row is too narrow for its columns', async () => {
      const fixture = await createFixed();
      fixture.componentInstance.width = '20rem';
      fixture.detectChanges();
      const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row.wide')!;
      const rowWidth = row.getBoundingClientRect().width;
      const [text, quantity] = Array.from(row.children).map((child) => child.getBoundingClientRect());

      expect(Math.abs(text.width - rowWidth)).toBeLessThan(1);
      expect(Math.abs(quantity.width - rowWidth)).toBeLessThan(1);
      expect(quantity.top).toBeGreaterThanOrEqual(text.bottom - 1);
    });

    it('lets a child take two columns', async () => {
      const fixture = await createFixed();
      fixture.detectChanges();
      const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row')!;
      const [a, , , d] = Array.from(row.children).map((child) => child.getBoundingClientRect().width);

      // Two columns and the gap between them.
      expect(d).toBeCloseTo(a * 2 + 16, 0);
    });

    it('gives up columns rather than squeeze them below the floor when the row is narrow', async () => {
      const fixture = await createFixed();
      fixture.componentInstance.width = '20rem';
      fixture.detectChanges();
      const row = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>('cp-field-row')!;

      expect(tracks(row)).toBeLessThanOrEqual(2);
      for (const child of Array.from(row.children)) {
        expect(child.getBoundingClientRect().width).toBeGreaterThanOrEqual(8 * 16 - 1);
      }
    });
  });

  it('leaves a row with no column count as wide as its container, as it always was', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('cp-field-row')!;
    expect(getComputedStyle(row).maxWidth).toBe('none');
    expect(row.classList.contains('cp-field-row--fixed')).toBeFalse();
  });

  it('keeps every child it was given', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const row = (fixture.nativeElement as HTMLElement).querySelector('cp-field-row')!;
    expect(row.querySelectorAll('span').length).toBe(2);
  });

  it('caps the fields it holds at the reading measure', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const field = (fixture.nativeElement as HTMLElement).querySelector('cp-field')!;
    // Two wide fields splitting a desktop row would otherwise each run well past a comfortable line.
    expect(getComputedStyle(field).maxWidth).not.toBe('none');
  });
});
