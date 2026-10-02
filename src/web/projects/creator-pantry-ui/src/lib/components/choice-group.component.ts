import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';

/** One option in a {@link CpChoiceGroupComponent}. */
export interface CpChoiceOption {
  /** What the group reports when this is picked. Also the suffix of the control's id. */
  readonly value: string;
  readonly label: string;
  /** A short line under the label saying what this choice means. */
  readonly hint?: string;
  /** A sample of the thing itself — a sentence written in that voice, say. Shown quoted, under the hint. */
  readonly example?: string;
  readonly disabled?: boolean;
}

export type CpChoiceLayout = 'stack' | 'fit';

let cpChoiceGroupUid = 0;

/**
 * A set of choices as selectable tiles: pick one, or pick several.
 *
 * **Why tiles and why here.** A choice between written voices is read, not scanned — each option carries a
 * label, a line about what it means and often a sample sentence — and a bare radio beside three lines of text
 * gives the eye nothing to aim at. A tile makes the whole option the target, which is also what gets it past
 * the 40px minimum without inventing padding per feature. It is the same "card you click" shape
 * `CpQuickActionComponent` gives an action, so a questionnaire reads as part of the same product as the rest of
 * it.
 *
 * **Why it is a component rather than four stylesheets.** The control is *drawn* — `appearance: none`, every
 * state authored — because `themes.css` sets `color-scheme` and a native radio left alone arrives at 13px with
 * no author focus ring, and in dark mode an unchecked box reads as checked. DESIGN-SYSTEM.md states the
 * consequence: a drawn control belongs in one library component. Three feature stylesheets had already drawn
 * this same row before this existed.
 *
 * **What stays the browser's.** A real `input` per option stays in the DOM, focusable and announced, and is
 * only visually replaced — so arrow-key movement within a radio group, `Space` to toggle a checkbox, and what a
 * screen reader says are all the platform's rather than this component's.
 *
 * **What it does not own.** Which options exist, what they mean, and whether an answer is required. It reports
 * what is picked and nothing else; it performs no validation and has no error state of its own, because a
 * choice group lives inside a `cp-field` or a `cp-form-section` that owns the label, the hint and the error.
 */
