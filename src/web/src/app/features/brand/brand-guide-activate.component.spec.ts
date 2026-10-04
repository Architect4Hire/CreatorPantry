import { ComponentFixture, TestBed } from '@angular/core/testing';

import { BrandStyleGuideVersionRow } from '../../models/brand-style-guide.models';
import { BrandStyleGuideActivateOutcome, BrandStyleGuideService } from '../../services/brand-style-guide.service';
import {
  BrandGuideActivateComponent,
  BrandStyleGuideVersionActivated,
} from './brand-guide-activate.component';

function version(overrides: Partial<BrandStyleGuideVersionRow> = {}): BrandStyleGuideVersionRow {
  return {
    id: 'v4',
    versionNumber: 4,
    status: 'Approved',
    sourceCount: 2,
    staleSourceCount: 0,
    changeReason: null,
    createdAt: '2026-09-20T10:00:00Z',
    isActive: false,
    ...overrides,
  };
}

const ACTIVATED: BrandStyleGuideActivateOutcome = {
  status: 'activated',
  activation: {
    guideId: 'g1',
    versionId: 'v4',
    versionNumber: 4,
    activatedAt: '2026-10-01T09:00:00Z',
    alreadyActive: false,
    replacedVersionNumber: 2,
  },
};

/** One recorded call to the activation route, as the dialog made it. */
interface ActivateCall {
  readonly slug: string;
  readonly guideId: string;
  readonly versionNumber: number;
  readonly expectedActiveVersionId: string | null;
  readonly idempotencyKey: string;
  readonly reason: string | null;
}

class StubGuideService {
  outcome: BrandStyleGuideActivateOutcome = ACTIVATED;
  outcomes: BrandStyleGuideActivateOutcome[] | null = null;
  readonly calls: ActivateCall[] = [];

  activate(
    slug: string,
    guideId: string,
    versionNumber: number,
    expectedActiveVersionId: string | null,
    idempotencyKey: string,
    reason: string | null = null,
  ): Promise<BrandStyleGuideActivateOutcome> {
    this.calls.push({ slug, guideId, versionNumber, expectedActiveVersionId, idempotencyKey, reason });

    return Promise.resolve(this.outcomes?.shift() ?? this.outcome);
  }
}

