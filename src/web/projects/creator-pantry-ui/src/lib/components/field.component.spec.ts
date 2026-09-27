import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpFieldComponent } from './field.component';

@Component({
  standalone: true,
  imports: [CpFieldComponent],
  template: `
    <div class="box" style="width: 300px">
      <cp-field label="Title" forId="title">
        <input id="title" type="text" />
      </cp-field>
      <cp-field label="Description" forId="description">
        <textarea id="description"></textarea>
      </cp-field>
      <cp-field label="Status" forId="status">
        <select id="status"><option>Draft</option></select>
      </cp-field>
    </div>
  `,
})
class HostComponent {}

describe('CpFieldComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('keeps a projected control inside the width it was given, padding and border included', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;
    const box = host.querySelector('.box') as HTMLElement;
    const limit = box.getBoundingClientRect().right + 1;

    // The controls are width:100% with their own padding and a border. Under content-box that made every
    // field ~28px wider than the column holding it — invisible while forms sat in a roomy fixed-width
    // column, and a horizontal scrollbar (WCAG 2.2 SC 1.4.10) as soon as one did not.
    for (const id of ['title', 'description', 'status']) {
      const control = host.querySelector('#' + id) as HTMLElement;
      expect(getComputedStyle(control).boxSizing)
        .withContext(id)
        .toBe('border-box');
      expect(control.getBoundingClientRect().right)
        .withContext(id)
        .toBeLessThanOrEqual(limit);
    }
  });

  it('gives every projected control a touch target of at least 40px', async () => {
    const fixture = await createFixture();
    const host = fixture.nativeElement as HTMLElement;

    for (const id of ['title', 'status']) {
      expect((host.querySelector('#' + id) as HTMLElement).getBoundingClientRect().height)
        .withContext(id)
        .toBeGreaterThanOrEqual(40);
    }
  });
});