@Component({
  selector: 'cp-choice-group',
  standalone: true,
  template: `<div
    [attr.role]="multiple() ? 'group' : 'radiogroup'"
    [attr.aria-label]="label()"
    [attr.aria-describedby]="describedBy() || null"
    class="tiles"
    [class.fit]="layout() === 'fit'"
  >
    @for (option of options(); track option.value) {
      @let picked = isPicked(option.value);
      @let blocked = isBlocked(option);
      <label class="tile" [class.picked]="picked" [attr.for]="idFor(option.value)">
        <input
          [type]="multiple() ? 'checkbox' : 'radio'"
          [id]="idFor(option.value)"
          [name]="resolvedName()"
          [value]="option.value"
          [checked]="picked"
          [disabled]="blocked"
          (change)="onChange(option.value, $event)"
        />
        <span class="mark" [class.round]="!multiple()" aria-hidden="true">
          @if (picked) {
            <span class="glyph">{{ multiple() ? '✓' : '●' }}</span>
          }
        </span>
        <span class="text">
          <span class="label">{{ option.label }}</span>
          @if (option.hint) {
            <span class="hint">{{ option.hint }}</span>
          }
          @if (option.example) {
            <span class="example">{{ option.example }}</span>
          }
        </span>
      </label>
    }
  </div>`,
  styles: [
    `
      :host {
        display: block;
      }
      /* minmax(0,1fr) so one wide example sentence cannot hold the column wider than the card (DESIGN-SYSTEM). */
      .tiles {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-2);
        min-width: 0;
      }
      /* Short options — a list of channels — as many across as fit, with the floor clamped so a narrow
         viewport shrinks the track instead of scrolling sideways. */
      .tiles.fit {
        grid-template-columns: repeat(auto-fit, minmax(min(13rem, 100%), 1fr));
      }
      .tile {
        display: flex;
        align-items: flex-start;
        gap: var(--cp-space-3);
        min-block-size: 3rem;
        min-width: 0;
        padding: var(--cp-space-3);
        border: 1px solid var(--cp-border);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface);
        color: var(--cp-text);
        cursor: pointer;
        /* The interactive card's own lift, a little shorter for a smaller surface: a tile is the same gesture
           at a smaller size, so it should answer the pointer the same way. global.css shortens this to nothing
           under prefers-reduced-motion. */
        transition: transform var(--cp-duration-fast) var(--cp-ease), border-color var(--cp-duration-fast),
          background var(--cp-duration-fast), box-shadow var(--cp-duration-fast);
      }
      /* Visually replaced, never removed: still focusable, still announced, still a radio or checkbox to a form. */
      input {
        position: absolute;
        width: 1px;
        height: 1px;
        margin: 0;
        padding: 0;
        opacity: 0;
        pointer-events: none;
      }
      .mark {
        display: grid;
        place-items: center;
        flex: 0 0 auto;
        width: 1.25rem;
        height: 1.25rem;
        margin-block-start: 0.1rem;
        border: 1px solid var(--cp-border-strong);
        border-radius: var(--cp-radius-sm);
        background: var(--cp-surface);
        color: var(--cp-primary-ink);
        font-size: 0.75rem;
        line-height: 1;
      }
      .mark.round {
        border-radius: var(--cp-radius-full);
        font-size: 0.5rem;
      }
      .text {
        display: grid;
        gap: var(--cp-space-1);
        min-width: 0;
      }
      .label {
        font-weight: 600;
        overflow-wrap: anywhere;
      }
      .hint,
      .example {
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
        overflow-wrap: anywhere;
      }
      .example {
        font-style: italic;
      }
      .tile:hover:not(:has(input:disabled)) {
        transform: translateY(-1px);
        border-color: color-mix(in srgb, var(--cp-primary) 45%, var(--cp-border));
        box-shadow: var(--cp-shadow-sm);
      }
      .tile.picked {
        border-color: var(--cp-primary);
        background: var(--cp-primary-soft);
      }
      .tile.picked .mark {
        border-color: var(--cp-primary);
        background: var(--cp-primary);
      }
      .tile.picked .hint,
      .tile.picked .example {
        color: var(--cp-text);
      }
      /* The same ring global.css gives every other focused control, moved onto the tile the input stands for. */
      .tile:has(input:focus-visible) {
        outline: 3px solid var(--cp-focus);
        outline-offset: 2px;
      }
      .tile:has(input:disabled) {
        border-color: var(--cp-border);
        background: var(--cp-surface-subtle);
        color: var(--cp-text-muted);
        cursor: not-allowed;
      }
      .tile:has(input:disabled) .mark {
        border-color: var(--cp-border);
        background: var(--cp-surface-subtle);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpChoiceGroupComponent {
  /** The group's accessible name. Required: a set of choices nobody has named is a set nobody can announce. */
  readonly label = input.required<string>();

  readonly options = input.required<readonly CpChoiceOption[]>();

  /** Several answers rather than one. Changes the control type, the role and the glyph. */
  readonly multiple = input(false);

  /**
   * What is picked, newest intent last, always an array so one answer and several share a shape.
   * A single-choice group replaces it; an empty array is nothing picked.
   */
  readonly value = model<readonly string[]>([]);

  /**
   * The most that may be picked at once, for a multiple group. At the cap the unpicked options are
   * **disabled** rather than silently dropped, so the limit is visible before it bites.
   */
  readonly max = input<number | null>(null);

  readonly layout = input<CpChoiceLayout>('stack');

  /**
   * Prefix for each control's id, so a consumer can address one option. The id is
   * `{idPrefix}{value}`; left unset, a unique prefix is generated.
   */
  readonly idPrefix = input('');

  /** The radio group's `name`. Left unset, a unique one is generated. */
  readonly name = input('');

  /** An element describing the whole group, when something outside it does. */
  readonly describedBy = input('');

  private readonly uid = `cp-choice-${(cpChoiceGroupUid += 1)}`;

  protected readonly resolvedName = computed(() => this.name() || this.uid);

  protected readonly atCap = computed(() => {
    const max = this.max();
    return this.multiple() && max !== null && this.value().length >= max;
  });

  protected idFor(value: string): string {
    return `${this.idPrefix() || `${this.uid}-`}${value}`;
  }

  protected isPicked(value: string): boolean {
    return this.value().includes(value);
  }

  /** Disabled either on its own terms, or because the cap is reached and this is not one of the picked. */
  protected isBlocked(option: CpChoiceOption): boolean {
    return option.disabled === true || (this.atCap() && !this.isPicked(option.value));
  }

  protected onChange(value: string, event: Event): void {
    const on = (event.target as HTMLInputElement).checked;

    if (!this.multiple()) {
      // A radio cannot be unchecked by clicking it, so `on` is always true here; replacing is the whole change.
      this.value.set([value]);
      return;
    }

    const next = new Set(this.value());
    if (on) {
      next.add(value);
    } else {
      next.delete(value);
    }

    // Reported in the order the options are shown, so the same answers always serialise the same way.
    this.value.set(this.options().map((option) => option.value).filter((candidate) => next.has(candidate)));
  }
}
