import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpFieldComponent } from './field.component';
import { CpFieldRowComponent } from './field-row.component';

@Component({
  standalone: true,
  imports: [CpFieldRowComponent, CpFieldComponent],
  template: `<cp-field-row [minColumn]="minColumn"><span>one</span><span>two</span></cp-field-row>
    <cp-field-row><cp-field label="Prep" forId="prep"><input id="prep" /></cp-field></cp-field-row>`,
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
