import { Component, signal, viewChild } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ConfirmRequest, ConfirmService } from '../../core/confirm.service';
import {
  AcceptLongFormDocumentRequest,
  EditLongFormSectionRequest,
  LongFormAssembledDocument,
  LongFormDocumentReview,
  LongFormDocumentSection,
  LongFormSectionRequestSummary,
  LongFormSectionWarning,
  RequestLongFormSectionRequest,
  SettleLongFormSectionRequest,
} from '../../models/long-form-document.models';
import { AiAllowanceState } from '../../services/ai-usage.service';
import { LongFormDraft, PutLongFormFramingRequest, ReorderLongFormItemsRequest } from '../../models/long-form-draft.models';
import { LongFormDraftService, LongFormPlanWriteOutcome } from '../../services/long-form-draft.service';
import {
  LongFormAcceptanceOutcome,
  LongFormDocumentOutcome,
  LongFormDocumentService,
  LongFormSectionWriteOutcome,
  LongFormWritingOutcome,
} from '../../services/long-form-document.service';
import { LongFormSeoService } from '../../services/long-form-seo.service';
import {
  LONG_FORM_REVIEW_POLL_MS,
  LongFormDocumentReviewComponent,
} from './long-form-document-review.component';

const HEALTHY: AiAllowanceState = { kind: 'unknown' };

const SPENT: AiAllowanceState = {
  kind: 'exhausted',
  period: {
    unit: 'Credits',
    allowance: 1000,
    remaining: 0,
    consumed: 1000,
    carriedOver: 0,
    periodLength: 'Monthly',
    resetsAt: '2026-11-01T00:00:00Z',
    timeZoneId: 'Etc/UTC',
    usedPercent: 100,
  },
};

function section(id: string, overrides: Partial<LongFormDocumentSection> = {}): LongFormDocumentSection {
  return {
    id,
    role: 'Body',
    heading: `Heading ${id}`,
    intent: `Intent ${id}`,
    body: `Body of ${id}.`,
    isWritten: true,
    settled: false,
    characterCount: 10,
    overCeiling: false,
    writtenBy: 'AiGenerated',
    aiProposalId: `p-${id}`,
    reusedEditorialParts: [],
    findings: [],
    sourceReferenceId: null,
    sourceState: null,
    ...overrides,
  };
}

function unwritten(id: string, overrides: Partial<LongFormDocumentSection> = {}): LongFormDocumentSection {
  return section(id, { body: null, isWritten: false, characterCount: 0, writtenBy: null, aiProposalId: null, ...overrides });
}

const THREE: readonly LongFormDocumentSection[] = [
  section('a', { role: 'Hook', heading: 'Why this cake', body: 'Some cakes ask for an afternoon.' }),
  section('b', { role: 'Tips', heading: 'Three tips', body: '- Stir gently.\n- Keep the oven hot.' }),
  section('c', { role: 'Closing', heading: 'Until next time', body: 'Go and bake.' }),
];

function documentOf(
  sections: readonly LongFormDocumentSection[] = THREE,
  overrides: Partial<LongFormAssembledDocument> = {},
): LongFormAssembledDocument {
  const missing = sections.filter((each) => !each.isWritten).map((each) => each.id);
  const over = sections.filter((each) => each.overCeiling).map((each) => each.id);

  return {
    draftId: 'd-1',
    creativeContextId: 'ctx-1',
    kind: 'BlogArticle',
    status: 'Writing',
    revisionNumber: 4,
    concurrencyToken: 'token-4',
    markdown: sections.filter((each) => each.isWritten).map((each) => `## ${each.heading}\n\n${each.body}\n`).join('\n'),
    wordCount: 1200,
    characterCount: 7000,
    targetWords: { min: 900, max: 1600 },
    wordTarget: missing.length > 0 ? 'NotAssessed' : 'Within',
    sectionMaxCharacters: 3000,
    documentMaxCharacters: 20000,
    documentOverCeiling: false,
    sections,
    needsConfirmation: [
      ...(missing.length > 0 ? [{ issue: 'UnwrittenSections' as const, sectionIds: missing }] : []),
      ...(over.length > 0 ? [{ issue: 'OverCeiling' as const, sectionIds: over }] : []),
    ],
    acceptedRevisionNumber: null,
    acceptedAt: null,
    framing: null,
    unresolvedItems: [],
    staleReasons: [],
    staleSince: null,
    ...overrides,
  };
}

function reviewOf(
  document: LongFormAssembledDocument = documentOf(),
  warnings: readonly LongFormSectionWarning[] = [],
  requests: readonly LongFormSectionRequestSummary[] = [],
): LongFormDocumentReview {
  return { document, warnings, requests };
}

function request(sectionId: string, outcome: LongFormSectionRequestSummary['outcome'], overrides: Partial<LongFormSectionRequestSummary> = {}): LongFormSectionRequestSummary {
  return {
    sectionId,
    requestId: `r-${sectionId}`,
    status: outcome === 'Waiting' ? 'Requested' : outcome === 'Failed' ? 'Failed' : 'Proposed',
    failureCategory: null,
    outcome,
    requestedAt: '2026-10-12T12:00:00Z',
    ...overrides,
  };
}

/** The service, scripted per test. Every call is recorded, because what was asked for is the contract. */
class FakeLongFormDocumentService {
  /** Answers to `read`, in order; the last one repeats. */
  reads: LongFormDocumentOutcome[] = [{ status: 'found', review: reviewOf() }];
  editOutcomes: LongFormSectionWriteOutcome[] = [{ status: 'saved' }];
  settleOutcome: LongFormSectionWriteOutcome = { status: 'saved' };
  writingOutcome: LongFormWritingOutcome = { status: 'accepted', requests: [], replayed: false };
  restOutcome: LongFormWritingOutcome = { status: 'accepted', requests: [], replayed: false };
  acceptOutcome: LongFormAcceptanceOutcome = { status: 'accepted', document: documentOf(THREE, { status: 'Accepted' }) };

  readCalls: [string, string][] = [];
  edits: [string, EditLongFormSectionRequest][] = [];
  settles: [string, SettleLongFormSectionRequest][] = [];
  sectionRequests: [string, RequestLongFormSectionRequest, string][] = [];
  restRequests: [number, string][] = [];
  acceptances: AcceptLongFormDocumentRequest[] = [];

  /** Null stands for a gateway address that is not known yet. */
  gateway: string | null = 'https://gateway.example';

  exportUrl(slug: string, draftId: string, format: string): string | null {
    return this.gateway ? `${this.gateway}/${slug}/${draftId}/exports/${format}` : null;
  }

  read(slug: string, draftId: string): Promise<LongFormDocumentOutcome> {
    this.readCalls.push([slug, draftId]);
    return Promise.resolve(this.reads.length > 1 ? this.reads.shift()! : this.reads[0]!);
  }

  editSection(_slug: string, _draft: string, sectionId: string, body: EditLongFormSectionRequest): Promise<LongFormSectionWriteOutcome> {
    this.edits.push([sectionId, body]);
    return Promise.resolve(this.editOutcomes.length > 1 ? this.editOutcomes.shift()! : this.editOutcomes[0]!);
  }

