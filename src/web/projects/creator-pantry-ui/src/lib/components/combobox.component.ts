import { ChangeDetectionStrategy, Component, ElementRef, computed, inject, input, model, signal } from '@angular/core';

/** One row of a {@link CpComboboxComponent}'s list. */
export interface CpComboboxOption {
  /** Stable and unique within the list; becomes the option element's id, which `aria-activedescendant` names. */
  readonly id: string;
  /** What is shown, and what local filtering matches. */
  readonly label: string;
  /** An optional second line — "g · mass", "12 recipes". Shown, never matched. */
  readonly detail?: string;
  /** Skipped by every movement key and refused on selection. */
  readonly disabled?: boolean;
}

/** "3 results" / "No results", the text announced when the list changes. Replaceable, so an app can localise it. */
export function cpComboboxResultsLabel(count: number): string {
  return count === 0 ? 'No results' : `${count} result${count === 1 ? '' : 's'}`;
}

/**
 * A type-ahead combobox: a text input that filters a supplied list, with keyboard selection.
 *
 * It owns no data. Options come in, a selection goes out, and nothing here fetches, debounces or knows what a
 * list is of — a consumer that filters server-side passes its own already-filtered list with
 * `[filterLocally]="false"` and this stops filtering it again.
 *
 * It renders **no label**: put it inside a `CpFieldComponent` and give it that field's `forId` as
 * {@link inputId}. The field owns the label, hint and error, and its own `input` styling reaches the text input
 * in here — which is also why a combobox outside a field renders an unstyled input, and is not a supported
 * arrangement.
 *
 * Both "must pick from the list" and "free text allowed" are the same component, because they differ only in
 * what happens to text that matches nothing (see {@link mode}), and splitting them would make a consumer choose
 * a component before knowing which rule their field has.
 *
 * Focus stays on the input throughout — the active option is named by `aria-activedescendant` rather than
 * focused, which is what lets a screen-reader user hear the option while still typing into the box.
 *
 * A caret marks it as a picker and clicking the box opens the list, so it does not read as a plain text field
 * before anything has been typed. Focus alone does not open it — see {@link onClick}.
 */
