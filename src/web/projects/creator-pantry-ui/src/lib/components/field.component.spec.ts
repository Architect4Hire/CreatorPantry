import { Component, signal } from '@angular/core';
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
        <select id="status"
          ><option>Draft</option></select
        >
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
      expect(getComputedStyle(control).boxSizing).withContext(id).toBe('border-box');
      expect(control.getBoundingClientRect().right).withContext(id).toBeLessThanOrEqual(limit);
    }
  });

  describe('a number', () => {
    @Component({
      standalone: true,
      imports: [CpFieldComponent],
      template: `<cp-field label="Prep time" forId="prep">
        <input id="prep" type="number" min="0" value="15" />
      </cp-field>`,
    })
    class NumberHostComponent {}

    /** The browser's own spin buttons are only there to restyle where the engine has them (Firefox does not). */
    const engineHasSpinButtons = CSS.supports('selector(::-webkit-inner-spin-button)');

    it('draws its own pair of arrows in the muted ink, as a select draws its caret, not the operating system’s', async () => {
      if (!engineHasSpinButtons) return pending('this engine has no spin buttons to restyle');

      await TestBed.configureTestingModule({ imports: [NumberHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(NumberHostComponent);
      document.body.appendChild(fixture.nativeElement);
      fixture.detectChanges();

      try {
        const style = getComputedStyle(fixture.nativeElement.querySelector('#prep') as HTMLElement);

        // Four wedges — an up pair and a down pair — in a background image, so they follow the theme.
        expect(style.backgroundImage.match(/linear-gradient/g)?.length).toBe(4);
        // Room for them, so a long value never runs under the arrows.
        expect(parseFloat(style.paddingInlineEnd)).toBeGreaterThanOrEqual(40);
        expect(style.position).toBe('relative');
      } finally {
        fixture.nativeElement.remove();
      }
    });
  });

  describe('error and warning', () => {
    @Component({
      standalone: true,
      imports: [CpFieldComponent],
      template: `<cp-field label="Title" forId="t" hint="Short." [error]="error()" [warning]="warning()">
        <input id="t" type="text" aria-describedby="theirs" />
      </cp-field>`,
    })
    class StateHostComponent {
      readonly error = signal('');
      readonly warning = signal('');
    }

    async function create() {
      await TestBed.configureTestingModule({ imports: [StateHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(StateHostComponent);
      fixture.detectChanges();
      const input = (fixture.nativeElement as HTMLElement).querySelector('input')!;

      return {
        fixture,
        input,
        host: fixture.componentInstance,
        async settle() {
          fixture.detectChanges();
          await fixture.whenStable();
        },
      };
    }

    it('marks the control invalid while an error shows and clears it when the error goes', async () => {
      const view = await create();
      await view.settle();
      expect(view.input.hasAttribute('aria-invalid')).toBeFalse();

      view.host.error.set('Name it.');
      await view.settle();
      expect(view.input.getAttribute('aria-invalid')).toBe('true');
      expect(view.input.getAttribute('aria-describedby')).toBe('theirs t-hint t-error');

      view.host.error.set('');
      await view.settle();
      expect(view.input.hasAttribute('aria-invalid')).toBeFalse();
    });

    it('leaves an aria-invalid it did not set alone', async () => {
      const view = await create();
      view.input.setAttribute('aria-invalid', 'true');

      view.host.error.set('');
      await view.settle();

      expect(view.input.getAttribute('aria-invalid')).toBe('true');
    });

    it('shows a warning beside the control, described to it, without marking it invalid', async () => {
      const view = await create();

      view.host.warning.set('A long title.');
      await view.settle();

      const warning = (view.fixture.nativeElement as HTMLElement).querySelector('.warning')!;
      expect(warning.textContent).toContain('A long title.');
      expect(warning.textContent).toContain('Heads up:');
      expect(warning.getAttribute('role')).toBeNull();
      expect(view.input.getAttribute('aria-describedby')).toBe('theirs t-hint t-warning');
      expect(view.input.hasAttribute('aria-invalid')).toBeFalse();
    });

    it('shows an error and a warning together, the error first', async () => {
      const view = await create();

      view.host.error.set('Name it.');
      view.host.warning.set('A long title.');
      await view.settle();

      expect(view.input.getAttribute('aria-describedby')).toBe('theirs t-hint t-error t-warning');
      const classes = Array.from((view.fixture.nativeElement as HTMLElement).querySelectorAll('small')).map(
        (el) => el.className,
      );
      expect(classes).toEqual(['', 'error', 'warning']);
    });
  });

  describe('a hidden label', () => {
    @Component({
      standalone: true,
      imports: [CpFieldComponent],
      template: `<cp-field label="Step 1" forId="s1" [required]="true" [labelHidden]="true" error="Write the step.">
        <textarea id="s1"></textarea>
      </cp-field>`,
    })
    class HiddenLabelHostComponent {}

    it('names the control without showing the label, and wires required and the error as when shown', async () => {
      await TestBed.configureTestingModule({ imports: [HiddenLabelHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(HiddenLabelHostComponent);
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const label = host.querySelector('label')!;
      const control = host.querySelector('#s1')!;

      // Still the control's label: its accessible name, and clicking it still reaches it.
      expect(label.getAttribute('for')).toBe('s1');
      expect(label.textContent).toContain('Step 1');
      // Out of sight and out of the layout: a pixel, taking no row of the field's grid.
      expect(label.getBoundingClientRect().width).toBeLessThanOrEqual(1);
      expect(getComputedStyle(label).position).toBe('absolute');
      // Everything else exactly as with a visible label.
      expect(control.getAttribute('aria-required')).toBe('true');
      expect(control.getAttribute('aria-invalid')).toBe('true');
      expect(control.getAttribute('aria-describedby')).toBe('s1-error');
      expect(host.querySelector('.error')?.textContent).toContain('Write the step.');
    });
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
