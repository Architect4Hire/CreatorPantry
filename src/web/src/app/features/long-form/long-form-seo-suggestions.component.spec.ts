import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flushMicrotasks, tick } from '@angular/core/testing';

import { AiOperationStatus, AiProposalDetail, AiProposalStatus, AiProposedChange } from '../../models/ai-proposal.models';
import { AiQuotaRefusal } from '../../models/ai-quota.models';
import { isEmptyLongFormSeoPackage, readLongFormSeoPackage } from '../../models/long-form-seo.models';
import { AiRequestOutcome, AiWatchOperationOutcome } from '../../services/ai-request';
import { LongFormSeoService } from '../../services/long-form-seo.service';
import { LONG_FORM_SEO_POLL_MS, LongFormSeoSuggestionsComponent } from './long-form-seo-suggestions.component';

let nextChange = 0;

function add(targetId: string, text: string, position = 0): AiProposedChange {
  return row({ changeKind: 'Add', targetId, afterValue: text, proposedPosition: position });
}

function set(targetId: string, fieldName: string, value: string): AiProposedChange {
  return row({ changeKind: 'Set', targetId, fieldName, afterValue: value });
}

function row(overrides: Partial<AiProposedChange>): AiProposedChange {
  nextChange += 1;

  return {
    changeId: `c-${nextChange}`,
    changeKind: 'Add',
    targetKind: 'ContentSection',
    targetId: null,
    fieldName: null,
    beforeValue: null,
    afterValue: null,
    proposedPosition: null,
    disposition: 'Pending',
    ...overrides,
  };
}

/** A package as the server stores one: an Add per suggestion, with Set rows naming what it is. */
function packageOf(): AiProposalDetail {
  const title = add('t-title', 'Soda Bread for Days With No Time');
  const slug = add('t-slug', 'soda-bread-for-days-with-no-time');
  const meta = add('t-meta', 'The top ranking soda bread, baked in 25 minutes.');
  const first = add('t-p1', 'soda bread', 0);
  const second = add('t-p2', 'no knead bread', 1);
  const link = add('t-link', 'Buttermilk soda bread');

  return {
    proposalId: 'p-1',
    outputSchemaVersion: 'content.seo-package.v1',
    promptTemplateId: 'content.seo-package',
    promptTemplateVersion: '1.1.0',
    promptTemplateBodyChecksum: 'sha256:abc',
    providerName: 'test',
    modelName: 'test',
    createdAt: '2026-10-12T00:00:00Z',
    changes: [
      // Deliberately out of order, as rows may arrive: the second phrase before the first.
      second,
      set('t-p2', 'section', 'keyPhrases'),
      title,
      set('t-title', 'section', 'seoTitle'),
      slug,
      set('t-slug', 'section', 'slug'),
      set('t-slug', 'slugBasis', 'seoTitle'),
      meta,
      set('t-meta', 'section', 'metaDescription'),
      first,
      set('t-p1', 'section', 'keyPhrases'),
      link,
      set('t-link', 'section', 'internalLinks'),
      set('t-link', 'recipeId', 'r-1'),
      set('t-link', 'reason', 'It is the recipe this piece is about.'),

      // A section this client has never heard of: left out, not guessed at.
      add('t-new', 'something new'),
      set('t-new', 'section', 'openGraphTitle'),
    ],
    warnings: [
      { kind: 'Limitation', message: '[seo.unsupported_metric] "top ranking" is a claim with no search data behind it.', changeId: meta.changeId },
      { kind: 'Limitation', message: '[seo.slug_uniqueness_not_checked] Whether it is unique has not been checked.', changeId: slug.changeId },
      { kind: 'Limitation', message: '[seo.key_phrases_not_from_search_data] These are not from search data.', changeId: first.changeId },
      { kind: 'Limitation', message: '[seo.key_phrases_not_from_search_data] These are not from search data.', changeId: second.changeId },
      { kind: 'Limitation', message: '[model] the piece says little about storage.', changeId: null },

      // A model note on one suggestion: it must never read like the server's finding beside it.
      { kind: 'Limitation', message: '[model] this title may be long for some results pages.', changeId: title.changeId },
    ],
  };
}

function operation(status: AiOperationStatus, proposal: AiProposalDetail | null = null): AiProposalStatus {
  return {
    aiProposalRequestId: 'req-1',
    status,
    taskType: 'SeoPackage',
    scope: 'NotApplicable',
    sourceVersionId: null,
    requestedAt: '2026-10-12T00:00:00Z',
    statusChangedAt: '2026-10-12T00:00:00Z',
    failureCategory: status === 'Failed' ? 'DomainInvalid' : null,
    proposal,
  };
}

class FakeLongFormSeoService {
  requestOutcomes: AiRequestOutcome[] = [{ status: 'accepted', operation: operation('Requested'), replayed: false }];
  watchOutcomes: AiWatchOperationOutcome[] = [{ status: 'found', operation: operation('Proposed', packageOf()) }];

