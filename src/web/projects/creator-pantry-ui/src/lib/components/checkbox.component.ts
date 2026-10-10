import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';

let nextId = 0;

/**
 * A labelled checkbox with a drawn control.
 *
 * It exists because an unstyled `<input type="checkbox">` cannot meet this design system's own rules. The
 * browser paints it from the inherited `color-scheme`, which `themes.css` sets per theme — so in dark mode the
 * *unchecked* box is a dark filled square, indistinguishable at a glance from a checked one — and it renders at
 * 13×13 with no author focus treatment, well under the 40px target. Fixing any of that requires
 * `appearance: none`, after which every state has to be drawn anyway. Drawn once here, rather than in each
 * feature that needs a checkbox.
 *
 * The real input stays in the DOM, focusable and in the accessibility tree; it is only visually replaced by the
 * box beside it. So keyboard behaviour, the `checked` semantics a screen reader announces, and form
 * participation are the browser's, not this component's.
 *
 * Checked reads as a glyph rather than as a colour — the fill is reinforcement, not the signal — and one set of
 * markup serves both themes, with every value a `--cp-*` token.
 *
 * The label is `--cp-font-size-sm`, the size of a form control. A surface read from further away sets
 * `--cp-checkbox-font-size` on the host to a type token; the box and the 40px target do not change.
 */
@Component({
  selector: 'cp-checkbox', standalone: true,
  template: `<label [attr.for]="resolvedId()">
    <input
      type="checkbox"
      [id]="resolvedId()"
      [checked]="checked()"
      [disabled]="disabled()"
      (change)="onToggle($event)"
    />
    <span class="box" aria-hidden="true">@if (checked()) { <span class="glyph">✓</span> }</span>
    <span class="text">{{ label() }}</span>
  </label>`,
  styles: [`
    :host { display:inline-block; }
    label { position:relative; display:inline-flex; align-items:center; gap:var(--cp-space-2); min-height:2.5rem; color:var(--cp-text); font-size:var(--cp-checkbox-font-size, var(--cp-font-size-sm)); cursor:pointer; }
    /* Visually replaced, never removed: still focusable, still announced, still a checkbox to a form. */
    input { position:absolute; width:1px; height:1px; margin:0; padding:0; opacity:0; pointer-events:none; }
    .box { display:grid; place-items:center; flex:0 0 auto; width:1.25rem; height:1.25rem; border:1px solid var(--cp-border-strong); border-radius:var(--cp-radius-sm); background:var(--cp-surface); color:var(--cp-primary-ink); font-size:.75rem; line-height:1; }
    input:checked + .box { border-color:var(--cp-primary); background:var(--cp-primary); }
    label:hover input:not(:disabled) + .box { border-color:var(--cp-primary); }
    /* The same ring global.css gives every other focused control, moved onto the box the input now stands for. */
    input:focus-visible + .box { outline:3px solid var(--cp-focus); outline-offset:2px; }
    input:disabled + .box { border-color:var(--cp-border); background:var(--cp-surface-subtle); color:var(--cp-text-faint); }
    input:disabled ~ .text { color:var(--cp-text-muted); }
    :host([data-disabled]) label { cursor:not-allowed; }
  `],
  host: { '[attr.data-disabled]': 'disabled() || null' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpCheckboxComponent {
  readonly label = input.required<string>();
  readonly checked = model(false);
  readonly disabled = input(false);

  /** Given only when something outside needs to name this control; otherwise one is generated. */
  readonly inputId = input('');

  /** Allocated once per instance, so the id is stable however often the template renders. */
  private readonly fallbackId = `cp-checkbox-${++nextId}`;

  protected readonly resolvedId = computed(() => this.inputId() || this.fallbackId);

  protected onToggle(event: Event): void {
    this.checked.set((event.target as HTMLInputElement).checked);
  }
}
