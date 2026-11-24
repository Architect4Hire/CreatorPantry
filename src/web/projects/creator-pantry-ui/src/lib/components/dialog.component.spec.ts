import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpDialogComponent } from './dialog.component';

type Variant = 'wrapped' | 'cp-actions' | 'own-class';

@Component({
  imports: [CpDialogComponent],
  template: `<cp-dialog [open]="true" title="Approve this recipe?">
    @switch (variant()) {
      @case ('wrapped') {
        <div cpDialogActions id="actions">
          <button id="first" type="button">Cancel</button>
          <button id="second" type="button">Approve</button>
        </div>
      }
      @case ('cp-actions') {
        <div cpDialogActions class="cp-actions" id="actions">
          <button id="first" type="button">Cancel</button>
          <button id="second" type="button">Approve</button>
        </div>
      }
      @case ('own-class') {
        <div cpDialogActions class="mine" id="actions">
          <button id="first" type="button">Cancel</button>
          <button id="second" type="button">Approve</button>
        </div>
      }
    }
  </cp-dialog>`,
  styles: `
    .mine {
      display: flex;
      gap: 40px;
    }
  `,
})
class HostComponent {
  readonly variant = signal<Variant>('wrapped');
}

/** Static markup, because content projected from a block with several roots does not reach a named slot. */
@Component({
  imports: [CpDialogComponent],
  template: `<cp-dialog [open]="true" title="Approve this recipe?">
    <button cpDialogActions id="first" type="button">Cancel</button>
    <button cpDialogActions id="second" type="button">Approve</button>
  </cp-dialog>`,
})
class DirectHostComponent {}

describe('CpDialogComponent, its actions', () => {
  let fixture: ComponentFixture<HostComponent | DirectHostComponent>;

  async function open(variant: Variant | 'direct'): Promise<void> {
    if (variant === 'direct') {
      fixture = TestBed.createComponent(DirectHostComponent);
    } else {
      const wrapped = TestBed.createComponent(HostComponent);
      wrapped.componentInstance.variant.set(variant);
      fixture = wrapped;
    }
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  /** The space between the two buttons, as drawn: what a person sees, not what a style says. */
  function gapBetweenButtons(): number {
    const first = document.querySelector<HTMLElement>('#first')!.getBoundingClientRect();
    const second = document.querySelector<HTMLElement>('#second')!.getBoundingClientRect();
    return Math.round(second.left - first.right);
  }

  beforeEach(() => TestBed.configureTestingModule({ imports: [HostComponent, DirectHostComponent] }));

  it('puts a gap between buttons that are the footer’s own children', async () => {
    await open('direct');

    expect(gapBetweenButtons()).toBe(8);
  });

  it('puts the same gap between buttons inside one wrapper that has no layout of its own', async () => {
    await open('wrapped');

    expect(gapBetweenButtons()).toBe(8);
    // Aligned to the end, as the footer's own children are.
    const wrapper = document.querySelector<HTMLElement>('#actions')!;
    const footer = wrapper.parentElement!;
    expect(Math.round(footer.getBoundingClientRect().right - wrapper.getBoundingClientRect().right)).toBe(0);
  });

  it('leaves a wrapper that is already .cp-actions to its own gap', async () => {
    await open('cp-actions');

    // The global utility's gap, which is wider than the footer's.
    expect(gapBetweenButtons()).toBeGreaterThan(8);
  });

  it('leaves a wrapper with a layout of the consumer’s own alone', async () => {
    await open('own-class');

    expect(gapBetweenButtons()).toBe(40);
  });
});
