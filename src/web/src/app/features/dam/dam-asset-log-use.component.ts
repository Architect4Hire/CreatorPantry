import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import {
  CpButtonComponent,
  CpDialogComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
} from '@creator-pantry/ui';

import { ConfirmService } from '../../core/confirm.service';
import {
  DAM_USE_FIELDS,
  DAM_USE_LIMITS,
  DamAssetUtilization,
  DamUseDraft,
  DamUseField,
  damLocalDateValue,
  damUseFieldFor,
  emptyDamUseDraft,
  encodeDamUse,
  isDamUseDraftTouched,
  validateDamUseDraft,
} from '../../models/dam-asset.models';
import { DamAssetService } from '../../services/dam-asset.service';

/** The control each field's error is put beside, and focus is sent to. */
const FIELD_IDS: Readonly<Record<DamUseField, string>> = {
  platformKey: 'cp-dam-use-platform',
  utilizedOn: 'cp-dam-use-date',
  campaignName: 'cp-dam-use-campaign',
  notes: 'cp-dam-use-notes',
};

/**
 * Recording that one library asset was used: where, and on which day (DAM-UI-004).
 *
 * `CpDialogComponent` rather than `ConfirmService`: this hosts a form with its own fields and validation.
 *
 * **The date is a calendar date and is sent as one.** It defaults to today in the creator's own calendar, and
 * may be any earlier day — recording where a photograph went out last year is a creator entering their own
 * history. The weekday is never sent: the server derives it from the date, so the two cannot disagree.
 *
 * **One submission is one use.** The idempotency key is held across a retry of the same entry, because without
 * one two identical calls record two uses — and a retry after a lost response would log something that
 * happened once as twice. It is dropped whenever the entry changes.
 *
 * **A use cannot be logged against an asset that is no longer in the library.** The route answers a removed
 * asset as not found; this says so in those words and logs nothing.
 *
 * **Closing with something entered asks first**, through `ConfirmService`, where dismissing means "stay".
 */
