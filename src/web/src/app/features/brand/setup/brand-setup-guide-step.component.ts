import { ChangeDetectionStrategy, Component, OnInit, computed, effect, inject, input, output, signal } from '@angular/core';
import {
  CpAnchorNavComponent,
  CpAnchorNavItem,
  CpButtonComponent,
  CpFieldComponent,
  CpFormSectionComponent,
  CpNoticeComponent,
  CpStatusPillComponent,
  CpStatusPillTone,
} from '@creator-pantry/ui';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';
import { BrandSourceDocumentService } from '../../../services/brand-source-document.service';
import { decodeCreateChoice } from './brand-setup-create';
import {
  GuideSection,
  GuideSectionDefinition,
  SECTION_BODY_MAX,
  answersOf,
  decodeGuideSections,
  isWritten,
  sameSections,
  sectionsFor,
  seedSections,
  writtenSummary,
} from './brand-setup-guide';

/** How one example a drafted part was written from reads on screen. */
type Citation = { readonly title: string; readonly load: 'ready' | 'missing' | 'unreachable' };

const ANCHOR = 'cp-guide-';

/**
 * Step 6 of "Create my voice": the creator reads their guide one part at a time and changes anything that does
 * not sound like them.
 *
 * **Their edits are the guide.** A part they have touched is theirs: it comes back exactly as they left it,
 * and nothing re-derives it afterwards — not a changed answer, not a later visit. A part still carrying the
 * sentence put together from their earlier answers *is* composed again when they change those answers, which
 * is the only re-derivation here and the reason each part records where its words came from rather than
 * guessing. The recipe editor's `Creator`-versus-`Composed` rule, applied to prose.
 *
 * **No model runs here, and nothing is activated.** The sentences a part starts from are fixed phrases for the
 * answers the creator picked, plus their own notes verbatim. A part written *for* them by a draft renders as
 * such, with the examples it came from named — nothing writes that yet, and a saved draft carrying it is shown
 * the way it will be. Approving, activating and trying the guide out all belong to the step after this one.
 *
 * **Saving is the shell's.** Edits report upwards and the shell autosaves them; Save draft only asks it to
 * write now rather than at the end of the debounce, and the shell's own status line is what says so.
 */