@Component({
  selector: 'cp-combobox', standalone: true,
  template: `<input
      type="text"
      role="combobox"
      autocomplete="off"
      [id]="inputId()"
      [attr.placeholder]="placeholder() || null"
      [value]="value()"
      [disabled]="disabled()"
      [attr.aria-expanded]="isOpen()"
      [attr.aria-controls]="listboxId()"
      aria-autocomplete="list"
      [attr.aria-activedescendant]="activeOptionElementId()"
      (click)="onClick()"
      (input)="onInput($event)"
      (keydown)="onKeydown($event)"
      (blur)="onBlur()"
    />

    <!-- Decoration, not a control: aria-hidden, and no pointer events of its own, so a pointer aimed at it
         lands on the input underneath and opens the list exactly as a click anywhere else in the box does. It
         is here because without it the control is indistinguishable from a plain text field; see onClick. -->
    <span class="caret" aria-hidden="true" [class.open]="isOpen()" [class.disabled]="disabled()">▾</span>

    <!-- Always in the DOM so the popup can be controlled by id even while closed, which keeps aria-controls
         pointing at something real rather than at an element that comes and goes. -->
    <ul class="popup" role="listbox" [id]="listboxId()" [hidden]="!isOpen()">
      @for (option of filtered(); track option.id) {
        <li
          class="option"
          role="option"
          [id]="optionElementId(option)"
          [class.active]="option.id === activeId()"
          [attr.aria-selected]="option.id === selected()?.id"
          [attr.aria-disabled]="option.disabled || null"
          (mousedown)="onOptionMousedown($event, option)"
        >
          <span class="row">
            <span class="label">{{ option.label }}</span>
            @if (option.id === selected()?.id) { <span class="chosen" aria-hidden="true">✓</span> }
          </span>
          @if (option.detail) { <span class="detail">{{ option.detail }}</span> }
        </li>
      }
      @if (filtered().length === 0) {
        <li class="empty" role="presentation">
          <ng-content select="[cpComboboxEmpty]">No matches</ng-content>
        </li>
      }
    </ul>

    <!-- How many rows the typing left, for someone who cannot see the list shrink. -->
    <span class="sr-only" role="status" aria-live="polite">{{ announcement() }}</span>`,
  styles: [`
    :host { display:block; position:relative; }
    /* Specific enough to beat the padding cp-field reaches in with, which the caret would otherwise sit on. */
    :host input[role='combobox'] { padding-inline-end:2rem; }
    /* Centred on the box whatever height cp-field gives the input, and never a pointer target of its own. */
    .caret { position:absolute; inset-inline-end:var(--cp-space-3); inset-block:0; display:flex; align-items:center; color:var(--cp-text-muted); font-size:var(--cp-font-size-sm); pointer-events:none; transition:transform .15s ease; }
    /* Turning over says "open" by shape as well as by the list appearing. Reduced motion is handled globally. */
    .caret.open { transform:rotate(180deg); }
    .caret.disabled { color:var(--cp-text-faint); }
    .popup { position:absolute; z-index:20; inset-inline:0; top:calc(100% + var(--cp-space-1)); max-height:16rem; overflow-y:auto; margin:0; padding:var(--cp-space-1); list-style:none; border:1px solid var(--cp-border); border-radius:var(--cp-radius-md); background:var(--cp-surface); box-shadow:var(--cp-shadow-lg); }
    .option { display:flex; flex-direction:column; justify-content:center; min-height:2.5rem; padding:var(--cp-space-1) var(--cp-space-2); border-radius:var(--cp-radius-sm); border-inline-start:3px solid transparent; color:var(--cp-text); font-size:var(--cp-font-size-sm); cursor:pointer; overflow-wrap:anywhere; }
    /* The active option shifts in weight and gains an edge marker, not only a hue. */
    .option.active { background:var(--cp-surface-subtle); border-inline-start-color:var(--cp-primary); }
    .option[aria-disabled='true'] { color:var(--cp-text-faint); cursor:not-allowed; }
    .row { display:flex; align-items:center; justify-content:space-between; gap:var(--cp-space-2); }
    .chosen { color:var(--cp-primary); }
    .detail { color:var(--cp-text-muted); font-size:var(--cp-font-size-xs); }
    .empty { padding:var(--cp-space-2); color:var(--cp-text-muted); font-size:var(--cp-font-size-sm); }
    .sr-only { position:absolute; width:1px; height:1px; overflow:hidden; clip-path:inset(50%); white-space:nowrap; }
  `],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpComboboxComponent {
  readonly options = input.required<readonly CpComboboxOption[]>();

  /** The same id the wrapping `CpFieldComponent` was given as `forId`, so its label labels this input. */
  readonly inputId = input.required<string>();

  /** The text in the box. In `restricted` mode this is a filter; in `free-text` mode it is also the answer. */
  readonly value = model('');

  /** The chosen option. Two-way, so a consumer can preselect one. */
  readonly selected = model<CpComboboxOption | null>(null);

  /**
   * `restricted`: text that matches nothing reverts on blur, and `selected` only ever holds a real option.
   * `free-text`: the text stands, and `selected` is the exactly-matching option or null.
   */
  readonly mode = input<'restricted' | 'free-text'>('restricted');

  /** False when the given list is already filtered — by a server, typically — so this does not filter it twice. */
  readonly filterLocally = input(true);

  readonly disabled = input(false);
  readonly placeholder = input('');

  /** The announced text. Replaceable so an app can localise without this library holding strings. */
  readonly resultsLabel = input<(count: number) => string>(cpComboboxResultsLabel);

  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly open = signal(false);
  private readonly active = signal<string | null>(null);

  protected readonly isOpen = computed(() => this.open() && !this.disabled());
  protected readonly activeId = this.active.asReadonly();
  protected readonly listboxId = computed(() => `${this.inputId()}-listbox`);

  protected readonly filtered = computed(() => {
    const all = this.options();
    const query = this.value().trim().toLowerCase();
    // Text that came from a selection is not a filter. Without this, a creator who chose Cups and reopens the
    // list sees only Cups, and has to clear the box before they can change their mind — which is exactly the
    // kind of dead end a keyboard-only user cannot see the way out of.
    const isSelectionText = this.selected()?.label.trim().toLowerCase() === query;

    if (!this.filterLocally() || query.length === 0 || isSelectionText) return all;

    return all.filter((option) => option.label.toLowerCase().includes(query));
  });

  protected readonly activeOptionElementId = computed(() => {
    const activeId = this.active();
    if (!this.isOpen() || activeId === null) return null;
    const option = this.filtered().find((candidate) => candidate.id === activeId);

    return option ? this.optionElementId(option) : null;
  });

  /** Silent while closed: a count nobody asked for is noise, and the list is only filtered while open. */
  protected readonly announcement = computed(() => (this.isOpen() ? this.resultsLabel()(this.filtered().length) : ''));

  protected optionElementId(option: CpComboboxOption): string {
    return `${this.inputId()}-option-${option.id}`;
  }


  /**
   * Opens the list when the box is clicked.
   *
   * Without it this control is indistinguishable from a plain text field: nothing happens until something is
   * typed, so a creator who clicks it and waits sees an ordinary input and no picker. Opening rather than
   * toggling — someone clicking into the box to put the caret somewhere has not asked to lose the list they are
   * reading; Escape and leaving the field are what close it.
   *
   * Deliberately not bound to focus. Tabbing through a form would pop open every list on the way past and
   * announce a result count nobody asked for, which is the noise {@link announcement} already declines to make
   * while closed. ArrowDown is the keyboard's way in, and always was.
   */
  protected onClick(): void {
    if (this.disabled() || this.isOpen()) return;
    this.openAt(this.selectable()[0]?.id ?? null);
  }

  protected onInput(event: Event): void {
    this.value.set((event.target as HTMLInputElement).value);
    this.open.set(true);
    this.active.set(this.selectable()[0]?.id ?? null);

    // Free text answers with itself, so the selection follows the typing: an exact match is a known thing, and
    // anything else is something new the consumer reads off `value` instead.
    if (this.mode() === 'free-text') this.selected.set(this.exactMatch());
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (this.disabled()) return;

    const options = this.selectable();

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.isOpen() ? this.move(1) : this.openAt(options[0]?.id ?? null);
        return;
      case 'ArrowUp':
        event.preventDefault();
        this.isOpen() ? this.move(-1) : this.openAt(options[options.length - 1]?.id ?? null);
        return;
      case 'Home':
        if (!this.isOpen()) return;
        event.preventDefault();
        this.activate(options[0]?.id ?? null);
        return;
      case 'End':
        if (!this.isOpen()) return;
        event.preventDefault();
        this.activate(options[options.length - 1]?.id ?? null);
        return;
      case 'Enter': {
        if (!this.isOpen()) return;
        const option = options.find((candidate) => candidate.id === this.active());
        // No active option leaves the text exactly as typed; Enter is not a commit-anything key.
        if (!option) return;
        event.preventDefault();
        this.choose(option);
        return;
      }
      case 'Escape':
        if (!this.isOpen()) return;
        // Closes and leaves the text alone: a creator who typed three words has not asked to lose them.
        event.preventDefault();
        this.close();
        return;
      case 'Tab':
        // Never prevented — focus must move. The blur that follows is what commits.
        this.close();
        return;
      default:
        return;
    }
  }

  /**
   * `mousedown`, not `click`, and prevented: the default would blur the input before the click landed, and the
   * blur handler would have reverted or committed the text under the pointer.
   */
  protected onOptionMousedown(event: MouseEvent, option: CpComboboxOption): void {
    event.preventDefault();
    if (option.disabled) return;
    this.choose(option);
  }

  protected onBlur(): void {
    this.close();

    if (this.mode() === 'free-text') {
      this.selected.set(this.exactMatch());
      return;
    }

    // Restricted: the box may only ever read as the option it stands for, or as nothing.
    const selected = this.selected();
    if (selected === null) {
      if (this.value() !== '') this.value.set('');
      return;
    }
    if (this.value() !== selected.label) this.value.set(selected.label);
  }

  private choose(option: CpComboboxOption): void {
    this.selected.set(option);
    this.value.set(option.label);
    this.close();
  }

  private openAt(id: string | null): void {
    this.open.set(true);
    this.active.set(id);
  }

  private close(): void {
    this.open.set(false);
    this.active.set(null);
  }

  private move(direction: 1 | -1): void {
    const options = this.selectable();
    if (options.length === 0) return;

    const index = options.findIndex((option) => option.id === this.active());
    // Stops at either end rather than wrapping: a list that jumps from last back to first makes a keyboard user
    // count rather than read.
    const next = index === -1 ? (direction === 1 ? 0 : options.length - 1) : Math.min(Math.max(index + direction, 0), options.length - 1);
    this.activate(options[next].id);
  }

  private activate(id: string | null): void {
    this.active.set(id);
    if (id === null) return;

    const option = this.filtered().find((candidate) => candidate.id === id);
    if (!option) return;

    // The rows are already rendered — only attributes changed — so this needs no render pass to find one.
    this.elementRef.nativeElement
      .querySelector(`[id="${this.optionElementId(option)}"]`)
      ?.scrollIntoView({ block: 'nearest' });
  }

  /** The options a key or a pointer may actually land on. */
  private selectable(): readonly CpComboboxOption[] {
    return this.filtered().filter((option) => !option.disabled);
  }

  /** Case-insensitive and trimmed: someone who types "thai" has named Thai. */
  private exactMatch(): CpComboboxOption | null {
    const typed = this.value().trim().toLowerCase();
    if (typed.length === 0) return null;

    return this.options().find((option) => !option.disabled && option.label.trim().toLowerCase() === typed) ?? null;
  }
}
