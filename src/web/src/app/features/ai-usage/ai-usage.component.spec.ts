import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RuntimeConfigService } from '../../core/runtime-config.service';
import { AiUsageService } from '../../services/ai-usage.service';
import { AiUsageComponent } from './ai-usage.component';

const URL = 'https://gateway.example/api/v1/me/ai-usage';

function payload(overrides: { remaining?: number; allowance?: number; isSuspended?: boolean; byTask?: unknown[]; byWorkspace?: unknown[] } = {}) {
  const allowance = overrides.allowance ?? 1000;
  const remaining = overrides.remaining ?? 640;

  return {
    period: {
      unit: 'Credits',
      allowance,
      carriedOver: 0,
      consumed: allowance - remaining,
      reserved: 0,
      remaining,
      periodLength: 'Monthly',
      timeZoneId: 'Europe/London',
      startsAt: '2026-03-01T00:00:00+00:00',
      resetsAt: '2026-04-01T00:00:00+01:00',
    },
    isSuspended: overrides.isSuspended ?? false,
    byTask: overrides.byTask ?? [
      { taskType: 'RecipeConcepts', amount: 240, requests: 12 },
      { taskType: 'IngredientSubstitution', amount: 120, requests: 4 },
    ],
    byWorkspace: overrides.byWorkspace ?? [
      { workspaceId: '11111111-1111-1111-1111-111111111111', workspaceName: "Sam's Kitchen", amount: 240, requests: 12 },
      { workspaceId: '22222222-2222-2222-2222-222222222222', workspaceName: null, amount: 120, requests: 4 },
    ],
  };
}

