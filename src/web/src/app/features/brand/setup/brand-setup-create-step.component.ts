import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpChoiceGroupComponent,
  CpChoiceOption,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { AiUsageService } from '../../../services/ai-usage.service';
import { BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { AiAllowanceNoticeComponent } from '../../../shared/ai-allowance-notice/ai-allowance-notice.component';
import { allowanceBlocksNewRequests } from '../../../shared/allowance-gate';
import {
  CreateChoice,
  GuideMethod,
  NO_CHOICE,
  SourceRow,
  SourceState,
  checkedIdsOf,
  decodeCreateChoice,
  isUsed,
  sameChoice,
  selectedSources,
  sourceStateOf,
  usedSummary,
} from './brand-setup-create';
import { BrandSetupExampleComponent } from './brand-setup-example.component';
import { formatSize, kindOption } from './brand-setup-examples';

/**
 * Step 5 of "Create my voice": the creator chooses whether a first draft of their guide is written for them, or
 * they write it themselves.
 *
 * **It decides; it does not do.** Nothing is generated, requested or activated here, and pressing Continue
 * starts no provider call — the choice is recorded in the creator's own draft and the step after it acts on
 * that. Choosing a draft therefore has to be *said* to spend an allowance rather than quietly spending one,
 * which is what the acknowledgement is: a sentence the creator ticks, kept with their answers so they are
 * never asked it twice.
 *
 * **A spent allowance is not a dead end.** A known-spent or switched-off allowance disables the draft and
 * leaves writing it themselves available, so setup can always be finished. An allowance nobody has read yet,
 * or one we are no longer sure of, blocks nothing — `allowanceBlocksNewRequests` is the single definition of
 * that, and the server is the authority either way.
 *
 * **No model, provider or prompt vocabulary, advanced or otherwise.** There is nothing of the sort to choose:
 * what gets used is the creator's own answers and the examples they already picked, named by their own titles,
 * and the disclosure says so in words rather than showing context, chunks or a prompt.
 */
@Component({
  selector: 'cp-brand-setup-create-step',
  standalone: true,
  imports: [
    AiAllowanceNoticeComponent,
    BrandSetupExampleComponent,
    CpButtonComponent,
    CpCheckboxComponent,
    CpChoiceGroupComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
  ],
  templateUrl: './brand-setup-create-step.component.html',
  styleUrl: './brand-setup-create-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupCreateStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  /** The examples step's saved slice: which examples the creator added. */
  readonly sources = input<Readonly<Record<string, unknown>> | null>(null);
  /** The text-checking step's saved slice: which of those the creator said look right. */
  readonly checked = input<Readonly<Record<string, unknown>> | null>(null);
  readonly workspaceSlug = input.required<string>();
  readonly reported = output<BrandSetupStepReport>();

  private readonly documents = inject(BrandSourceDocumentService);
  private readonly usage = inject(AiUsageService);

  protected readonly method = signal<GuideMethod | null>(null);
  protected readonly acknowledged = signal(false);
  protected readonly rows = signal<readonly SourceRow[]>([]);
  protected readonly checkedIds = signal<ReadonlySet<string>>(new Set<string>());
  protected readonly loaded = signal(false);
  protected readonly retrying = signal(false);
  protected readonly touched = signal(false);

  /** The account's allowance, so a spent one is said before the choice rather than after it. */
  protected readonly allowance = this.usage.allowance;

  private baseline: CreateChoice = NO_CHOICE;
  private hadSavedDraft = false;
  private ready = false;
  /** Once the creator has changed anything, every later state is reported, including a return to the start. */
  private edited = false;

  protected readonly entries = computed(() =>
    this.rows().map((row) => ({ row, state: sourceStateOf(row, this.checkedIds()) })),
  );
  protected readonly noSources = computed(() => this.rows().length === 0);
  protected readonly unreachableCount = computed(() => this.entries().filter((entry) => entry.state === 'unreachable').length);
  protected readonly summary = computed(() => {
    const used = this.entries().filter((entry) => isUsed(entry.state));
    return usedSummary(
      used.filter((entry) => entry.state !== 'visual').length,
      used.filter((entry) => entry.state === 'visual').length,
    );
  });
  protected readonly usedTitles = computed(() =>
    this.entries().filter((entry) => isUsed(entry.state)).map((entry) => this.titleOf(entry.row)),
  );

  protected readonly picked = computed<readonly string[]>(() => {
    const method = this.method();
    return method === null ? [] : [method];
  });

  protected readonly draftBlocked = computed(() => allowanceBlocksNewRequests(this.allowance()));
  protected readonly needsAck = computed(() => this.method() === 'draft' && !this.acknowledged());

  protected readonly choices = computed<readonly CpChoiceOption[]>(() => [
    {
      value: 'draft',
      label: 'Draft it for me',
      hint: "We write a first draft from what's above. You read it next and change anything that doesn't sound like you.",
      disabled: this.draftBlocked(),
    },
    {
      value: 'myself',
      label: "I'll write it myself",
      hint: 'You get the sections laid out and empty, and you fill them in. Nothing is written for you.',
    },
  ]);

  protected readonly canContinue = computed(() => {
    const method = this.method();
    if (method === null) return false;
    if (method === 'myself') return true;
    return !this.needsAck() && !this.draftBlocked();
  });

  protected readonly nextLine = computed(() => {
    switch (this.method()) {
      case 'draft':
        return "Next: your draft is written, then you read it section by section and change anything that doesn't sound like you.";
      case 'myself':
        return "Next: you'll see your guide's sections, ready for you to fill in.";
      default:
        return '';
    }
  });

  constructor() {
    effect(() => {
      const choice: CreateChoice = { method: this.method(), costAcknowledged: this.acknowledged() };
      const canContinue = this.canContinue();
      if (!this.ready) return;

      const dirty = !sameChoice(choice, this.baseline);
      // Nothing is picked to begin with, so nothing is saved until the creator chooses something.
      if (dirty) this.edited = true;
      const draft = this.edited || this.hadSavedDraft ? ({ ...choice } as Record<string, unknown>) : null;
      this.reported.emit({ canContinue, isDirty: dirty, draft });
    });
  }

  ngOnInit(): void {
    const saved = this.draft();
    this.hadSavedDraft = saved !== null && Object.keys(saved).length > 0;
    this.baseline = decodeCreateChoice(this.hadSavedDraft ? saved : null);
    this.method.set(this.baseline.method);
    this.acknowledged.set(this.baseline.costAcknowledged);
    this.checkedIds.set(checkedIdsOf(this.checked()));
    this.ready = true;

    void this.usage.ensureLoaded();

    const selected = selectedSources(this.sources());
    this.rows.set(selected.map((source) => ({ documentId: source.documentId, kind: source.kind, detail: null, load: 'loading' as const })));
    void this.loadAll();
  }

  // ---- The choice ----

  /** The group reports an array whether it takes one answer or many; one answer is its only element. */
  protected setMethod(picked: readonly string[]): void {
    const value = picked[0];
    if (value !== 'draft' && value !== 'myself') return;
    this.touched.set(true);
    this.method.set(value);
  }

  protected setAcknowledged(on: boolean): void {
    this.acknowledged.set(on);
  }

  // ---- Looking up the examples ----

  private async loadAll(): Promise<void> {
    await Promise.all(this.rows().map((row) => this.loadRow(row.documentId)));
    this.loaded.set(true);
  }

  protected async retry(): Promise<void> {
    if (this.retrying()) return;
    this.retrying.set(true);
    const again = this.entries().filter((entry) => entry.state === 'unreachable').map((entry) => entry.row.documentId);
    await Promise.all(again.map((id) => this.loadRow(id)));
    this.retrying.set(false);
  }

  private async loadRow(documentId: string): Promise<void> {
    const outcome = await this.documents.get(this.workspaceSlug(), documentId);
    if (outcome.status === 'ok') {
      this.patchRow(documentId, { load: 'ready', detail: outcome.document });
    } else if (outcome.status === 'not_found') {
      this.patchRow(documentId, { load: 'ready', detail: null });
    } else {
      this.patchRow(documentId, { load: 'unreachable' });
    }
  }

  private patchRow(documentId: string, change: Partial<SourceRow>): void {
    this.rows.update((all) => all.map((row) => (row.documentId === documentId ? { ...row, ...change } : row)));
  }

  // ---- Presentation ----

  protected titleOf(row: SourceRow): string {
    return row.detail?.title ?? 'An example you added';
  }

  protected metaOf(row: SourceRow): string {
    const kind = kindOption(row.kind).label;
    return row.detail ? `${kind}, ${row.detail.fileName}, ${formatSize(row.detail.sizeBytes)}` : kind;
  }

  /** What each state is called and what it means for the guide, in one place. */
  private static readonly STATES: Readonly<Record<SourceState, { tone: CpStatusPillTone; status: string; note: string }>> = {
    loading: { tone: 'progress', status: 'Looking it up…', note: '' },
    checked: { tone: 'success', status: 'Checked by you', note: 'The words you checked are used exactly as you left them.' },
    unchecked: {
      tone: 'neutral',
      status: 'Not checked',
      note: "You didn't check this one, so the words will be used as we read them. You can go back and check it.",
    },
    visual: { tone: 'neutral', status: 'A look you like', note: 'Used for your photo style, not for your words.' },
    'left-out': { tone: 'neutral', status: 'Left out', note: "Left out earlier, so it won't be used here." },
    missing: { tone: 'warning', status: 'Not available', note: "We can't find this one any more, so it won't be used." },
    unreachable: {
      tone: 'warning',
      status: "Couldn't check",
      note: "We couldn't reach this one just now. It's still saved and will still be used.",
    },
  };

  protected toneOf(state: SourceState): CpStatusPillTone {
    return BrandSetupCreateStepComponent.STATES[state].tone;
  }

  protected statusOf(state: SourceState): string {
    return BrandSetupCreateStepComponent.STATES[state].status;
  }

  protected noteOf(state: SourceState): string {
    return BrandSetupCreateStepComponent.STATES[state].note;
  }
}
