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
  viewChild,
} from '@angular/core';

let cpValidationSummaryUid = 0;

/** How much a problem matters. Only `error` is something to fix; the other two are worth knowing. */
export type CpValidationSeverity = 'error' | 'warning' | 'info';

/**
 * One thing to say about one place in a form. `path` is whatever the consumer uses to find the field again;
 * the library never reads it, only hands it back through {@link CpValidationSummaryComponent.issueSelected}.
 */
export interface CpValidationIssue {
  readonly path: string;
  readonly message: string;
  readonly severity: CpValidationSeverity;
}

const GLYPHS: Readonly<Record<CpValidationSeverity, string>> = { error: '!', warning: '▲', info: 'ℹ' };

/**
 * Everything wrong with a form, in one place, each item a way to get to the field it is about.
 *
 * **Why a summary at all.** A field's own message is beside the field, which is the right place to fix it and
 * the wrong place to find out *that* something is wrong: a long form has fields below the fold, and a recipe
 * editor has whole tabs the creator is not looking at. A save that is refused says so here, once, and every
 * line is a control that takes the creator to the field.
 *
 * **Two lists, and only one of them is a refusal.** Errors stop a save and are headed with their count.
 * Warnings and information never do, so they sit below under their own heading and a form with only advice
 * still reads as one that can be saved. Severity is a glyph as well as a colour — a disc for an error, a
 * triangle for a warning, an `i` for information — so no one has to tell two hues apart.
 *
 * **Presentation only.** It decides nothing: which issues exist, what each is called and what selecting one
 * does are the consumer's. It emits {@link issueSelected} and leaves the tab, the scroll and the focus to the
 * form, which knows where its fields are. With no issues it draws nothing.
 *
 * **Announcing.** A status region is always in the DOM and gets its text when the count changes, because a
 * live region that appears together with its text is announced unreliably. A form that has just refused a
 * save calls {@link focus} to bring a keyboard or screen-reader user straight to the list.
 */
@Component({
  selector: 'cp-validation-summary',
  standalone: true,
  templateUrl: './validation-summary.component.html',
  styleUrl: './validation-summary.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class CpValidationSummaryComponent {
  /** Everything to say, in the order to say it. Only the visible ones belong here — what is shown is the consumer's call. */
  readonly issues = input.required<readonly CpValidationIssue[]>();

  /** What to call the place an issue is about — a field's label — or nothing, for a message that stands alone. */
  readonly labelOf = input<(path: string) => string>(() => '');

  /**
   * Whether this keeps a status region of its own that announces the error count. On by default. A form that
   * already announces a refusal itself — a save banner with `role="alert"` — turns it off, so one refusal is not
   * read out twice.
   */
  readonly announce = input(true);

  /** The issue the creator chose. The consumer reveals its field; this does not scroll or focus anything. */
  readonly issueSelected = output<CpValidationIssue>();

  protected readonly errors = computed(() => this.issues().filter((issue) => issue.severity === 'error'));
  protected readonly advice = computed(() => this.issues().filter((issue) => issue.severity !== 'error'));

  protected readonly errorHeading = computed(() => {
    const count = this.errors().length;

    return count === 1 ? '1 problem to fix' : `${count} problems to fix`;
  });

  protected readonly adviceHeading = computed(() => {
    const count = this.advice().length;

    return count === 1 ? '1 thing worth a look' : `${count} things worth a look`;
  });

  /** What the status region says. Errors only: advice is there to read, and is not worth interrupting for. */
  protected readonly announcement = computed(() => (this.errors().length > 0 ? this.errorHeading() : ''));

  private readonly uid = cpValidationSummaryUid++;
  protected readonly headingId = `cp-validation-summary-heading-${this.uid}`;
  protected readonly adviceHeadingId = `cp-validation-summary-advice-${this.uid}`;

  private readonly panel = viewChild<ElementRef<HTMLElement>>('panel');
  private readonly injector = inject(Injector);

  protected glyph(issue: CpValidationIssue): string {
    return GLYPHS[issue.severity];
  }

  /**
   * Moves focus to the list. For a form that has just refused a save.
   *
   * A form calls this in the same breath as it sets the issues, so the list usually is not drawn yet: when there
   * is no panel it waits for the next render and focuses it then. If that render still has nothing to show, it
   * does nothing.
   */
  focus(): void {
    const panel = this.panel();
    if (panel) {
      panel.nativeElement.focus();

      return;
    }

    afterNextRender(() => this.panel()?.nativeElement.focus(), { injector: this.injector });
  }
}