describe('AiUsageComponent', () => {
  let fixture: ComponentFixture<AiUsageComponent>;
  let http: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [AiUsageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);

    const runtimeConfig = TestBed.inject(RuntimeConfigService);
    const loading = runtimeConfig.load();
    http.expectOne('/runtime-config.json').flush({ gatewayUrl: 'https://gateway.example' });
    await loading;
  });

  afterEach(() => http.verify());

  async function render(body: Record<string, unknown> = payload(), fail = false): Promise<HTMLElement> {
    fixture = TestBed.createComponent(AiUsageComponent);
    fixture.detectChanges();

    const request = http.expectOne(URL);
    if (fail) {
      request.error(new ProgressEvent('network'));
    } else {
      request.flush(body);
    }

    await TestBed.inject(AiUsageService).ensureLoaded();
    fixture.detectChanges();

    return fixture.nativeElement as HTMLElement;
  }

  it('shows what is left, when it comes back, and both breakdowns', async () => {
    const host = await render();

    expect(host.textContent).toContain('640 AI credits left');
    expect(host.textContent).toContain('April');
    expect(host.textContent).toContain('Recipe ideas');
    expect(host.textContent).toContain('Ingredient swaps');
    expect(host.textContent).toContain("Sam's Kitchen");
  });

  /** The lead is what a creator has, not how far through they are: its own figure, first (AU.6.4). */
  it('leads with an amount rather than a percentage', async () => {
    const host = await render();
    const first = host.querySelector('cp-stat-tile');

    expect(first?.textContent?.replace(/\s+/g, ' ').trim()).toContain('640 AI credits left');
    expect(first?.textContent).not.toContain('%');
    // Said once: the card below no longer repeats it as a headline.
    expect(host.querySelector('.headline')).toBeNull();
  });

  // ---- The figures (AU.6.4, APP-008) ----

  const tileTexts = (host: HTMLElement) =>
    Array.from(host.querySelectorAll('cp-stat-tile')).map((tile) => tile.textContent!.replace(/\s+/g, ' ').trim());

  it('reads the allowance as three figures, each the server’s own number', async () => {
    const host = await render({ ...payload(), period: { ...payload().period, allowance: 1000, consumed: 300, reserved: 60, remaining: 640 } });

    const tiles = tileTexts(host);
    expect(tiles.length).toBe(3);
    expect(tiles[0]).toContain('640 AI credits left');
    // `consumed` as sent, not the allowance less what is left: 60 are held for a request still running.
    expect(tiles[1]).toContain('300 AI credits used this month');
    expect(tiles[2]).toContain('1,000 AI credits in your allowance this month');
    // So the three not adding up is said, from the server's own figure, rather than left to look like an error.
    expect(tiles[0]).toContain('60 AI credits set aside for requests still running.');

    // The tiles come first, then the card that says the state in words.
    const grid = host.querySelector('ul[cpCardGrid]')!;
    const card = host.querySelector('cp-topic-card')!;
    expect(grid.compareDocumentPosition(card) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('says nothing is set aside when nothing is running', async () => {
    const host = await render();

    expect(tileTexts(host)[0]).not.toContain('set aside');
  });

  it('keeps the last figures when a check fails, and says beside them that they are the last ones', async () => {
    const host = await render();

    void TestBed.inject(AiUsageService).refresh();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(tileTexts(host).length).toBe(3);
    expect(tileTexts(host)[0]).toContain('640 AI credits left');
    expect(tileTexts(host)[0]).toContain('The last figures we had.');
  });

  it('says one credit in the singular', async () => {
    const host = await render({ ...payload(), period: { ...payload().period, allowance: 1000, consumed: 999, remaining: 1 } });

    expect(tileTexts(host)[0]).toContain('1 AI credit left');
    expect(tileTexts(host)[0]).not.toContain('credits left');
  });

  it('holds what is left at nothing when a period has run over, as the headline always has', async () => {
    const host = await render({ ...payload(), period: { ...payload().period, remaining: -20, consumed: 1020 } });

    expect(tileTexts(host)[0]).toContain('0 AI credits left');
  });

  it('draws no figures when the allowance could not be read at all', async () => {
    const host = await render(payload(), true);

    expect(host.querySelector('cp-stat-tile')).toBeNull();
  });

  it('draws the page arriving while the allowance is read, said once from the region that is always there', async () => {
    fixture = TestBed.createComponent(AiUsageComponent);
    fixture.detectChanges();
    const host = fixture.nativeElement as HTMLElement;

    expect(host.querySelector('[role="status"]')?.textContent).toContain('Loading your AI allowance.');
    const busy = host.querySelector('.loading[aria-busy="true"]')!;
    expect(busy.querySelectorAll('ul[cpCardGrid] cp-card cp-skeleton').length).toBe(3);
    expect(busy.textContent!.trim()).toBe('');
    expect(host.querySelector('cp-stat-tile')).toBeNull();

    http.expectOne(URL).flush(payload());
    await TestBed.inject(AiUsageService).ensureLoaded();
    fixture.detectChanges();
    expect(host.querySelector('.loading')).toBeNull();
  });

  /** The meter announces the same words the sighted reader sees. */
  it('announces the meter as an amount', async () => {
    const host = await render();
    const meter = host.querySelector('[role="progressbar"]');

    expect(meter?.getAttribute('aria-valuetext')).toBe('640 AI credits left of 1,000');
    expect(meter?.getAttribute('aria-valuenow')).toBe('36');
  });

  /**
   * A workspace the account has left keeps its spend and loses its name, so the parts still sum to the whole
   * (USAGE-008).
   */
  it('keeps the spend of a workspace it may not name', async () => {
    const host = await render();

    expect(host.textContent).toContain('A workspace you’ve left');
    expect(host.textContent).toContain('120 credits');
  });

  it('names a nearly spent allowance in words, not only in colour', async () => {
    const host = await render(payload({ remaining: 120 }));
    const pill = host.querySelector('cp-status-pill');

    expect(pill?.textContent).toContain('Nearly spent');
    expect(pill?.querySelector('.icon')?.getAttribute('aria-hidden')).toBe('true');
  });

  it('tells a suspended account something different from a spent one', async () => {
    const suspended = await render(payload({ isSuspended: true }));

    expect(suspended.querySelector('cp-status-pill')?.textContent).toContain('Switched off');
    expect(suspended.textContent).toContain('Get in touch');

    // Scoped to the explanation: a suspension must not be told it will come back, because it will not. The
    // page's standing intro mentions the idea in general, and that is not a promise to this creator.
    const explanation = suspended.querySelector('.explanation')?.textContent ?? '';
    expect(explanation).not.toContain('comes back');
    expect(explanation).not.toContain('April');
  });

  it('offers a retry when it could not load at all', async () => {
    const host = await render(payload(), true);

    expect(host.textContent).toContain('Couldn’t load');
    expect(host.querySelector('button')?.textContent).toContain('Try again');
  });

  /** "this month", from the period's own length — the field the server publishes so we need not say "period". */
  it('shows an empty breakdown as empty, naming the period in plain words', async () => {
    const host = await render(payload({ byTask: [], byWorkspace: [] }));

    expect(host.textContent).toContain('Nothing used yet this month.');
  });

  /**
   * A roll-over is said rather than folded into the total: without it, "640 left of 1,000" hides that 400 of
   * the 1,000 was last period's unspent balance.
   */
  it('says what was carried over from last time, and only when there was any', async () => {
    const carried = {
      ...payload(),
      period: { ...payload().period, carriedOver: 400 },
    };

    // Beside the allowance figure it is part of (AU.6.4).
    const withCarry = await render(carried);
    expect(tileTexts(withCarry)[2]).toContain('Includes 400 AI credits carried over from last time.');
    expect((await render(payload())).textContent).not.toContain('carried over');
  });

  /**
   * The meter is the complement of the headline. Drawing it from `consumed` alone would disagree with
   * "640 left" by whatever runs in flight are holding — which is exactly when this refreshes.
   */
  it('counts what is in flight as spoken for, so the meter matches the headline', async () => {
    const inFlight = {
      ...payload(),
      period: { ...payload().period, consumed: 300, reserved: 60, remaining: 640 },
    };

    const host = await render(inFlight);

    expect(host.querySelector('[role="progressbar"]')?.getAttribute('aria-valuenow')).toBe('36');
  });

  /**
   * EASE-004. The page talks about credits and dates; the words below belong to the plumbing behind it and
   * none of them should reach a creator.
   */
  for (const jargon of ['token', 'provider', 'deployment', 'reservation', 'quota']) {
    it(`never says '${jargon}'`, async () => {
      const host = await render();

      expect(host.textContent?.toLowerCase()).not.toContain(jargon);
    });
  }

  /** Never the enum member: a capability a creator has not named has no business appearing on screen. */
  it('translates capability names into the creator’s language', async () => {
    const host = await render();

    expect(host.textContent).not.toContain('RecipeConcepts');
    expect(host.textContent).not.toContain('IngredientSubstitution');
  });

  /**
   * The shell renders the route's title as the page's h1. A second one here would show the same words twice
   * and give the page two top-level headings.
   */
  it("has one h1, and it is the page head's", async () => {
    const host = await render();

    expect(host.querySelectorAll('h1').length).toBe(1);
    expect(host.querySelector('cp-page-head h1')).toBeTruthy();
  });

  it('says what the page is for in the head, and puts the allowance in a card named for it', async () => {
    const host = await render();

    expect(host.querySelector('cp-page-head .lede')?.textContent).toContain('What you have left for AI help');
    // A top-level page: nothing to go back to.
    expect(host.querySelector('cp-page-head a[cpBackLink]')).toBeNull();

    const card = host.querySelector('cp-topic-card') as HTMLElement;
    expect(card.querySelector('h2')?.textContent?.trim()).toBe('Your allowance');
    expect(card.querySelector('cp-status-pill')).toBeTruthy();
    expect(card.querySelector('cp-progress')).toBeTruthy();
  });

  /**
   * A token-denominated period must not put the raw token count on screen wearing a different noun. The
   * figure is what EASE-004 keeps off the page, not the word.
   */
  it('describes rather than counts an allowance in a unit it does not quote', async () => {
    const tokens = {
      ...payload(),
      period: { ...payload().period, unit: 'Tokens', remaining: 150000, allowance: 1000000 },
    };

    const host = await render(tokens);

    expect(host.querySelector('.headline')?.textContent?.trim()).toBe('Allowance remaining');
    expect(host.textContent).not.toContain('150,000');
    expect(host.textContent).not.toContain('1,000,000');
    // No figures to quote, so no tiles: the card's words are the whole answer (AU.6.4).
    expect(host.querySelector('cp-stat-tile')).toBeNull();
  });

  /** A repeated failure has to announce, or the live region's content is unchanged and nobody is told. */
  it('says something different when a retry fails too', async () => {
    const host = await render(payload(), true);

    expect(host.querySelector('[role="status"]')?.textContent).toContain('We couldn’t load');

    host.querySelector('button')?.click();
    http.expectOne(URL).error(new ProgressEvent('network'));
    await fixture.whenStable();
    fixture.detectChanges();

    expect(host.querySelector('[role="status"]')?.textContent).toContain('Still couldn’t load');
  });

  /** Rows a screen reader can navigate, rather than one run-on sentence. */
  it('renders each breakdown as a list', async () => {
    const host = await render();
    const lists = host.querySelectorAll('.breakdowns ul[role="list"]');

    expect(lists.length).toBe(2);
    expect(lists[0].querySelectorAll('li').length).toBe(2);
  });

  it('announces the whole state in one sentence', async () => {
    const host = await render(payload({ remaining: 120 }));
    const region = host.querySelector('[role="status"]');

    expect(region?.textContent).toContain('Nearly spent');
    expect(region?.textContent).toContain('120 AI credits left');
  });
});