describe('BrandGuideActivateComponent', () => {
  let fixture: ComponentFixture<BrandGuideActivateComponent>;
  let service: StubGuideService;
  let activated: BrandStyleGuideVersionActivated[];
  let reloads: number;
  let closes: number;

  async function render(
    options: {
      version?: BrandStyleGuideVersionRow;
      expectedActiveVersionId?: string | null;
      expectedActiveVersionNumber?: number | null;
    } = {},
  ): Promise<void> {
    fixture = TestBed.createComponent(BrandGuideActivateComponent);
    fixture.componentRef.setInput('workspaceSlug', 'sams-kitchen');
    fixture.componentRef.setInput('guideId', 'g1');
    fixture.componentRef.setInput('version', options.version ?? version());
    // `undefined` means the test did not say; an explicit null is the claim that there is no default.
    fixture.componentRef.setInput(
      'expectedActiveVersionId',
      options.expectedActiveVersionId === undefined ? 'v2' : options.expectedActiveVersionId,
    );
    fixture.componentRef.setInput(
      'expectedActiveVersionNumber',
      options.expectedActiveVersionNumber === undefined ? 2 : options.expectedActiveVersionNumber,
    );

    activated = [];
    reloads = 0;
    closes = 0;
    fixture.componentInstance.activated.subscribe((event) => activated.push(event));
    fixture.componentInstance.reloadRequested.subscribe(() => (reloads += 1));
    fixture.componentInstance.closed.subscribe(() => (closes += 1));

    fixture.detectChanges();
    await settle();
  }

  async function settle(): Promise<void> {
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function root(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function text(): string {
    return root().textContent ?? '';
  }

  function maybeButton(label: string): HTMLButtonElement | null {
    return (
      Array.from(root().querySelectorAll('button')).find((each) => each.textContent?.includes(label)) ?? null
    );
  }

  function button(label: string): HTMLButtonElement {
    const match = maybeButton(label);
    if (!match) throw new Error(`no button labelled "${label}"`);

    return match;
  }

  async function submit(): Promise<void> {
    button('Write with this version').click();
    await settle();
  }

  async function typeReason(value: string): Promise<void> {
    const textarea = root().querySelector('textarea') as HTMLTextAreaElement;
    textarea.value = value;
    textarea.dispatchEvent(new Event('input'));
    await settle();
  }

  beforeEach(() => {
    service = new StubGuideService();
    TestBed.configureTestingModule({
      imports: [BrandGuideActivateComponent],
      providers: [{ provide: BrandStyleGuideService, useValue: service }],
    });
  });

  // ---- What it says before anything is written ----

  it('says what activating changes, and what it leaves alone', async () => {
    await render();

    expect(text()).toContain("Version 4 becomes this workspace's default voice");
    expect(text()).toContain('replaces version 2');
    expect(text()).toContain('stays exactly where it is in the history');
    expect(text()).toContain('Nothing is published anywhere');
  });

  /**
   * Audit 11A.24a, B1. This dialog used to say "the voice this workspace writes and makes pictures with" and
   * "everything generated from now on is grounded on it". No writing handler reads a guide yet (11A.20) and
   * there is no image generation at all (11A.21), so the one place an active guide has any effect is "Test my
   * style". Asserting the absence as well as the presence is what stops the promise drifting back in before
   * those two prompts land.
   */
  it('does not promise that writing or pictures use the guide yet', async () => {
    await render();

    expect(text()).toContain('Test my style');
    expect(text()).toContain('does not read it yet');
    expect(text()).not.toContain('makes pictures with');
    expect(text()).not.toContain('Everything generated from now on');
  });

  it('says nothing is being replaced when the workspace has no default yet', async () => {
    await render({ expectedActiveVersionId: null, expectedActiveVersionNumber: null });

    expect(text()).toContain('no default voice yet');
    expect(text()).not.toContain('replaces version');
  });

  it('warns when this version already holds the default, where activating would change nothing', async () => {
    await render({ version: version({ isActive: true }) });

    expect(text()).toContain('already what this workspace writes with');
  });

  // ---- The request ----

  it('sends the confirmation, the expected active version and the typed reason', async () => {
    await render();
    await typeReason('Reads most like me.');
    await submit();

    expect(service.calls).toHaveSize(1);
    expect(service.calls[0].slug).toBe('sams-kitchen');
    expect(service.calls[0].guideId).toBe('g1');
    expect(service.calls[0].versionNumber).toBe(4);
    expect(service.calls[0].expectedActiveVersionId).toBe('v2');
    expect(service.calls[0].reason).toBe('Reads most like me.');
  });

  /** Null asserts the workspace has no default, which the server checks rather than assumes. */
  it('sends a null expected version when the workspace has no default', async () => {
    await render({ expectedActiveVersionId: null, expectedActiveVersionNumber: null });
    await submit();

    expect(service.calls[0].expectedActiveVersionId).toBeNull();
  });

  it('omits a blank reason rather than sending an empty one', async () => {
    await render();
    await typeReason('   ');
    await submit();

    expect(service.calls[0].reason).toBeNull();
  });

  it('reports the activation to its host, including what it replaced', async () => {
    await render();
    await submit();

    expect(activated).toHaveSize(1);
    expect(activated[0]).toEqual({ versionNumber: 4, alreadyActive: false, replacedVersionNumber: 2 });
  });

  it('passes on that nothing was written when the version already held the default', async () => {
    service.outcome = {
      status: 'activated',
      activation: {
        guideId: 'g1',
        versionId: 'v4',
        versionNumber: 4,
        activatedAt: '2026-10-01T09:00:00Z',
        alreadyActive: true,
        replacedVersionNumber: null,
      },
    };
    await render({ version: version({ isActive: true }) });
    await submit();

    expect(activated[0].alreadyActive).toBeTrue();
  });

  // ---- Idempotency ----

  it('reuses one key while the request has not changed, so a retry cannot activate twice', async () => {
    service.outcomes = [{ status: 'failed', reason: 'unavailable' }, ACTIVATED];
    await render();
    await submit();
    await submit();

    expect(service.calls).toHaveSize(2);
    expect(service.calls[1].idempotencyKey).toBe(service.calls[0].idempotencyKey);
  });

  it('starts a new key when the reason has been rewritten, which is a different request', async () => {
    service.outcomes = [{ status: 'failed', reason: 'unavailable' }, ACTIVATED];
    await render();
    await submit();
    await typeReason('Actually, this one.');
    await submit();

    expect(service.calls[1].idempotencyKey).not.toBe(service.calls[0].idempotencyKey);
  });

  // ---- Refusals, each in its own terms ----

  it('names the version that actually holds the default when the expectation was wrong', async () => {
    service.outcome = {
      status: 'failed',
      reason: 'activation_conflict',
      active: { guideId: 'g1', versionId: 'v7', versionNumber: 7 },
    };
    await render();
    await submit();

    expect(text()).toContain('Version 7 is what this workspace writes with now');
    expect(root().querySelector('[role="alert"]')).not.toBeNull();
  });

  it('falls back to saying the default moved when the refusal named no version', async () => {
    service.outcome = { status: 'failed', reason: 'activation_conflict' };
    await render();
    await submit();

    expect(text()).toContain("default changed while this dialog was open");
  });

  it('says an unapproved version has to be approved first', async () => {
    service.outcome = { status: 'failed', reason: 'unapproved' };
    await render();
    await submit();

    expect(text()).toContain("hasn't been approved yet");
  });

  it('says a stale version needs rewriting from the current examples', async () => {
    service.outcome = { status: 'failed', reason: 'stale' };
    await render();
    await submit();

    expect(text()).toContain('has been replaced since');
  });

  it('says an empty version would leave nothing to write with', async () => {
    service.outcome = { status: 'failed', reason: 'empty' };
    await render();
    await submit();

    expect(text()).toContain('says nothing yet');
  });

  it('says an archived guide has to be brought back first', async () => {
    service.outcome = { status: 'failed', reason: 'archived' };
    await render();
    await submit();

    expect(text()).toContain('archived');
  });

  it('names the role a refused caller is missing', async () => {
    service.outcome = { status: 'failed', reason: 'forbidden' };
    await render();
    await submit();

    expect(text()).toContain('Owner role');
  });

  it('offers a reload after a refusal the same request cannot survive, and no retry', async () => {
    service.outcome = { status: 'failed', reason: 'stale' };
    await render();
    await submit();

    expect(maybeButton('Reload guide')).not.toBeNull();
    expect(button('Write with this version').disabled).toBeTrue();

    button('Reload guide').click();
    await settle();

    expect(reloads).toBe(1);
  });

  it('keeps the button enabled after a failure that trying again could survive', async () => {
    service.outcome = { status: 'failed', reason: 'unavailable' };
    await render();
    await submit();

    expect(text()).toContain('Check your connection');
    expect(button('Write with this version').disabled).toBeFalse();
    expect(maybeButton('Reload guide')).toBeNull();
  });

  it('keeps the typed reason after a refusal', async () => {
    service.outcome = { status: 'failed', reason: 'unavailable' };
    await render();
    await typeReason('Reads most like me.');
    await submit();

    expect((root().querySelector('textarea') as HTMLTextAreaElement).value).toBe('Reads most like me.');
  });

  // ---- Dismissal ----

  it('closes on Cancel without writing anything', async () => {
    await render();
    button('Cancel').click();
    await settle();

    expect(closes).toBe(1);
    expect(service.calls).toHaveSize(0);
  });

  it('closes on Escape, which means stay rather than confirm', async () => {
    await render();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    await settle();

    expect(closes).toBe(1);
    expect(service.calls).toHaveSize(0);
  });

  // ---- Accessibility ----

  it('labels the reason field and marks the button busy while the request is in flight', async () => {
    await render();

    const textarea = root().querySelector('textarea') as HTMLTextAreaElement;
    const label = root().querySelector(`label[for="${textarea.id}"]`);

    expect(label?.textContent).toContain('Why this version?');

    let release = (): void => {};
    service.activate = () =>
      new Promise<BrandStyleGuideActivateOutcome>((resolve) => {
        release = () => resolve(ACTIVATED);
      });

    button('Write with this version').click();
    fixture.detectChanges();

    expect(button('Setting…').getAttribute('aria-busy')).toBe('true');

    release();
    await settle();
  });
});