  requests: [string, string, number, string][] = [];
  watches: [string, string, string][] = [];

  request(slug: string, draftId: string, revisionNumber: number, key: string): Promise<AiRequestOutcome> {
    this.requests.push([slug, draftId, revisionNumber, key]);

    return Promise.resolve(this.requestOutcomes.length > 1 ? this.requestOutcomes.shift()! : this.requestOutcomes[0]!);
  }

  watch(slug: string, draftId: string, requestId: string): Promise<AiWatchOperationOutcome> {
    this.watches.push([slug, draftId, requestId]);

    return Promise.resolve(this.watchOutcomes.length > 1 ? this.watchOutcomes.shift()! : this.watchOutcomes[0]!);
  }
}

@Component({
  standalone: true,
  imports: [LongFormSeoSuggestionsComponent],
  template: `<cp-long-form-seo-suggestions
    [workspaceSlug]="'cozy-fall'"
    [draftId]="draftId()"
    [revisionNumber]="revision()"
    sectionId="seo"
    (blocked)="blocked.push($event)"
  />`,
})
class HostComponent {
  readonly draftId = signal('d-1');
  readonly revision = signal(4);
  readonly blocked: AiQuotaRefusal[] = [];
}

describe('readLongFormSeoPackage', () => {
  it('reads each suggestion from its rows, in position order, with the cautions said about it', () => {
    const seo = readLongFormSeoPackage(packageOf());

    expect(seo.seoTitle?.text).toBe('Soda Bread for Days With No Time');
    expect(seo.slug?.text).toBe('soda-bread-for-days-with-no-time');
    expect(seo.metaDescription?.text).toContain('baked in 25 minutes');
    expect(seo.keyPhrases.map((phrase) => phrase.text)).toEqual(['soda bread', 'no knead bread']);
    expect(seo.internalLinks).toEqual([
      { text: 'Buttermilk soda bread', warnings: [], recipeId: 'r-1', reason: 'It is the recipe this piece is about.' },
    ]);

    // The code is for machines; a creator is shown the sentence.
    expect(seo.metaDescription?.warnings).toEqual(['"top ranking" is a claim with no search data behind it.']);
    expect(seo.slug?.warnings).toEqual(['Whether it is unique has not been checked.']);

    // A note the model wrote still says it is the model's. Only a dotted server code is ever removed.
    expect(seo.warnings).toEqual(['The model noted: the piece says little about storage.']);
    expect(seo.seoTitle?.warnings).toEqual(['The model noted: this title may be long for some results pages.']);
  });

  it('leaves out a section it does not know rather than showing it as something else', () => {
    const seo = readLongFormSeoPackage(packageOf());

    expect(JSON.stringify(seo)).not.toContain('something new');
  });

  it('knows a package with nothing in it', () => {
    expect(isEmptyLongFormSeoPackage(readLongFormSeoPackage({ ...packageOf(), changes: [], warnings: [] }))).toBeTrue();
    expect(isEmptyLongFormSeoPackage(readLongFormSeoPackage(packageOf()))).toBeFalse();
  });
});

describe('LongFormSeoSuggestionsComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let el: HTMLElement;
  let service: FakeLongFormSeoService;

  function text(): string {
    return el.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  }

  function button(): HTMLButtonElement {
    return el.querySelector<HTMLButtonElement>('button')!;
  }

  function settle(): void {
    flushMicrotasks();
    fixture.detectChanges();
  }

  function ask(): void {
    button().click();
    settle();
  }

  beforeEach(async () => {
    service = new FakeLongFormSeoService();

    await TestBed.configureTestingModule({
      providers: [{ provide: LongFormSeoService, useValue: service }],
    }).compileComponents();

    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
  });

  it('asks for nothing until it is asked, and says what the suggestions are and are not', () => {
    expect(service.requests).toEqual([]);
    expect(button().textContent).toContain('Suggest SEO details');
    expect(text()).toContain('Nothing is added to the piece, and none of it is search data.');
    expect(el.querySelector('[data-testid="seo-facts"]')).toBeNull();
  });

  it('asks about the accepted revision with a key, waits, and shows what was written', fakeAsync(() => {
    service.watchOutcomes = [
      { status: 'found', operation: operation('Running') },
      { status: 'found', operation: operation('Proposed', packageOf()) },
    ];

    ask();

    expect(service.requests.length).toBe(1);
    expect(service.requests[0]!.slice(0, 3)).toEqual(['cozy-fall', 'd-1', 4]);
    expect(service.requests[0]![3]).toMatch(/^[0-9a-f-]{36}$/);
    expect(el.querySelector('[data-testid="seo-waiting"]')).not.toBeNull();
    expect(button().getAttribute('aria-disabled')).toBe('true');
    expect(el.querySelector('[data-testid="seo-status"]')?.textContent).toContain('Writing SEO suggestions');

    tick(LONG_FORM_SEO_POLL_MS);
    settle();

    expect(service.watches.length).toBe(2);
    expect(el.querySelector('[data-testid="seo-waiting"]')).toBeNull();
    expect(text()).toContain('Soda Bread for Days With No Time');
    expect(text()).toContain('soda-bread-for-days-with-no-time');
    expect(text()).toContain('soda bread');
    expect(text()).toContain('It is the recipe this piece is about.');

    // Identified as written for the creator, with each caution beside what it is about, said once.
    expect(text()).toContain('Written for you');
    expect(text()).toContain('"top ranking" is a claim with no search data behind it.');
    expect(text().split('These are not from search data.').length - 1).toBe(1);
    expect(text()).not.toContain('seo.unsupported_metric');
    expect(el.querySelector('[data-testid="seo-status"]')?.textContent).toContain('SEO suggestions are ready.');

    // A real description list, and no control that applies anything: there is nothing to accept.
    expect(el.querySelector('dl')).not.toBeNull();
    expect(Array.from(el.querySelectorAll('button')).map((b) => b.textContent?.trim())).toEqual(['Suggest again']);
    expect(el.querySelector('a')).toBeNull();
  }));

  it('ignores a second press while one request is under way', fakeAsync(() => {
    service.watchOutcomes = [{ status: 'found', operation: operation('Running') }];

    ask();
    ask();

    expect(service.requests.length).toBe(1);

    fixture.destroy();
    tick(LONG_FORM_SEO_POLL_MS);
  }));

  it('keeps the key when the reply was lost, so the retry is the same request', fakeAsync(() => {
    service.requestOutcomes = [{ status: 'unavailable' }, { status: 'accepted', operation: operation('Requested'), replayed: true }];

    ask();
    expect(el.querySelector('[data-testid="seo-problem"]')?.textContent).toContain('Try again');

    ask();

    expect(service.requests.length).toBe(2);
    expect(service.requests[1]![3]).toBe(service.requests[0]![3]);
  }));

  it('says so when the suggestions could not be written, and the next request is a new one', fakeAsync(() => {
    service.watchOutcomes = [{ status: 'found', operation: operation('Failed') }];

    ask();

    expect(el.querySelector('[data-testid="seo-problem"]')?.textContent).toContain('could not be written');
    expect(el.querySelector('[data-testid="seo-facts"]')).toBeNull();

    service.watchOutcomes = [{ status: 'found', operation: operation('Proposed', packageOf()) }];
    ask();

    expect(service.requests[1]![3]).not.toBe(service.requests[0]![3]);
    expect(el.querySelector('[data-testid="seo-problem"]')).toBeNull();
    expect(el.querySelector('[data-testid="seo-facts"]')).not.toBeNull();
  }));

  it('says a package with nothing in it is nothing, rather than showing an empty list', fakeAsync(() => {
    service.watchOutcomes = [{ status: 'found', operation: operation('Proposed', { ...packageOf(), changes: [], warnings: [] }) }];

    ask();

    expect(el.querySelector('[data-testid="seo-empty"]')).not.toBeNull();
    expect(el.querySelectorAll('dl > *').length).toBe(0);
  }));

  it('gives the server’s own reason when the piece cannot be written about', fakeAsync(() => {
    service.requestOutcomes = [
      { status: 'refused', code: 'ai.seoPackage.longFormDraft.not_accepted.conflict', message: 'Something this piece was written from has changed since it was accepted. Review it first.' },
    ];

    ask();

    expect(el.querySelector('[data-testid="seo-problem"]')?.textContent).toContain('Review it first.');
    expect(service.watches).toEqual([]);
  }));

  it('hands a spent allowance to the page that shows it, and says nothing of its own', fakeAsync(() => {
    const refusal = { status: 'account_suspended', unit: 'Credits' } as AiQuotaRefusal;
    service.requestOutcomes = [refusal];

    ask();

    expect(host.blocked).toEqual([refusal]);
    expect(el.querySelector('[data-testid="seo-problem"]')).toBeNull();
    expect(button().getAttribute('aria-disabled')).toBeNull();
  }));

  it('drops what it was showing when the piece or its accepted revision changes', fakeAsync(() => {
    ask();
    expect(el.querySelector('[data-testid="seo-facts"]')).not.toBeNull();

    host.revision.set(5);
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="seo-facts"]')).toBeNull();
    expect(button().textContent).toContain('Suggest SEO details');
  }));

  it('does not show an answer that arrives for a piece it is no longer about', fakeAsync(() => {
    service.watchOutcomes = [
      { status: 'found', operation: operation('Running') },
      { status: 'found', operation: operation('Proposed', packageOf()) },
    ];

    ask();
    host.draftId.set('d-2');
    fixture.detectChanges();

    tick(LONG_FORM_SEO_POLL_MS);
    settle();

    expect(el.querySelector('[data-testid="seo-facts"]')).toBeNull();
    expect(service.watches.length).toBe(1);
  }));
});