@Component({
  selector: 'cp-brand-setup-guide-step',
  standalone: true,
  imports: [
    CpAnchorNavComponent,
    CpButtonComponent,
    CpFieldComponent,
    CpFormSectionComponent,
    CpNoticeComponent,
    CpStatusPillComponent,
  ],
  templateUrl: './brand-setup-guide-step.component.html',
  styleUrl: './brand-setup-guide-step.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupGuideStepComponent implements OnInit {
  readonly step = input.required<BrandSetupStepDefinition>();
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  /** The first step's saved slice: what the creator makes, for whom, and where they share it. */
  readonly goals = input<Readonly<Record<string, unknown>> | null>(null);
  /** The second step's saved slice: how they like to sound. */
  readonly style = input<Readonly<Record<string, unknown>> | null>(null);
  /** The guide-building step's saved slice: whether they asked for a draft or to write it themselves. */
  readonly choice = input<Readonly<Record<string, unknown>> | null>(null);
  readonly workspaceSlug = input.required<string>();
  readonly reported = output<BrandSetupStepReport>();
  /** Save draft: asks the shell to write now instead of waiting for the autosave debounce. */
  readonly saveRequested = output<void>();

  private readonly documents = inject(BrandSourceDocumentService);

  protected readonly bodyMax = SECTION_BODY_MAX;
  protected readonly definitions = signal<readonly GuideSectionDefinition[]>([]);
  protected readonly sections = signal<readonly GuideSection[]>([]);
  protected readonly citations = signal<Readonly<Record<string, Citation>>>({});
  protected readonly active = signal<string | null>(null);

  private baseline: readonly GuideSection[] = [];
  private ready = false;

  protected readonly views = computed(() =>
    this.definitions().map((definition) => {
      const section = this.sections().find((each) => each.id === definition.id);
      const held: GuideSection = section ?? { id: definition.id, body: '', origin: 'creator', edited: false, citedDocumentIds: [] };
      const marker = markerOf(held);
      return {
        definition,
        section: held,
        anchorId: ANCHOR + definition.id,
        written: isWritten(held),
        tone: TONES[marker],
        pill: PILLS[marker],
        marker: MARKERS[marker],
        citation: this.citationFor(held),
      };
    }),
  );

  protected readonly written = computed(() => this.views().filter((view) => view.written).length);
  protected readonly summary = computed(() => writtenSummary(this.written(), this.views().length));
  protected readonly anchors = computed<readonly CpAnchorNavItem[]>(() =>
    this.views().map((view) => ({ targetId: view.anchorId, label: view.definition.label, detail: view.written ? 'Written' : 'Empty' })),
  );

  /** True when the creator asked for a draft and no part has been written for them. */
  protected readonly awaitingDraft = computed(
    () => decodeCreateChoice(this.choice()).method === 'draft' && this.sections().every((each) => each.origin !== 'draft'),
  );

  protected readonly unreachableCitations = computed(
    () => Object.values(this.citations()).filter((each) => each.load === 'unreachable').length,
  );

  constructor() {
    effect(() => {
      const sections = this.sections();
      if (!this.ready) return;

      this.reported.emit({
        canContinue: sections.some(isWritten),
        isDirty: !sameSections(sections, this.baseline),
        // Always handed over, even untouched: what the earlier answers composed is this step's own work, and
        // the step after this one has nothing to show without it.
        draft: { sections: sections.map((each) => ({ ...each })) },
      });
    });
  }

  ngOnInit(): void {
    const { goals, style } = answersOf(this.goals(), this.style());
    const definitions = sectionsFor(goals);
    const seeded = seedSections(definitions, decodeGuideSections(this.draft()), goals, style);

    this.definitions.set(definitions);
    this.sections.set(seeded);
    // What is on screen now. An edit is measured against it, so composing a part is not an unsaved change of
    // the creator's — while the shell still persists it, because the stored slice does not hold it yet.
    this.baseline = seeded;
    this.ready = true;

    void this.loadCitations();
  }

  // ---- Editing ----

  protected setBody(id: string, event: Event): void {
    const body = (event.target as HTMLTextAreaElement).value;
    // Recorded, not inferred: once the creator has typed in a part it is theirs, even if they type the same
    // words back. Re-deriving it later would be the rewrite this step must never do.
    this.sections.update((all) => all.map((each) => (each.id === id ? { ...each, body, edited: true } : each)));
  }

  protected save(): void {
    this.saveRequested.emit();
  }

  // ---- The nav ----

  protected jump(item: CpAnchorNavItem): void {
    this.active.set(item.targetId);
    const target = document.getElementById(item.targetId);
    if (!target) return;
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
    target.scrollIntoView({ behavior: reduce ? 'auto' : 'smooth', block: 'start' });
    target.focus();
  }

  // ---- The examples a drafted part was written from ----

  private async loadCitations(): Promise<void> {
    const ids = [...new Set(this.sections().flatMap((each) => (each.origin === 'draft' ? each.citedDocumentIds : [])))];
    await Promise.all(ids.map((id) => this.loadCitation(id)));
  }

  protected async retryCitations(): Promise<void> {
    const ids = Object.entries(this.citations())
      .filter(([, citation]) => citation.load === 'unreachable')
      .map(([id]) => id);
    await Promise.all(ids.map((id) => this.loadCitation(id)));
  }

  private async loadCitation(documentId: string): Promise<void> {
    const outcome = await this.documents.get(this.workspaceSlug(), documentId);
    const citation: Citation =
      outcome.status === 'ok'
        ? { title: outcome.document.title, load: 'ready' }
        : outcome.status === 'not_found'
          ? { title: 'an example that is no longer there', load: 'missing' }
          : { title: "an example we couldn't look up just now", load: 'unreachable' };
    this.citations.update((all) => ({ ...all, [documentId]: citation }));
  }

  private citationFor(section: GuideSection): string {
    if (section.origin === 'answers') return 'Put together from your answers in the earlier steps.';
    if (section.origin !== 'draft') return '';
    if (section.citedDocumentIds.length === 0) return 'Written from your answers, not from one of your examples.';

    const named = section.citedDocumentIds.map((id) => this.citations()[id]?.title ?? 'one of your examples');
    return `Written from ${named.join(', ')}.`;
  }
}

/** What a part's words are, in one word: where they came from and whether the creator has changed them. */
type Marker = 'drafted' | 'changed' | 'composed' | 'yours';

function markerOf(section: GuideSection): Marker {
  if (section.origin === 'draft') return section.edited ? 'changed' : 'drafted';
  if (section.origin === 'answers' && !section.edited) return 'composed';
  return 'yours';
}

const PILLS: Readonly<Record<Marker, string>> = {
  drafted: 'Written for you',
  changed: 'Changed by you',
  composed: 'From your answers',
  yours: 'Your words',
};

const TONES: Readonly<Record<Marker, CpStatusPillTone>> = {
  drafted: 'progress',
  changed: 'success',
  composed: 'neutral',
  yours: 'success',
};

const MARKERS: Readonly<Record<Marker, string>> = {
  drafted: 'Written for you. Change anything that does not sound like you.',
  changed: 'You rewrote this, so it stays exactly as you left it.',
  composed: 'Said back from what you told us earlier. Change it and it becomes yours.',
  yours: 'Your own words.',
};