  settleSection(_slug: string, _draft: string, sectionId: string, body: SettleLongFormSectionRequest): Promise<LongFormSectionWriteOutcome> {
    this.settles.push([sectionId, body]);
    return Promise.resolve(this.settleOutcome);
  }

  requestSection(_slug: string, _draft: string, sectionId: string, body: RequestLongFormSectionRequest, key: string): Promise<LongFormWritingOutcome> {
    this.sectionRequests.push([sectionId, body, key]);
    return Promise.resolve(this.writingOutcome);
  }

  requestRest(_slug: string, _draft: string, revisionNumber: number, key: string): Promise<LongFormWritingOutcome> {
    this.restRequests.push([revisionNumber, key]);
    return Promise.resolve(this.restOutcome);
  }

  accept(_slug: string, _draft: string, body: AcceptLongFormDocumentRequest): Promise<LongFormAcceptanceOutcome> {
    this.acceptances.push(body);
    return Promise.resolve(this.acceptOutcome);
  }
}

/** The two routes an issue has beyond a section's: its framing and the order of its items. */
class FakeLongFormDraftService {
  framingOutcomes: LongFormPlanWriteOutcome[] = [{ status: 'saved', draft: {} as LongFormDraft }];
  orderOutcomes: LongFormPlanWriteOutcome[] = [{ status: 'saved', draft: {} as LongFormDraft }];

  framings: PutLongFormFramingRequest[] = [];
  orders: ReorderLongFormItemsRequest[] = [];

  putFraming(_slug: string, _draft: string, body: PutLongFormFramingRequest): Promise<LongFormPlanWriteOutcome> {
    this.framings.push(body);
    return Promise.resolve(this.framingOutcomes.length > 1 ? this.framingOutcomes.shift()! : this.framingOutcomes[0]!);
  }

  reorderItems(_slug: string, _draft: string, body: ReorderLongFormItemsRequest): Promise<LongFormPlanWriteOutcome> {
    this.orders.push(body);
    return Promise.resolve(this.orderOutcomes.length > 1 ? this.orderOutcomes.shift()! : this.orderOutcomes[0]!);
  }
}

/** An issue: a greeting, three items each covering its own recipe, and a sign-off. */
const ISSUE: readonly LongFormDocumentSection[] = [
  section('g', { role: 'Greeting', heading: 'Hello again', body: 'Hello, everyone.' }),
  section('i1', { role: 'Item', heading: 'The loaf', sourceReferenceId: 'ref-1', sourceState: 'Resolved' }),
  section('i2', { role: 'Item', heading: 'The cake', sourceReferenceId: 'ref-2', sourceState: 'Resolved' }),
  unwritten('i3', { role: 'Item', heading: 'The soup', sourceReferenceId: 'ref-3', sourceState: 'Resolved' }),
  section('s', { role: 'Signoff', heading: 'Until next week', body: 'See you then.' }),
];

function issueOf(
  sections: readonly LongFormDocumentSection[] = ISSUE,
  overrides: Partial<LongFormAssembledDocument> = {},
): LongFormAssembledDocument {
  return documentOf(sections, { kind: 'NewsletterIssue', ...overrides });
}

function field(id: string): HTMLInputElement | HTMLTextAreaElement {
  return el.querySelector<HTMLInputElement | HTMLTextAreaElement>(`#cp-long-form-document-${id}`)!;
}

class StubConfirmService {
  answer = true;
  calls: ConfirmRequest[] = [];

  confirm(asked: ConfirmRequest): Promise<boolean> {
    this.calls.push(asked);
    return Promise.resolve(this.answer);
  }
}

@Component({
  standalone: true,
  imports: [LongFormDocumentReviewComponent],
  template: `<cp-long-form-document-review
    [workspaceSlug]="slug()"
    [draftId]="draftId()"
    [allowance]="allowance()"
    [show]="show()"
    (accepted)="accepted.push($event)"
    (documentRead)="reads.push($event)"
  />`,
})
class HostComponent {
  readonly slug = signal('cozy-fall');
  readonly draftId = signal('d-1');
  readonly allowance = signal<AiAllowanceState>(HEALTHY);
  readonly accepted: LongFormAssembledDocument[] = [];
  readonly show = signal<'all' | 'write' | 'accept'>('all');
  readonly reads: LongFormAssembledDocument[] = [];
  readonly review = viewChild.required(LongFormDocumentReviewComponent);
}

let fixture: ComponentFixture<HostComponent>;
let host: HostComponent;
let el: HTMLElement;
let service: FakeLongFormDocumentService;
let issue: FakeLongFormDraftService;
let confirmService: StubConfirmService;

/** Drains promises and renders, without waiting on the poll timer a waiting section leaves running. */
async function settle(): Promise<void> {
  for (let turn = 0; turn < 10; turn += 1) {
    fixture.detectChanges();
    await Promise.resolve();
  }
  fixture.detectChanges();
}

function text(node: ParentNode = el): string {
  return (node as HTMLElement).textContent?.replace(/\s+/g, ' ').trim() ?? '';
}

function card(position: number): HTMLElement {
  return el.querySelector<HTMLElement>(`[data-testid="section-${position}"]`)!;
}

function button(label: string, within: ParentNode = el): HTMLButtonElement | null {
  return (
    Array.from(within.querySelectorAll<HTMLButtonElement>('button')).find(
      (node) => node.textContent?.trim() === label,
    ) ?? null
  );
}

async function click(label: string, within: ParentNode = el): Promise<void> {
  const target = button(label, within);
  expect(target).withContext(`a "${label}" button`).not.toBeNull();
  target?.click();
  await settle();
}

function box(position: number): HTMLTextAreaElement {
  return card(position).querySelector<HTMLTextAreaElement>('textarea')!;
}

async function type(area: HTMLTextAreaElement, value: string): Promise<void> {
  area.value = value;
  area.dispatchEvent(new Event('input'));
  await settle();
}

function announcement(): string {
  return el.querySelector('[data-testid="announcement"]')?.textContent?.trim() ?? '';
}

async function create(): Promise<void> {
  fixture = TestBed.createComponent(HostComponent);
  host = fixture.componentInstance;
  el = fixture.nativeElement;
  await settle();
}

