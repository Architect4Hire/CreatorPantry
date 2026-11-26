import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';

/** One answer the creator can choose. `value` is what the consumer keeps; `label` is what is shown. */
export interface CpMenuSelectOption {
  readonly value: string;
  readonly label: string;
}

/** Something the menu does rather than a value it chooses — "Create workspace…". Never the current value. */
export interface CpMenuSelectAction {
  readonly id: string;
  readonly label: string;
}

let nextMenuId = 0;

/**
 * A choice among a few named answers, drawn in the theme: a button that shows the current answer and opens a
 * menu of the rest.
 *
 * It exists because a native `<select>`'s open list is painted by the operating system, so its hover and
 * selected rows come out in the OS's blue and cannot be themed. Where the select sits in a form or a toolbar
 * and its list is not on show for long, `cp-field` and `cp-select-field` still wrap one; use this where the
 * control lives on a surface the creator looks at all day — the sidebar's workspace switcher — and the open
 * list should read as part of the product.
 *
 * It follows the WAI-ARIA menu-button pattern. Options are `menuitemradio`s with exactly one checked; an
 * `actions` entry is a `menuitem`, below a divider, because it does something rather than choosing a value.
 * Focus lands on the current option when the menu opens, the arrow keys, Home and End move between rows,
 * Escape closes and returns focus to the button, and Tab, a press outside, or focus leaving closes it.
 *
 * Controlled: it keeps no value of its own. `value` is the consumer's, `selected` says what the creator picked,
 * and the button shows the new answer only when the consumer passes it back — so a pick that is refused (a
 * navigation that fails) leaves the old answer showing. No fetching, no domain vocabulary, no router.
 */
@Component({
  selector: 'cp-menu-select',
  standalone: true,
  templateUrl: './menu-select.component.html',
  styleUrl: './menu-select.component.scss',
  host: {
    '(focusout)': 'onFocusOut($event)',
    '(document:pointerdown)': 'onDocumentPointerDown($event)',
  },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpMenuSelectComponent {
  /** What this chooses, for a screen reader ("Switch workspace"). Names the button and the menu; not shown. */
  readonly label = input.required<string>();
  readonly options = input.required<readonly CpMenuSelectOption[]>();
  /** The `value` of the chosen option, or null when none is. The consumer owns it. */
  readonly value = input<string | null>(null);
  /** Shown on the button when `value` matches no option. */
  readonly placeholder = input('Choose…');
  /** Things the menu does, listed after the options below a divider. */
  readonly actions = input<readonly CpMenuSelectAction[]>([]);

  /** The `value` the creator picked. Not emitted for the option already chosen. */
  readonly selected = output<string>();
  /** The `id` of the action the creator picked. */
  readonly actionChosen = output<string>();

  protected readonly open = signal(false);
  protected readonly menuId = `cp-menu-select-${nextMenuId++}`;

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private readonly items = viewChildren<ElementRef<HTMLButtonElement>>('item');

  protected readonly current = computed(() => this.options().find((option) => option.value === this.value()));
  protected readonly triggerText = computed(() => this.current()?.label ?? this.placeholder());
  protected readonly triggerName = computed(() => {
    const current = this.current();
    return current === undefined ? this.label() : `${this.label()}, current: ${current.label}`;
  });

  protected toggle(): void {
    if (this.open()) {
      this.open.set(false);
    } else {
      this.openMenu('current');
    }
  }

  protected onTriggerKeydown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.openMenu(event.key === 'ArrowDown' ? 'current' : 'last');
    }
  }

  protected onMenuKeydown(event: KeyboardEvent): void {
    const items = this.items().map((item) => item.nativeElement);
    const index = items.indexOf(document.activeElement as HTMLButtonElement);

    switch (event.key) {
      case 'ArrowDown':
        items[(index + 1) % items.length]?.focus();
        break;
      case 'ArrowUp':
        items[(index - 1 + items.length) % items.length]?.focus();
        break;
      case 'Home':
        items[0]?.focus();
        break;
      case 'End':
        items[items.length - 1]?.focus();
        break;
      case 'Escape':
        this.closeToTrigger();
        break;
      case 'Tab':
        // Focus moves on naturally; the menu is just no longer open behind it.
        this.open.set(false);
        return;
      default:
        return;
    }
    event.preventDefault();
  }

  /** Focus leaving the control altogether (a click elsewhere, a screen reader's virtual cursor) closes it. */
  protected onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (this.open() && next && !this.host.nativeElement.contains(next)) {
      this.open.set(false);
    }
  }

  protected onDocumentPointerDown(event: PointerEvent): void {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) {
      this.open.set(false);
    }
  }

  protected choose(option: CpMenuSelectOption): void {
    this.closeToTrigger();
    if (option.value !== this.value()) {
      this.selected.emit(option.value);
    }
  }

  protected act(action: CpMenuSelectAction): void {
    // Focus goes back to the button first, so something the action opens that returns focus lands there.
    this.closeToTrigger();
    this.actionChosen.emit(action.id);
  }

  private openMenu(focus: 'current' | 'last'): void {
    this.open.set(true);
    afterNextRender(
      () => {
        const items = this.items().map((item) => item.nativeElement);
        const target =
          focus === 'last'
            ? items[items.length - 1]
            : (items.find((item) => item.getAttribute('aria-checked') === 'true') ?? items[0]);
        target?.focus();
      },
      { injector: this.injector },
    );
  }

  private closeToTrigger(): void {
    this.open.set(false);
    this.trigger()?.nativeElement.focus();
  }
}
