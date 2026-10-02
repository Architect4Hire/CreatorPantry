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
} from '@angular/core';
import {
  CpButtonComponent,
  CpCheckboxComponent,
  CpFieldComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import {
  BrandGuideChoice,
  BrandGuideControlStatus,
  BrandGuideSelection,
  brandGuideChoiceKey,
} from '../../models/brand-guide-control.models';
import {
  BRAND_VISUAL_MAX_REFERENCES,
  BrandVisualConflict,
  BrandVisualReference,
  BrandVisualStyleLine,
  BrandVisualUse,
  resolveBrandVisualConflicts,
  resolveBrandVisualUse,
} from '../../models/brand-visual-style-control.models';

let nextId = 0;

/**
 * Which visual style a photography or image-prompt screen will use, in one small control (11A.21b, BRAND-008).
 *
 * The sibling of `cp-brand-guide-control`, not a mode of it: the look has reference examples, an "avoid"
 * summary and conflicts the voice does not, and composing beside it keeps each vocabulary honest. It shares the
 * selection and stale rules with it through `resolveBrandVisualUse`.
 *
 * <strong>It never uploads and never activates.</strong> References are documents already in the creator's
 * library; "Add a reference" only asks the host to go there. A guide is chosen for this piece, never made
 * active from here.
 *
 * <strong>It tells the creator first what the server would only report afterwards</strong> — a reference with
 * no readable text, a guide that is not the active one — and it never swaps in another guide silently. No blob
 * path, embedding or provider term appears anywhere in it.
 */
@Component({
  selector: 'cp-brand-visual-style-control',
  standalone: true,
  imports: [CpButtonComponent, CpCheckboxComponent, CpFieldComponent, CpStatusPillComponent],
  templateUrl: './brand-visual-style-control.component.html',
  styleUrl: './brand-visual-style-control.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandVisualStyleControlComponent {
  private readonly injector = inject(Injector);
  private readonly uid = `cp-brand-visual-${nextId++}`;

  protected readonly headingId = `${this.uid}-heading`;
  protected readonly selectId = `${this.uid}-select`;
  protected readonly countId = `${this.uid}-count`;
  protected readonly maxReferences = BRAND_VISUAL_MAX_REFERENCES;

  readonly status = input<BrandGuideControlStatus>('ready');
  readonly activeGuide = input<BrandGuideChoice | null>(null);
  readonly choices = input<readonly BrandGuideChoice[]>([]);
  readonly selection = input<BrandGuideSelection>({ kind: 'default' });
  readonly staleAcknowledged = input(false);

  /** Whether the guide that would be used describes any visual style. */
  readonly hasVisualGuidance = input(true);

  /** The look, in the creator's own words: identity, photography direction, prompt guidance. */
  readonly styleLines = input<readonly BrandVisualStyleLine[]>([]);

  /** One line of what to keep out of the picture, in the creator's words. Null when the guide names none. */
  readonly negativeGuidance = input<string | null>(null);

  /** Library documents that could serve as references. */
  readonly references = input<readonly BrandVisualReference[]>([]);
  readonly selectedReferenceIds = input<readonly string[]>([]);
  readonly referencesStatus = input<'loading' | 'error' | 'ready'>('ready');

  readonly selectionChange = output<BrandGuideSelection>();
  readonly staleAcknowledgedChange = output<boolean>();
  readonly selectedReferenceIdsChange = output<readonly string[]>();
  readonly reviewGuide = output<void>();
  readonly setUpGuide = output<void>();

  /** The creator asked to add a reference. The host takes them to the library; nothing is uploaded here. */
  readonly addReference = output<void>();
  readonly retry = output<void>();

  protected readonly changing = signal(false);

  private readonly changeButton = viewChild('changeButton', { read: ElementRef<HTMLButtonElement> });
  private readonly selectEl = viewChild('guideSelect', { read: ElementRef<HTMLSelectElement> });

  protected readonly use = computed<BrandVisualUse>(() =>
    resolveBrandVisualUse({
      selection: this.selection(),
      activeGuide: this.activeGuide(),
      choices: this.choices(),
      staleAcknowledged: this.staleAcknowledged(),
      hasVisualGuidance: this.hasVisualGuidance(),
    }),
  );

  protected readonly guide = computed(() => {
    const use = this.use();

    return use.kind === 'guide' || use.kind === 'no-visual-guide' ? use.guide : null;
  });

  protected readonly conflicts = computed<BrandVisualConflict[]>(() =>
    resolveBrandVisualConflicts({
      use: this.use(),
      activeGuide: this.activeGuide(),
      references: this.references(),
      selectedReferenceIds: this.selectedReferenceIds(),
    }),
  );

  protected readonly referencesUsed = computed(() => {
    const use = this.use();

    return use.kind === 'guide' || use.kind === 'no-visual-guide';
  });

  protected readonly atLimit = computed(() => this.selectedReferenceIds().length >= this.maxReferences);

  protected readonly summary = computed(() => {
    const use = this.use();

    switch (use.kind) {
      case 'guide':
        return use.source === 'default'
          ? `Using your visual style: ${use.guide.name}, version ${use.guide.versionNumber}.`
          : `Using ${use.guide.name}, version ${use.guide.versionNumber}, for this piece only.`;
      case 'no-visual-guide':
        return `${use.guide.name}, version ${use.guide.versionNumber}, does not describe a visual style yet, so there is no look to follow.`;
      case 'none':
        return use.reason === 'chosen'
          ? 'Making this without your visual style.'
          : 'You have not set up a brand style guide yet, so there is no visual style to follow.';
      case 'unavailable':
        return 'The guide you chose is no longer available.';
    }
  });

  protected readonly pill = computed<{ tone: CpStatusPillTone; label: string }>(() => {
    const use = this.use();

    switch (use.kind) {
      case 'guide':
      case 'no-visual-guide':
        if (use.stale) return { tone: 'stale', label: use.blocked ? 'May be out of date' : 'Out of date' };
        if (use.kind === 'no-visual-guide') return { tone: 'neutral', label: 'No visual style' };

        return use.source === 'default'
          ? { tone: 'success', label: 'Visual style on' }
          : { tone: 'progress', label: 'This piece only' };
      case 'none':
        return { tone: 'neutral', label: 'Visual style off' };
      case 'unavailable':
        return { tone: 'error', label: 'Unavailable' };
    }
  });

  protected readonly staleBlocked = computed(() => {
    const use = this.use();

    return (use.kind === 'guide' || use.kind === 'no-visual-guide') && use.blocked;
  });

  protected readonly staleAccepted = computed(() => {
    const use = this.use();

    return (use.kind === 'guide' || use.kind === 'no-visual-guide') && use.stale && !use.blocked;
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
      ? 'My visual style (none active yet)'
      : `My visual style: ${active.name}, version ${active.versionNumber} (default)`;
  });

  protected isSelected(documentId: string): boolean {
    return this.selectedReferenceIds().includes(documentId);
  }

  protected isLocked(reference: BrandVisualReference): boolean {
    return !this.referencesUsed() || (!this.isSelected(reference.documentId) && this.atLimit());
  }

  protected choiceKey(choice: BrandGuideChoice): string {
    return brandGuideChoiceKey(choice);
  }

  protected conflictText(conflict: BrandVisualConflict): string {
    switch (conflict.kind) {
      case 'reference-unusable':
        return `${conflict.title}: ${conflict.reason}`;
      case 'reference-missing':
        return 'A reference you chose is no longer in your library, so it will not be used.';
      case 'guide-not-active':
        return `${conflict.name}, version ${conflict.versionNumber}, is not your active visual style.`;
    }
  }

  protected toggleReference(documentId: string, checked: boolean): void {
    const current = this.selectedReferenceIds();

    if (checked === current.includes(documentId)) return;

    this.selectedReferenceIdsChange.emit(
      checked ? [...current, documentId] : current.filter((id) => id !== documentId),
    );
  }

  protected removeReference(documentId: string): void {
    this.selectedReferenceIdsChange.emit(this.selectedReferenceIds().filter((id) => id !== documentId));
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

    // Accepting one stale guide never carries over to another.
    if (this.staleAcknowledged()) this.staleAcknowledgedChange.emit(false);

    this.selectionChange.emit(next);
    afterNextRender(() => this.changeButton()?.nativeElement.focus(), { injector: this.injector });
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