describe('LongFormDocumentReviewComponent', () => {
  beforeEach(async () => {
    service = new FakeLongFormDocumentService();
    issue = new FakeLongFormDraftService();
    confirmService = new StubConfirmService();

    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      providers: [
        { provide: LongFormDocumentService, useValue: service },

        // The SEO card under an accepted piece has its own spec; here it only has to be mountable.
        { provide: LongFormSeoService, useValue: { request: () => new Promise(() => undefined), watch: () => new Promise(() => undefined) } },
        { provide: LongFormDraftService, useValue: issue },
        { provide: ConfirmService, useValue: confirmService },
      ],
    }).compileComponents();
  });

  afterEach(() => fixture?.destroy());

  // ---- a newsletter issue: its subject, its note and its items (LF.4.2) ------------------------------

  it('shows an issue as its subject, its note and its items in order, and an article as neither', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(issueOf(ISSUE, { framing: { subject: 'Three for a cold week', note: 'It rained, so I baked.' } })) },
    ];
    await create();

    expect(field('subject').value).toBe('Three for a cold week');
    expect(field('note').value).toBe('It rained, so I baked.');
    expect(text(card(2))).toContain('Recipe 1 of 3');
    expect(text(card(4))).toContain('Recipe 3 of 3');
    expect(text(card(1))).not.toContain('Recipe');

    // Nothing here offers to send the issue to anyone: no control does, and nothing names a reader list.
    const controls = Array.from(el.querySelectorAll('button')).map((node) => node.textContent?.toLowerCase() ?? '');
    expect(controls.some((label) => /send|schedule|publish/.test(label))).toBeFalse();
    expect(text().toLowerCase()).not.toMatch(/subscriber|mailing list/);

    fixture.destroy();
    service.reads = [{ status: 'found', review: reviewOf() }];
    await create();

    expect(field('subject')).toBeNull();
    expect(button('Move up')).toBeNull();
  });

  it('saves the subject and note the creator typed, and only then lets go of them', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(issueOf()) },
      { status: 'found', review: reviewOf(issueOf(ISSUE, { framing: { subject: 'Three for a cold week', note: null } })) },
    ];
    await create();

    expect(button('Save subject and note')?.getAttribute('aria-disabled')).toBe('true');

    await type(field('subject') as unknown as HTMLTextAreaElement, 'Three for a cold week');

    expect(host.review().hasUnsavedWork()).toBeTrue();
    expect(text()).toContain('Unsaved changes');

    await click('Save subject and note');

    // Both parts every time, and a blank one as none: the route replaces the framing whole.
    expect(issue.framings).toEqual([{ expectedConcurrencyToken: 'token-4', subject: 'Three for a cold week', note: null }]);
    expect(announcement()).toBe('Saved the subject and note.');
    expect(host.review().hasUnsavedWork()).toBeFalse();
    expect(field('subject').value).toBe('Three for a cold week');
  });

  it('keeps the creator’s words in their boxes when a save is refused, and says why beside the field', async () => {
    service.reads = [{ status: 'found', review: reviewOf(issueOf()) }];
    issue.framingOutcomes = [
      { status: 'refused', message: 'The request is not valid.', fieldErrors: { subject: ['Keep the subject to 150 characters.'] } },
    ];
    await create();

    await type(field('subject') as unknown as HTMLTextAreaElement, 'A subject that runs on');
    await click('Save subject and note');

    expect(field('subject').value).toBe('A subject that runs on');
    expect(field('subject').getAttribute('aria-invalid')).toBe('true');
    expect(text()).toContain('Keep the subject to 150 characters.');
    expect(host.review().hasUnsavedWork()).toBeTrue();
  });

  it('reads the piece again and saves once more when only the token had moved', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(issueOf()) },
      { status: 'found', review: reviewOf(issueOf(ISSUE, { concurrencyToken: 'token-5', revisionNumber: 5 })) },
    ];
    issue.framingOutcomes = [{ status: 'stale' }, { status: 'saved', draft: {} as LongFormDraft }];
    await create();

    await type(field('note') as HTMLTextAreaElement, 'A note.');
    await click('Save subject and note');

    expect(issue.framings.map((sent) => sent.expectedConcurrencyToken)).toEqual(['token-4', 'token-5']);
    expect(issue.framings[1]).toEqual({ expectedConcurrencyToken: 'token-5', subject: null, note: 'A note.' });
  });

  it('asks before discarding a subject the creator has changed', async () => {
    service.reads = [{ status: 'found', review: reviewOf(issueOf()) }];
    await create();

    await type(field('subject') as unknown as HTMLTextAreaElement, 'Half a thought');

    confirmService.answer = false;
    await click('Discard changes');
    expect(field('subject').value).toBe('Half a thought');

    confirmService.answer = true;
    await click('Discard changes');
    expect(field('subject').value).toBe('');
    expect(issue.framings).toEqual([]);
  });

  it('moves an item among the items by the recipe each covers, and says so', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(issueOf()) },
      { status: 'found', review: reviewOf(issueOf([ISSUE[0]!, ISSUE[2]!, ISSUE[1]!, ISSUE[3]!, ISSUE[4]!])) },
    ];
    await create();

    // The ends say so and do nothing.
    expect(button('Move up', card(2))?.getAttribute('aria-disabled')).toBe('true');
    expect(button('Move down', card(4))?.getAttribute('aria-disabled')).toBe('true');
    expect(button('Move up', card(3))?.getAttribute('aria-label')).toBe('Move The cake up among the recipes');

    await click('Move up', card(2));
    expect(issue.orders).toEqual([]);

    await click('Move up', card(3));

    // References, not positions and not section ids: the greeting and the sign-off are not in it at all.
    expect(issue.orders).toEqual([{ expectedConcurrencyToken: 'token-4', sourceReferenceIds: ['ref-2', 'ref-1', 'ref-3'] }]);
    expect(announcement()).toBe('Moved “The cake” up.');
    expect(Array.from(el.querySelectorAll('article h2')).map((node) => node.textContent)).toEqual([
      'Hello again',
      'The cake',
      'The loaf',
      'The soup',
      'Until next week',
    ]);
    expect(document.activeElement?.textContent?.trim()).toBe('Move up');
  });

  it('says why an item could not be moved, beside that item, and moves nothing', async () => {
    service.reads = [{ status: 'found', review: reviewOf(issueOf()) }];
    issue.orderOutcomes = [{ status: 'unavailable' }];
    await create();

    await click('Move down', card(2));

    expect(text(card(2))).toContain('Nothing was moved');
    expect(card(2).querySelector('h2')?.textContent).toBe('The loaf');
  });

  it('says an item’s recipe is no longer part of the piece, keeps its text, and does not offer to have it written', async () => {
    const lost = section('i2', { role: 'Item', heading: 'The cake', sourceReferenceId: 'ref-2', sourceState: 'Unresolved' });
    const lostAndUnwritten = unwritten('i3', { role: 'Item', heading: 'The soup', sourceReferenceId: 'ref-3', sourceState: 'Unresolved' });
    service.reads = [
      { status: 'found', review: reviewOf(issueOf([ISSUE[0]!, ISSUE[1]!, lost, lostAndUnwritten, ISSUE[4]!], { unresolvedItems: ['i2', 'i3'] })) },
    ];
    await create();

    expect(card(2).querySelector('[data-testid="unresolved"]')).toBeNull();
    expect(text(card(3).querySelector('[data-testid="unresolved"]')!)).toContain('no longer part of this piece');

    // Kept, and still the creator's to change.
    expect(card(3).querySelector('.body-read')?.textContent).toBe('Body of i2.');
    expect(button('Edit', card(3))).not.toBeNull();
    expect(button('Write it again', card(3))).toBeNull();
    expect(button('Write it for me', card(4))).toBeNull();
    expect(button('Write it yourself', card(4))).not.toBeNull();

    // And not counted among what "write the rest" would write.
    expect(button('Write the one section left')).toBeNull();
  });

  // ---- reading ---------------------------------------------------------------------------------------

  it('shows the whole piece in plan order, each section under the creator’s own heading', async () => {
    await create();

    expect(service.readCalls).toEqual([['cozy-fall', 'd-1']]);
    expect(Array.from(el.querySelectorAll('article h2')).map((node) => node.textContent)).toEqual([
      'Why this cake',
      'Three tips',
      'Until next time',
    ]);
    expect(card(1).querySelector('.body-read')?.textContent).toBe('Some cakes ask for an afternoon.');

    // The text as written, line breaks and Markdown included: shown, not rendered.
    expect(card(2).querySelector('.body-read')?.textContent).toBe('- Stir gently.\n- Keep the oven hot.');
    expect(card(2).querySelector('.body-read ul')).toBeNull();
  });

  it('says how long the piece is in words, against what a piece of its kind usually runs to', async () => {
    await create();

    const length = el.querySelector('[data-testid="length"]')!;
    expect(text(length)).toContain('A usual length');
    expect(text(length)).toContain('1,200 words');
    expect(text(length)).toContain('900 to 1,600');
  });

  it('marks generated text as written for the creator, and their own text as not', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Mine', writtenBy: 'CreatorEdit', aiProposalId: null })])) },
    ];
    await create();

    expect(text(card(1))).toContain('Drafted by AI');
    expect(text(card(2))).not.toContain('Drafted by AI');
  });

  it('says whose words a section began as when it was written from accepted copy', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Three tips', reusedEditorialParts: ['tips', 'storageReheating'] })])) },
    ];
    await create();

    expect(card(1).querySelector('[data-testid="attribution"]')).toBeNull();
    expect(card(2).querySelector('[data-testid="attribution"]')?.textContent).toContain(
      'Written from the tips and storage and reheating notes you already accepted for this recipe.',
    );
  });

  it('lists what was found about a section beside it, and marks the writer’s own note as the writer’s', async () => {
    service.reads = [
      {
        status: 'found',
        review: reviewOf(documentOf(), [
          { sectionId: 'b', kind: 'UnverifiedClaim', code: 'editorial.unsupported_number', message: '“900g” is a figure the recipe does not state.', fromModel: false },
          { sectionId: 'b', kind: 'Assumption', code: null, message: 'I assumed a family audience.', fromModel: true },
        ]),
      },
    ];
    await create();

    const findings = card(2).querySelector('.findings')!;
    expect(findings.getAttribute('aria-label')).toBe('Things to check in Three tips');
    expect(findings.querySelectorAll('li').length).toBe(2);
    expect(text(findings)).toContain('“900g” is a figure the recipe does not state.');
    expect(text(findings)).toContain('The writer’s own note: I assumed a family audience.');

    // The server's code is for the record, not for the creator.
    expect(text(findings)).not.toContain('editorial.unsupported_number');
    expect(card(1).querySelector('.findings')).toBeNull();
  });

  it('shows what the scanner found in a section the creator wrote themselves, ahead of anything the writer said', async () => {
    service.reads = [
      {
        status: 'found',
        review: reviewOf(
          documentOf([
            THREE[0]!,
            section('b', {
              heading: 'Three tips',
              writtenBy: 'CreatorEdit',
              aiProposalId: null,
              findings: [
                { code: 'editorial.unsupported_number', kind: 'UnverifiedClaim', message: '“900g” is a figure your recipe does not state.' },
                { code: 'editorial.unsupported_safety_claim', kind: 'SafetyCaution', message: 'This says the food is safe for someone.' },
              ],
            }),
          ]),
          [{ sectionId: 'b', kind: 'Assumption', code: null, message: 'A note from the writer.', fromModel: true }],
        ),
      },
    ];
    await create();

    const items = Array.from(card(2).querySelectorAll('.findings li')).map((item) => text(item));
    expect(items.length).toBe(3);
    expect(items[0]).toContain('“900g” is a figure your recipe does not state.');
    expect(items[1]).toContain('This says the food is safe for someone.');
    expect(items[2]).toContain('The writer’s own note: A note from the writer.');

    // Their own text, with cautions on it, is still theirs to keep, settle and accept.
    expect(text(card(2))).not.toContain('Drafted by AI');
    expect(button('Mark as settled', card(2))).not.toBeNull();
    expect(button('Accept this piece')?.getAttribute('aria-disabled')).toBeNull();
  });

  it('says a draft that is missing — or another workspace’s — is not here, with none of it shown', async () => {
    service.reads = [{ status: 'not_found' }];
    await create();

    expect(text()).toContain('no longer here');
    expect(el.querySelectorAll('article').length).toBe(0);
  });

  it('offers to try again when the piece could not be loaded, and loads it', async () => {
    service.reads = [{ status: 'unavailable' }, { status: 'found', review: reviewOf() }];
    await create();

    expect(text()).toContain('couldn’t load');
    await click('Try again');

    expect(el.querySelectorAll('article').length).toBe(3);
  });

  it('reads again when the draft it is given changes, and takes none of the old draft’s unsaved text with it', async () => {
    await create();
    await click('Edit', card(1));
    await type(box(1), 'Unsaved, for the first draft.');

    service.reads = [{ status: 'found', review: reviewOf(documentOf([section('z', { heading: 'Another piece' })], { draftId: 'd-2' })) }];
    host.draftId.set('d-2');
    await settle();

    expect(service.readCalls[service.readCalls.length - 1]).toEqual(['cozy-fall', 'd-2']);
    expect(el.querySelector('textarea')).toBeNull();
    expect(text()).not.toContain('Unsaved, for the first draft.');
    expect(host.review().hasUnsavedWork()).toBeFalse();
  });

  // ---- jumping ---------------------------------------------------------------------------------------

  it('offers a jump to every section, and a jump lands focus on that section', async () => {
    await create();

    const links = Array.from(el.querySelectorAll<HTMLAnchorElement>('cp-anchor-nav a'));
    expect(links.length).toBe(3);
    expect(links.map((link) => link.textContent ?? '').every((label) => label.includes('Written'))).toBeTrue();
    expect(links[0]!.textContent).toContain('Why this cake');
    expect(links[2]!.textContent).toContain('Until next time');

    links[2]!.click();
    await settle();

    expect(document.activeElement).toBe(card(3));
    expect(card(3).getAttribute('aria-labelledby')).toBe(card(3).querySelector('h2')!.id);
  });

  // ---- editing ---------------------------------------------------------------------------------------

  it('edits a section in a plain text box inside a labelled field, and focuses it', async () => {
    await create();
    await click('Edit', card(2));

    const area = box(2);
    expect(area.tagName).toBe('TEXTAREA');
    expect(area.value).toBe('- Stir gently.\n- Keep the oven hot.');
    expect(card(2).querySelector(`label[for="${area.id}"]`)?.textContent).toContain('This section’s text');
    expect(document.activeElement).toBe(area);
    expect(el.querySelector('[contenteditable]')).toBeNull();
  });

  it('saves the creator’s text exactly as typed, with the token it read, and reads the piece again', async () => {
    await create();
    await click('Edit', card(2));
    await type(box(2), '  My own tips.\n\n- One.  ');

    expect(host.review().hasUnsavedWork()).toBeTrue();
    expect(text(card(2))).toContain('Unsaved changes');

    // Said plainly: this text lives on this page and nowhere else until it is saved.
    expect(text(card(2))).toContain('held on this page only');

    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Three tips', body: '  My own tips.\n\n- One.  ', writtenBy: 'CreatorEdit' }), THREE[2]!])) }];
    await click('Save this section', card(2));

    expect(service.edits).toEqual([['b', { expectedConcurrencyToken: 'token-4', body: '  My own tips.\n\n- One.  ' }]]);
    expect(card(2).querySelector('textarea')).toBeNull();
    expect(host.review().hasUnsavedWork()).toBeFalse();
    expect(announcement()).toBe('Saved “Three tips”.');
    expect(text(card(2))).not.toContain('Drafted by AI');
  });

  it('keeps the creator’s text in the box when a save fails, and says it is still there', async () => {
    service.editOutcomes = [{ status: 'unavailable' }];
    await create();
    await click('Edit', card(2));
    await type(box(2), 'Text I do not want to lose.');
    await click('Save this section', card(2));

    expect(box(2).value).toBe('Text I do not want to lose.');
    expect(text(card(2))).toContain('Your text is still here');
    expect(box(2).getAttribute('aria-invalid')).toBe('true');
    expect(host.review().hasUnsavedWork()).toBeTrue();
  });

  it('sends a save again with the new token when only the token had moved', async () => {
    service.editOutcomes = [{ status: 'stale' }, { status: 'saved' }];
    service.reads = [
      { status: 'found', review: reviewOf() },
      { status: 'found', review: reviewOf(documentOf(THREE, { revisionNumber: 5, concurrencyToken: 'token-5' })) },
    ];
    await create();
    await click('Edit', card(2));
    await type(box(2), 'Mine.');
    await click('Save this section', card(2));

    expect(service.edits.map(([, body]) => body.expectedConcurrencyToken)).toEqual(['token-4', 'token-5']);
    expect(service.edits.every(([, body]) => body.body === 'Mine.')).toBeTrue();
    expect(card(2).querySelector('textarea')).toBeNull();
  });

  it('shows the server’s reason beside the box when it refuses the text', async () => {
    service.editOutcomes = [
      { status: 'refused', code: 'content.long_form.section.invalid', message: 'The request is not valid.', fieldErrors: { body: ['A section’s text is at most 12000 characters.'] } },
    ];
    await create();
    await click('Edit', card(2));
    await type(box(2), 'x');
    await click('Save this section', card(2));

    expect(text(card(2))).toContain('at most 12000 characters');
    expect(box(2).value).toBe('x');
  });

  it('counts what is being typed and flags text over the ceiling without stopping it', async () => {
    await create();
    await click('Edit', card(1));
    await type(box(1), 'a'.repeat(3001));

    expect(text(card(1))).toContain('3001 of 3000 characters');
    expect(text(card(1))).toContain('Too long');
    expect(box(1).hasAttribute('maxlength')).toBeFalse();
    expect(button('Save this section', card(1))?.getAttribute('aria-disabled')).toBeNull();
  });

  it('discards an edit only after the creator confirms, and stops editing untouched text without asking', async () => {
    await create();
    await click('Edit', card(1));
    await click('Stop editing', card(1));
    expect(confirmService.calls.length).toBe(0);
    expect(card(1).querySelector('textarea')).toBeNull();

    await click('Edit', card(1));
    await type(box(1), 'Changed.');

    confirmService.answer = false;
    await click('Discard changes', card(1));
    expect(box(1).value).toBe('Changed.');

    confirmService.answer = true;
    await click('Discard changes', card(1));
    expect(card(1).querySelector('textarea')).toBeNull();
    expect(card(1).querySelector('.body-read')?.textContent).toBe('Some cakes ask for an afternoon.');
    expect(confirmService.calls.every((call) => call.tone === 'danger')).toBeTrue();
  });

  // ---- writing again ---------------------------------------------------------------------------------

  it('asks for a section to be written again against the revision on screen, with what the creator said to change', async () => {
    await create();
    await click('Write it again', card(2));

    const instruction = card(2).querySelector<HTMLTextAreaElement>('textarea')!;
    expect(document.activeElement).toBe(instruction);
    await type(instruction, 'Make it warmer.');

    service.reads = [{ status: 'found', review: reviewOf(documentOf(), [], [request('b', 'Waiting')]) }];
    await click('Write it again', card(2));

    expect(service.sectionRequests.length).toBe(1);
    const [sectionId, body, key] = service.sectionRequests[0]!;
    expect(sectionId).toBe('b');
    expect(body).toEqual({ revisionNumber: 4, instruction: 'Make it warmer.' });
    expect(key).toBeTruthy();
    expect(text(card(2))).toContain('Being written');
    expect(text(card(2))).toContain('Writing this section now');
    expect(card(2).querySelector('[data-testid="section-writing"] cp-skeleton')).not.toBeNull();
    expect(announcement()).toBe('“Three tips” is being written.');

    // The button that was pressed is gone, so focus is on the section and not dropped to the page.
    expect(document.activeElement).toBe(card(2));
  });

  it('retries a request that may have arrived under the same key, and a new request under a new one', async () => {
    service.writingOutcome = { status: 'unavailable' };
    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' })])) }];
    await create();

    await click('Write it for me', card(2));
    expect(text(card(2))).toContain('Nothing was started');
    await click('Write it for me', card(2));

    expect(service.sectionRequests[1]![2]).toBe(service.sectionRequests[0]![2]);

    // Once the server has answered for it, the key is spent.
    service.writingOutcome = { status: 'refused', code: 'content.long_form.revision.stale.conflict', message: 'This section has changed since it was asked for.' };
    await click('Write it for me', card(2));
    expect(text(card(2))).toContain('This section has changed since it was asked for.');

    await click('Write it for me', card(2));
    expect(service.sectionRequests[3]![2]).not.toBe(service.sectionRequests[2]![2]);
  });

  it('keeps showing the piece and says it may be behind when a refresh fails, and stops asking once the screen has gone', async () => {
    jasmine.clock().install();

    try {
      service.reads = [
        { status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' })]), [], [request('b', 'Waiting')]) },
        { status: 'unavailable' },
      ];
      await create();

      jasmine.clock().tick(LONG_FORM_REVIEW_POLL_MS + 1);
      await settle();

      expect(el.querySelectorAll('article').length).toBe(2);
      expect(text()).toContain('may be behind what has been saved');
      expect(button('Check again')).not.toBeNull();

      const reads = service.readCalls.length;
      fixture.destroy();
      jasmine.clock().tick(LONG_FORM_REVIEW_POLL_MS * 4);

      expect(service.readCalls.length).toBe(reads);
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('leaves another section’s unsaved text exactly as it is when a section is written again', async () => {
    await create();
    await click('Edit', card(1));
    await type(box(1), 'My unsaved opening.');

    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { revisionNumber: 5, concurrencyToken: 'token-5' }), [], [request('b', 'Waiting')]) }];
    await click('Write it again', card(2));
    await click('Write it again', card(2));

    expect(box(1).value).toBe('My unsaved opening.');
    expect(host.review().hasUnsavedWork()).toBeTrue();
  });

  it('keeps unsaved text through the refresh that brings a newly written section in', async () => {
    jasmine.clock().install();

    try {
      service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' }), THREE[2]!]), [], [request('b', 'Waiting')]) }];
      await create();
      await click('Edit', card(1));
      await type(box(1), 'Still typing.');

      service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { revisionNumber: 5 }), [], [request('b', 'Written')]) }];
      jasmine.clock().tick(LONG_FORM_REVIEW_POLL_MS + 1);
      await settle();

      expect(card(2).querySelector('.body-read')?.textContent).toContain('Stir gently');
      expect(box(1).value).toBe('Still typing.');

      // Nothing is waiting any more, so the screen stops asking.
      const reads = service.readCalls.length;
      jasmine.clock().tick(LONG_FORM_REVIEW_POLL_MS * 3);
      await settle();
      expect(service.readCalls.length).toBe(reads);
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('offers no rewrite of a section while its own text is being edited', async () => {
    await create();
    await click('Edit', card(2));
    await type(box(2), 'Half a thought.');

    // The edit is open, so the way to "write it again" is to stop editing; the screen offers no button that
    // would silently replace the box.
    expect(button('Write it again', card(2))).toBeNull();
  });

  it('says when writing failed and that nothing was changed', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' })]), [], [request('b', 'Failed', { failureCategory: 'Cancelled' })]) },
    ];
    await create();

    expect(text(card(2))).toContain('was cancelled. Nothing was changed.');
    expect(button('Write it for me', card(2))).not.toBeNull();
  });

  it('writes the rest in one request, and says how many the allowance covered when it covers only some', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' }), unwritten('c', { heading: 'Until next time' })])) }];
    service.restOutcome = {
      status: 'blocked',
      refusal: { status: 'quota_exhausted', unit: 'Credits', allowance: 100, remaining: 10, required: 20, resetsAt: '2026-11-01T00:00:00Z' } as never,
      sectionsRequested: 2,
      sectionsCoverable: 1,
    };
    await create();
    await click('Write the 2 sections left');

    expect(service.restRequests.length).toBe(1);
    expect(service.restRequests[0]![0]).toBe(4);
    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain(
      'covers 1 of the 2 sections left, so none was started',
    );
  });

  it('says how many sections are being written once the rest has been asked for', async () => {
    const twoLeft = documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' }), unwritten('c', { heading: 'Until next time' })]);
    service.reads = [
      { status: 'found', review: reviewOf(twoLeft) },
      { status: 'found', review: reviewOf(twoLeft, [], [request('b', 'Waiting'), request('c', 'Waiting')]) },
    ];
    service.restOutcome = {
      status: 'accepted',
      requests: [
        { sectionId: 'b', request: {} as never },
        { sectionId: 'c', request: {} as never },
      ],
      replayed: false,
    };
    await create();
    await click('Write the 2 sections left');

    expect(announcement()).toBe('2 sections are being written.');
    expect(text()).toContain('Writing is under way');

    // Nothing is left to ask for, and nothing can be accepted while it is under way.
    expect(button('Write the 2 sections left')).toBeNull();
    expect(button('Accept this piece')?.getAttribute('aria-disabled')).toBe('true');
    fixture.destroy();
  });

  it('does not ask for writing when the allowance is spent', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' })])) }];
    await create();
    host.allowance.set(SPENT);
    await settle();

    expect(button('Write it for me', card(2))?.getAttribute('aria-disabled')).toBe('true');
    await click('Write it for me', card(2));
    await click('Write the one section left');

    expect(service.sectionRequests.length + service.restRequests.length).toBe(0);

    // Their own words need no allowance.
    expect(button('Write it yourself', card(2))?.getAttribute('aria-disabled')).toBeNull();
  });

  // ---- settling --------------------------------------------------------------------------------------

  it('says a section is too long in words, and still lets it be settled or accepted with the rest', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Three tips', overCeiling: true, characterCount: 3200 })])) }];
    await create();

    expect(card(2).querySelector('cp-status-pill')?.textContent?.trim()).toContain('Too long');
    expect(text(card(2))).toContain('3200 of 3000 characters');
    expect(button('Mark as settled', card(2))).not.toBeNull();
  });

  it('keeps the section as it was and says why when a settle could not be saved', async () => {
    service.settleOutcome = { status: 'unavailable' };
    await create();
    await click('Mark as settled', card(2));

    expect(text(card(2))).toContain('We couldn’t save that');
    expect(text(card(2))).not.toContain('Settled');
    expect(button('Mark as settled', card(2))).not.toBeNull();
  });

  it('marks a section settled and offers no rewrite of it until the mark is cleared', async () => {
    await create();

    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Three tips', settled: true }), THREE[2]!])) }];
    await click('Mark as settled', card(2));

    expect(service.settles).toEqual([['b', { expectedConcurrencyToken: 'token-4', settled: true }]]);
    expect(text(card(2))).toContain('Settled');
    expect(button('Write it again', card(2))).toBeNull();
    expect(announcement()).toBe('Marked “Three tips” as settled.');

    service.reads = [{ status: 'found', review: reviewOf() }];
    await click('Not settled after all', card(2));

    expect(service.settles[1]).toEqual(['b', { expectedConcurrencyToken: 'token-4', settled: false }]);
    expect(button('Write it again', card(2))).not.toBeNull();
  });

  // ---- accepting -------------------------------------------------------------------------------------

  it('accepts a finished piece in one press, with nothing to confirm, and emits it', async () => {
    await create();

    expect(text()).toContain('Every section is written and within its length.');
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await click('Accept this piece');

    expect(confirmService.calls.length).toBe(0);
    expect(service.acceptances).toEqual([{ expectedConcurrencyToken: 'token-4', revisionNumber: 4, confirmed: [] }]);
    expect(host.accepted.length).toBe(1);
    expect(el.querySelector('[data-testid="accepted"]')?.textContent).toContain('has not been published');
    expect(announcement()).toBe('This piece is accepted. Nothing has been published.');

    // Every action has gone; focus is at the top of the piece, where it says so.
    expect(document.activeElement?.id).toBe('cp-long-form-document-summary');

    // Accepted is read-only: nothing to edit, write, settle or accept.
    expect(button('Edit')).toBeNull();
    expect(button('Accept this piece')).toBeNull();
  });

  it('names what is wrong before accepting an unfinished piece, and accepts nothing if the creator says no', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(documentOf([THREE[0]!, section('b', { heading: 'Three tips', overCeiling: true }), unwritten('c', { heading: 'Until next time' })])) },
    ];
    await create();

    const issues = el.querySelector('[data-testid="issues"]')!;
    expect(text(issues)).toContain('“Until next time” has not been written.');
    expect(text(issues)).toContain('“Three tips” is longer than a section may be.');

    confirmService.answer = false;
    await click('Accept this piece');

    expect(confirmService.calls.length).toBe(1);
    expect(confirmService.calls[0]!.message).toContain('“Until next time” has not been written.');
    expect(confirmService.calls[0]!.message).toContain('“Three tips” is longer than a section may be.');
    expect(service.acceptances.length).toBe(0);
    expect(host.accepted.length).toBe(0);

    confirmService.answer = true;
    await click('Accept this piece');

    // Exactly the issues that were shown, by name.
    expect(service.acceptances[0]!.confirmed).toEqual(['UnwrittenSections', 'OverCeiling']);
    expect(host.accepted.length).toBe(1);
  });

  it('accepts nothing and shows the piece again when it changed while the creator was deciding', async () => {
    service.acceptOutcome = { status: 'confirmation_required', issues: [{ issue: 'UnwrittenSections', sectionIds: ['c'] }] };
    await create();

    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, THREE[1]!, unwritten('c', { heading: 'Until next time' })], { revisionNumber: 5 })) }];
    await click('Accept this piece');

    expect(host.accepted.length).toBe(0);
    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain('changed while you were deciding');
    expect(text(el.querySelector('[data-testid="issues"]')!)).toContain('“Until next time” has not been written.');
  });

  it('will not accept over unsaved text or while a section is being written, and says why', async () => {
    await create();
    await click('Edit', card(1));
    await type(box(1), 'Unsaved.');

    const accept = button('Accept this piece')!;
    expect(accept.getAttribute('aria-disabled')).toBe('true');
    expect(el.querySelector(`#${accept.getAttribute('aria-describedby')}`)?.textContent).toContain('Save or discard your changes');

    accept.click();
    await settle();
    expect(service.acceptances.length).toBe(0);
  });

  it('shows an already accepted piece read-only, with nothing to press', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(el.querySelector('[data-testid="accepted"]')).not.toBeNull();
    expect(el.querySelectorAll('article button').length).toBe(0);
    expect(el.querySelector('[data-testid="accept-actions"]')).toBeNull();
    expect(el.querySelectorAll('article .body-read').length).toBe(3);
  });

  // ---- one screen, two steps of a flow (LF.6.2) --------------------------------------------------------

  it('shows only the writing half when a flow asks for it: sections and their controls, nothing about accepting', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Writing' })) }];
    await create();
    host.show.set('write');
    await settle();

    expect(el.querySelectorAll('article.section').length).toBe(3);
    expect(button('Edit')).not.toBeNull();
    expect(button('Accept this piece')).toBeNull();
    expect(el.querySelector('[data-testid="accept-actions"]')).toBeNull();
    expect(el.querySelector('[data-testid="downloads"]')).toBeNull();
    expect(el.querySelector('cp-long-form-seo-suggestions')).toBeNull();
  });

  it('shows only the accepting half when a flow asks for it: the piece to read, with no control that changes it', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Writing' })) }];
    await create();
    host.show.set('accept');
    await settle();

    // The same sections, to read.
    expect(el.querySelectorAll('article.section').length).toBe(3);
    expect(button('Edit')).toBeNull();
    expect(Array.from(el.querySelectorAll('button')).some((b) => /Write the/.test(b.textContent ?? ''))).toBeFalse();
    expect(button('Accept this piece')).not.toBeNull();
  });

  it('offers what an accepted piece offers on the accepting half, and none of it on the writing half', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();
    host.show.set('accept');
    await settle();

    expect(el.querySelector('[data-testid="downloads"]')).not.toBeNull();
    expect(el.querySelector('cp-long-form-seo-suggestions')).not.toBeNull();

    host.show.set('write');
    await settle();

    expect(el.querySelector('[data-testid="downloads"]')).toBeNull();
    expect(el.querySelector('cp-long-form-seo-suggestions')).toBeNull();
  });

  it('reports each reading of the piece, so a flow knows where it stands without reading it again', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Writing' })) }];
    await create();

    expect(host.reads.length).toBeGreaterThan(0);
    expect(host.reads.at(-1)!.status).toBe('Writing');
    expect(service.readCalls.length).toBe(1);
  });

  // ---- downloading (LF.5.2) ----------------------------------------------------------------------------

  function downloads(): { label: string; href: string | null; download: boolean }[] {
    return Array.from(el.querySelectorAll<HTMLAnchorElement>('[data-testid="downloads"] a')).map((link) => ({
      label: text(link),
      href: link.getAttribute('href'),
      download: link.hasAttribute('download'),
    }));
  }

  it('offers both downloads of an accepted piece as plain links to the export routes', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(downloads()).toEqual([
      { label: 'Download Markdown', href: 'https://gateway.example/cozy-fall/d-1/exports/markdown', download: true },
      { label: 'Download HTML', href: 'https://gateway.example/cozy-fall/d-1/exports/html', download: true },
    ]);
    expect(el.querySelector('[data-testid="needs-review"]')).toBeNull();
  });

  // ---- SEO suggestions (LF.5.3) --------------------------------------------------------------------------

  it('offers SEO suggestions for an accepted piece only, and about its accepted revision', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(el.querySelector('cp-long-form-seo-suggestions')).not.toBeNull();
    expect(button('Suggest SEO details')).not.toBeNull();

    for (const status of ['Outlined', 'Writing', 'Assembled', 'NeedsReview'] as const) {
      service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status, acceptedRevisionNumber: status === 'NeedsReview' ? 4 : null })) }];
      await create();

      expect(el.querySelector('cp-long-form-seo-suggestions')).withContext(status).toBeNull();
    }
  });

  it('offers no download of a piece that has not been accepted', async () => {
    for (const status of ['Outlined', 'Writing', 'Assembled'] as const) {
      service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status })) }];
      await create();

      expect(el.querySelector('[data-testid="downloads"]')).withContext(status).toBeNull();
    }
  });

  it('still offers the downloads once the sources have moved, and says they are the piece as accepted', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'NeedsReview', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(downloads().map((link) => link.label)).toEqual(['Download Markdown', 'Download HTML']);
    expect(el.querySelector('[data-testid="needs-review"]')?.textContent).toContain('as you accepted it');
  });

  // ---- which source moved (LF.6.1) -----------------------------------------------------------------------

  function staleNotice(reasons: readonly string[]): Promise<string> {
    service.reads = [
      {
        status: 'found',
        review: reviewOf(documentOf(THREE, { status: 'NeedsReview', acceptedRevisionNumber: 4, staleReasons: reasons, staleSince: '2026-10-12T09:00:00Z' })),
      },
    ];

    return create().then(() => el.querySelector('[data-testid="needs-review"]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '');
  }

  it('says which source moved, and that nothing in the piece was changed', async () => {
    const recipe = await staleNotice(['RecipeChanged']);
    expect(recipe).toContain('A recipe this piece was written from has changed.');
    expect(recipe).not.toContain('brand style guide');
    expect(recipe).toContain('Nothing in it has been changed');

    const voice = await staleNotice(['VoiceChanged']);
    expect(voice).toContain('Your brand style guide has changed.');
    expect(voice).not.toContain('A recipe this piece');
  });

  it('names both sources when both moved, each once', async () => {
    const both = await staleNotice(['RecipeChanged', 'VoiceChanged']);

    expect(both).toContain('A recipe this piece was written from has changed.');
    expect(both).toContain('Your brand style guide has changed.');
    expect(el.querySelectorAll('[data-testid="needs-review"] li').length).toBe(2);
  });

  it('still says something changed for a reason it does not know, or when none is given', async () => {
    expect(await staleNotice(['TemplateChanged'])).toContain('Something this piece was written from has changed.');
    expect(await staleNotice([])).toContain('Something this piece was written from has changed.');
    expect(el.querySelectorAll('[data-testid="needs-review"] li').length).toBe(1);
  });

  it('says nothing about a source moving while the piece is accepted and current', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(el.querySelector('[data-testid="needs-review"]')).toBeNull();
  });

  it('offers no dead link while the gateway address is not known', async () => {
    service.gateway = null;
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'Accepted', acceptedRevisionNumber: 4 })) }];
    await create();

    expect(el.querySelector('[data-testid="downloads"]')).toBeNull();
  });

  it('says a piece cannot be changed when it is neither being written nor accepted', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf(THREE, { status: 'NeedsReview' })) }];
    await create();

    expect(text()).toContain('cannot be changed at the moment');
    expect(button('Edit')).toBeNull();
    expect(button('Accept this piece')).toBeNull();
  });

  it('offers nothing to accept while nothing is written, and says why', async () => {
    service.reads = [
      { status: 'found', review: reviewOf(documentOf([unwritten('a', { heading: 'Why this cake' }), unwritten('b', { heading: 'Three tips' })], { status: 'Outlined', markdown: '' })) },
    ];
    await create();

    const accept = button('Accept this piece')!;
    expect(accept.getAttribute('aria-disabled')).toBe('true');
    expect(el.querySelector(`#${accept.getAttribute('aria-describedby')}`)?.textContent).toContain('Nothing has been written yet');
    expect(text(card(1))).toContain('Nothing has been written for this section yet.');
  });

  it('says what the server said when it refuses an acceptance, or the creator may not accept', async () => {
    service.acceptOutcome = { status: 'refused', code: 'content.long_form.acceptance.nothing_written.unprocessable', message: 'None of this piece has been written yet.' };
    await create();
    await click('Accept this piece');
    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain('None of this piece has been written yet.');

    service.acceptOutcome = { status: 'forbidden' };
    await click('Accept this piece');
    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain('need to be a contributor');

    service.acceptOutcome = { status: 'not_found' };
    await click('Accept this piece');
    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain('no longer here');
    expect(host.accepted.length).toBe(0);
  });

  it('says so when an acceptance could not be sent, and changes nothing', async () => {
    service.acceptOutcome = { status: 'unavailable' };
    await create();
    await click('Accept this piece');

    expect(el.querySelector('[data-testid="document-problem"]')?.textContent).toContain('Nothing was changed');
    expect(host.accepted.length).toBe(0);
  });

  // ---- the unsaved-edit guard ------------------------------------------------------------------------

  it('lets the creator leave without asking when nothing is unsaved, including with an untouched box open', async () => {
    await create();
    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();

    await click('Edit', card(1));
    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();
    expect(confirmService.calls.length).toBe(0);
  });

  it('asks before leaving with unsaved text, and staying keeps it', async () => {
    await create();
    await click('Edit', card(1));
    await type(box(1), 'Unsaved.');

    confirmService.answer = false;
    expect(await host.review().confirmDiscardIfDirty()).toBeFalse();
    expect(confirmService.calls[0]!.tone).toBe('danger');
    expect(box(1).value).toBe('Unsaved.');

    confirmService.answer = true;
    expect(await host.review().confirmDiscardIfDirty()).toBeTrue();
  });

  it('holds a browser close while there is unsaved text, and only then', async () => {
    // The listener is captured and called directly: dispatching a real `beforeunload` on the window is what
    // the test runner itself listens for, and it reads one as the page having reloaded.
    const listen = spyOn(window, 'addEventListener').and.callThrough();
    const unlisten = spyOn(window, 'removeEventListener').and.callThrough();
    await create();

    const handler = listen.calls.allArgs().find(([kind]) => kind === 'beforeunload')?.[1] as (event: Event) => void;
    expect(handler).toBeDefined();

    const clean = new Event('beforeunload', { cancelable: true });
    handler(clean);
    expect(clean.defaultPrevented).toBeFalse();

    await click('Edit', card(1));
    await type(box(1), 'Unsaved.');
    const dirty = new Event('beforeunload', { cancelable: true });
    handler(dirty);
    expect(dirty.defaultPrevented).toBeTrue();

    fixture.destroy();
    expect(unlisten.calls.allArgs().some(([kind, removed]) => kind === 'beforeunload' && removed === handler)).toBeTrue();
  });

  // ---- accessibility and layout ----------------------------------------------------------------------

  it('says every status in words, names each control by its section, and has its live region before anything is said', async () => {
    service.reads = [{ status: 'found', review: reviewOf(documentOf([THREE[0]!, unwritten('b', { heading: 'Three tips' })])) }];
    await create();

    expect(card(1).querySelector('cp-status-pill')?.textContent?.trim()).toContain('Written');
    expect(card(2).querySelector('cp-status-pill')?.textContent?.trim()).toContain('Not written yet');
    expect(button('Edit', card(1))?.getAttribute('aria-label')).toBe('Edit Why this cake');
    expect(button('Mark as settled', card(1))?.getAttribute('aria-label')).toBe('Mark Why this cake as settled');
    expect(button('Write it for me', card(2))?.getAttribute('aria-label')).toBe('Have Three tips written for you');

    const region = el.querySelector('[data-testid="announcement"]');
    expect(region?.getAttribute('role')).toBe('status');
    expect(region?.textContent?.trim()).toBe('');

    const ids = Array.from(el.querySelectorAll<HTMLElement>('[id]')).map((node) => node.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('holds a very long piece inside a narrow column without scrolling sideways', async () => {
    const longWord = 'buttermilk'.repeat(80);
    const sections = Array.from({ length: 12 }, (_, index) =>
      section(`s${index}`, {
        heading: `A long heading that keeps going ${longWord.slice(0, 120)}`,
        body: `${longWord}\n\n${'A paragraph of ordinary words. '.repeat(60)}`,
      }),
    );
    service.reads = [{ status: 'found', review: reviewOf(documentOf(sections)) }];
    await create();

    // The width a phone has, which is also what a desktop window has left at 200% zoom.
    const hostElement = el.querySelector<HTMLElement>('cp-long-form-document-review')!;
    hostElement.style.width = '320px';
    await settle();

    expect(el.querySelectorAll('article').length).toBe(12);
    expect(hostElement.scrollWidth).toBeLessThanOrEqual(hostElement.clientWidth);

    for (const article of Array.from(el.querySelectorAll<HTMLElement>('article'))) {
      expect(article.scrollWidth).withContext(article.id).toBeLessThanOrEqual(article.clientWidth);
    }
  });
});