@Component({
  selector: 'cp-dam-asset-log-use',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpFieldComponent, CpFormSectionComponent, CpNoticeComponent],
  template: `
    <cp-dialog
      [open]="open()"
      title="Log a use"
      description="Record where this picture was used, and on which day."
      titleId="cp-dam-use-title"
      (closed)="cancel()"
      (keydown.escape)="cancel()"
    >
      @if (draft(); as form) {
        <form class="form" novalidate (submit)="onSubmit($event)">
          <!-- Optionality is said once, here, rather than field by field. -->
          <p class="legend">Platform and date are needed. The rest is optional.</p>

          @if (problem()) {
            <cp-notice tone="error" role="alert">{{ problem() }}</cp-notice>
          }

          <cp-form-section heading="Where and when">
            <cp-field
              label="Platform"
              [forId]="ids.platformKey"
              [required]="true"
              hint="A short key of your own, like instagram or newsletter."
              [error]="errorFor('platformKey')"
            >
              <input
                type="text"
                [id]="ids.platformKey"
                autocomplete="off"
                [attr.maxlength]="limits.platformMaxLength"
                [value]="form.platformKey"
                (input)="set('platformKey', $event)"
              />
            </cp-field>

            <cp-field
              label="Date used"
              [forId]="ids.utilizedOn"
              [required]="true"
              hint="Today or any earlier day."
              [error]="errorFor('utilizedOn')"
            >
              <input
                type="date"
                [id]="ids.utilizedOn"
                [attr.max]="latestDate"
                [value]="form.utilizedOn"
                (input)="set('utilizedOn', $event)"
              />
            </cp-field>
          </cp-form-section>

          <cp-form-section heading="Anything else">
            <cp-field label="Campaign" [forId]="ids.campaignName" [error]="errorFor('campaignName')">
              <input
                type="text"
                [id]="ids.campaignName"
                autocomplete="off"
                [attr.maxlength]="limits.campaignMaxLength"
                [value]="form.campaignName"
                (input)="set('campaignName', $event)"
              />
            </cp-field>

            <cp-field label="Notes" [forId]="ids.notes" [error]="errorFor('notes')">
              <textarea
                rows="3"
                [id]="ids.notes"
                [attr.maxlength]="limits.notesMaxLength"
                [value]="form.notes"
                (input)="set('notes', $event)"
              ></textarea>
            </cp-field>
          </cp-form-section>

          <!-- So Enter in a text box submits. The visible button lives in the dialog's footer, outside this form. -->
          <button type="submit" hidden tabindex="-1"></button>
        </form>
      }

      <div cpDialogActions>
        <button cpButton variant="secondary" type="button" [disabled]="saving()" (click)="cancel()">Cancel</button>
        <button cpButton type="button" [disabled]="saving() || gone()" (click)="save()">
          {{ saving() ? 'Logging…' : 'Log this use' }}
        </button>
      </div>
    </cp-dialog>
  `,
  styles: [
    `
      .form {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-8);
      }
      .legend {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DamAssetLogUseComponent {
  private readonly assets = inject(DamAssetService);
  private readonly confirm = inject(ConfirmService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  readonly open = input(false);
  readonly workspaceSlug = input.required<string>();
  readonly assetId = input.required<string>();

  readonly closed = output<void>();
  /** The use as the server recorded it, weekday included. */
  readonly logged = output<DamAssetUtilization>();
  /** The asset turned out not to be in the library any more, so the page behind this should read it again. */
  readonly assetGone = output<void>();

  protected readonly limits = DAM_USE_LIMITS;
  protected readonly ids = FIELD_IDS;

  protected readonly draft = signal<DamUseDraft | null>(null);
  protected readonly saving = signal(false);
  protected readonly problem = signal('');
  /** True once the server has said this asset is not there. Nothing more can be logged from this dialog. */
  protected readonly gone = signal(false);
  private readonly fieldErrors = signal<Readonly<Partial<Record<DamUseField, string>>>>({});

  /** The last day a use may be dated, for the date picker. Worked out when the dialog opens. */
  protected latestDate = damLocalDateValue(new Date(), DAM_USE_LIMITS.maxDaysAhead);

  /** Held across a retry of the same entry; dropped when the entry changes. */
  private idempotencyKey: string | null = null;

  /** True when closing would lose something the creator entered. Public, so the page can warn before leaving. */
  readonly dirty = computed(() => {
    const draft = this.draft();

    return this.open() && draft !== null && isDamUseDraftTouched(draft);
  });

  constructor() {
    // Each opening starts from an empty entry dated today: a use left half-entered for one asset must not be
    // waiting here for the next.
    effect(() => {
      if (!this.open()) return;

      untracked(() => this.begin());
    });
  }

  protected set(field: DamUseField, event: Event): void {
    const draft = this.draft();
    const target = event.target;
    if (draft === null || !(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return;

    this.draft.set({ ...draft, [field]: target.value });
    // A different entry from the one the key was minted for.
    this.idempotencyKey = null;

    if (this.fieldErrors()[field]) {
      const { [field]: _cleared, ...rest } = this.fieldErrors();
      this.fieldErrors.set(rest);
    }
  }

  protected errorFor(field: DamUseField): string {
    return this.fieldErrors()[field] ?? '';
  }

  protected async save(): Promise<void> {
    const draft = this.draft();
    if (draft === null || this.saving() || this.gone()) return;

    const errors = validateDamUseDraft(draft);
    this.fieldErrors.set(errors);
    const firstInvalid = DAM_USE_FIELDS.find((field) => errors[field]);
    if (firstInvalid) {
      this.problem.set('');
      this.focusField(firstInvalid);
      return;
    }

    this.idempotencyKey ??= crypto.randomUUID();
    this.saving.set(true);
    this.problem.set('');

    const outcome = await this.assets.logUtilization(
      this.workspaceSlug(),
      this.assetId(),
      encodeDamUse(draft),
      this.idempotencyKey,
    );

    this.saving.set(false);

    switch (outcome.status) {
      case 'logged':
        this.logged.emit(outcome.use);
        this.end();
        return;

      case 'invalid': {
        const mapped: Partial<Record<DamUseField, string>> = {};
        for (const [name, messages] of Object.entries(outcome.fieldErrors)) {
          const field = damUseFieldFor(name);
          if (field !== null && messages.length > 0) mapped[field] = messages[0];
        }

        this.fieldErrors.set(mapped);
        this.problem.set(Object.keys(mapped).length > 0 ? 'This use could not be logged as entered.' : outcome.message);

        const first = DAM_USE_FIELDS.find((field) => mapped[field]);
        if (first) this.focusField(first);
        return;
      }

      case 'forbidden':
        this.problem.set('You need Contributor access in this workspace to log a use. Nothing was logged.');
        return;

      case 'not_found':
        this.gone.set(true);
        this.problem.set("This picture is no longer in the library, so a use can't be logged.");
        this.assetGone.emit();
        return;

      default:
        this.problem.set('The use could not be logged just now. What you entered is still here, so try again.');
    }
  }

  /** Close without logging. Asks first when that would lose something, and never while a save is running. */
  protected async cancel(): Promise<void> {
    if (this.saving()) return;

    // Nothing entered here can be used once the asset is gone, so there is nothing to ask about.
    if (this.dirty() && !this.gone()) {
      const discard = await this.confirm.confirm({
        title: 'Discard this entry?',
        message: 'What you entered here has not been logged, and closing now will lose it.',
        confirmLabel: 'Discard entry',
        cancelLabel: 'Keep editing',
      });
      if (!discard) return;
    }

    this.end();
  }

  protected onSubmit(event: Event): void {
    event.preventDefault();
    void this.save();
  }

  private begin(): void {
    const today = new Date();

    this.latestDate = damLocalDateValue(today, DAM_USE_LIMITS.maxDaysAhead);
    this.draft.set(emptyDamUseDraft(today));
    this.idempotencyKey = null;
    this.saving.set(false);
    this.problem.set('');
    this.gone.set(false);
    this.fieldErrors.set({});
  }

  private end(): void {
    this.draft.set(null);
    this.closed.emit();
  }

  private focusField(field: DamUseField): void {
    // After the render that shows the error, so the control is described by it when focus arrives.
    setTimeout(() => {
      this.host.nativeElement.querySelector<HTMLElement>(`[id="${FIELD_IDS[field]}"]`)?.focus();
    });
  }
}
