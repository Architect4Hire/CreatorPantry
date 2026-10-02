import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
  ElementRef,
} from '@angular/core';
import { CpButtonComponent, CpFieldComponent, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

import {
  BrandGuideChannelRule,
  BrandGuideChoice,
  BrandGuideControlStatus,
  BrandGuideRulesStatus,
  BrandGuideSelection,
  BrandGuideUse,
  brandGuideChoiceKey,
  resolveBrandGuideUse,
} from '../../models/brand-guide-control.models';

let nextId = 0;

/**
 * Which brand guide a writing screen will use, in one small control (11A.21a, BRAND-007).
 *
 * Shared by the editorial, SEO and social setup screens so a creator learns it once and reads the same words
 * everywhere. Presentation only: it takes what the host already knows and emits what the creator chose. It
 * fetches nothing, and it never receives a chunk, a prompt, an embedding or a workspace id.
 *
 * <strong>No silent fallback.</strong> A stale guide shows a warning and the creator must continue or review it
 * explicitly; a chosen guide that has disappeared is reported, not swapped for the active one. The host reads
 * `resolveBrandGuideUse` for the same verdict, so Submit and this control cannot disagree.
 *
 * <strong>Advanced provenance stays behind "Guide details".</strong> The primary surface is a sentence, a
 * Change button and a plain-language list of what the channel's rules mean for the writing.
 */
@Component({
  selector: 'cp-brand-guide-control',
  standalone: true,
  imports: [CpButtonComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './brand-guide-control.component.html',
  styleUrl: './brand-guide-control.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandGuideControlComponent {
  private readonly injector = inject(Injector);
  private readonly uid = `cp-brand-guide-${nextId++}`;

  protected readonly headingId = `${this.uid}-heading`;
  protected readonly selectId = `${this.uid}-select`;
  protected readonly detailsId = `${this.uid}-details`;

  /** Whether the host has the guide information yet. */
  readonly status = input<BrandGuideControlStatus>('ready');

  /** The workspace's active guide, or null when nothing has been approved yet. */
  readonly activeGuide = input<BrandGuideChoice | null>(null);

  /** Other versions or guides a creator may pick instead. The active guide need not be repeated here. */
  readonly choices = input<readonly BrandGuideChoice[]>([]);

  readonly selection = input<BrandGuideSelection>({ kind: 'default' });

  /** True once the creator has chosen to continue with a stale guide. Reset by the control on any change. */
  readonly staleAcknowledged = input(false);

  /** What the channel's rules mean for this piece, already in plain language. */
  readonly rules = input<readonly BrandGuideChannelRule[]>([]);
  readonly rulesStatus = input<BrandGuideRulesStatus>('ready');

  readonly selectionChange = output<BrandGuideSelection>();
  readonly staleAcknowledgedChange = output<boolean>();
  readonly reviewGuide = output<void>();
  readonly setUpGuide = output<void>();
  readonly retry = output<void>();

  protected readonly changing = signal(false);

  private readonly changeButton = viewChild('changeButton', { read: ElementRef<HTMLButtonElement> });
  private readonly selectEl = viewChild('guideSelect', { read: ElementRef<HTMLSelectElement> });

  protected readonly use = computed<BrandGuideUse>(() =>
    resolveBrandGuideUse({
      selection: this.selection(),
      activeGuide: this.activeGuide(),
      choices: this.choices(),
      staleAcknowledged: this.staleAcknowledged(),
    }),
  );

  protected readonly guide = computed(() => {
    const use = this.use();

    return use.kind === 'guide' ? use.guide : null;
  });

  protected readonly summary = computed(() => {
    const use = this.use();

    switch (use.kind) {
      case 'guide':
        return use.source === 'default'
          ? `Using your brand voice: ${use.guide.name}, version ${use.guide.versionNumber}.`
          : `Using ${use.guide.name}, version ${use.guide.versionNumber}, for this piece only.`;
      case 'none':
        if (use.reason === 'chosen') return 'Writing without a brand voice.';

        return this.choices().length === 0
          ? 'You have not set up a brand voice yet, so this will be written without one.'
          : 'No brand voice is active, so this will be written without one.';
      case 'unavailable':
        return 'The guide you chose is no longer available.';
    }
  });

  protected readonly pill = computed<{ tone: CpStatusPillTone; label: string }>(() => {
    const use = this.use();

    switch (use.kind) {
      case 'guide':
        if (use.stale) return { tone: 'stale', label: use.blocked ? 'May be out of date' : 'Out of date' };

        return use.source === 'default'
          ? { tone: 'success', label: 'Brand voice on' }
          : { tone: 'progress', label: 'This piece only' };
      case 'none':
        return { tone: 'neutral', label: 'Brand voice off' };
      case 'unavailable':
        return { tone: 'error', label: 'Unavailable' };
    }
  });

  protected readonly staleBlocked = computed(() => {
    const use = this.use();

    return use.kind === 'guide' && use.blocked;
  });

  protected readonly staleAccepted = computed(() => {
    const use = this.use();

    return use.kind === 'guide' && use.stale && !use.blocked;
  });

  protected readonly staleReason = computed(
    () => this.guide()?.staleReason?.trim() || 'The sources behind it have changed since you approved it.',
  );

  protected readonly otherChoices = computed(() => {
    const active = this.activeGuide();

    return this.choices().filter(
      (choice) =>
        active === null || choice.guideId !== active.guideId || choice.versionNumber !== active.versionNumber,
    );
  });

  /** The select's value, including one for a chosen guide that is gone so the control never shows a false one. */
  protected readonly selectValue = computed(() => {
    const selection = this.selection();

    if (selection.kind === 'default') return 'default';
    if (selection.kind === 'none') return 'none';

    return brandGuideChoiceKey(selection);
  });

  protected readonly unavailableKey = computed(() => (this.use().kind === 'unavailable' ? this.selectValue() : null));

  protected readonly defaultLabel = computed(() => {
    const active = this.activeGuide();

    return active === null
      ? 'My brand voice (none active yet)'
      : `My brand voice: ${active.name}, version ${active.versionNumber} (default)`;
  });

  protected readonly approvedOn = computed(() => formatDate(this.guide()?.approvedAt ?? null));

  protected choiceKey(choice: BrandGuideChoice): string {
    return brandGuideChoiceKey(choice);
  }

  protected toggleChange(): void {
    const opening = !this.changing();

    this.changing.set(opening);

    if (opening) afterNextRender(() => this.selectEl()?.nativeElement.focus(), { injector: this.injector });
  }

  protected onSelect(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    const next = this.parse(value);

    if (next === null) return;

    this.changing.set(false);

    // A different choice is a different guide: acceptance of one stale guide never carries to another.
    if (this.staleAcknowledged()) this.staleAcknowledgedChange.emit(false);

    this.selectionChange.emit(next);
    afterNextRender(() => this.changeButton()?.nativeElement.focus(), { injector: this.injector });
  }

  protected continueAnyway(): void {
    this.staleAcknowledgedChange.emit(true);
  }

  protected undoContinue(): void {
    this.staleAcknowledgedChange.emit(false);
  }

  private parse(value: string): BrandGuideSelection | null {
    if (value === 'default') return { kind: 'default' };
    if (value === 'none') return { kind: 'none' };

    const choice = this.choices().find((c) => brandGuideChoiceKey(c) === value);

    return choice === undefined
      ? null
      : { kind: 'override', guideId: choice.guideId, versionNumber: choice.versionNumber };
  }
}

function formatDate(iso: string | null): string | null {
  if (iso === null) return null;

  const date = new Date(iso);

  return Number.isNaN(date.getTime()) ? null : new Intl.DateTimeFormat('en', { dateStyle: 'medium' }).format(date);
}
