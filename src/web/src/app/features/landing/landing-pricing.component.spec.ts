import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { CpCardGridComponent } from '@creator-pantry/ui';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { LandingPricingComponent, RECOMMENDED_LABEL } from './landing-pricing.component';

const creator = {
  key: 'creator',
  name: 'Creator',
  monthlyPrice: 19,
  annualMonthlyPrice: 15,
  annualPrice: 180,
  annualSaving: 48,
  credits: 6000,
  exampleMix: '50 recipe drafts, 100 social posts and 80 pictures',
  dailyImageCap: 25,
  // As the server publishes them since AP.4.5: the enum's spelling, ascending.
  qualityClasses: ['Draft', 'Standard', 'High', 'High2K'],
  recommended: true,
  trialDays: null,
  trialImageCap: null,
  // 6,000 credits at 50 a standard picture (AP.4.5); the server's figure, not worked out here.
  picturesAtDefaultQuality: 120,
  // One month made all together, fitting the credits; the server holds the sum, so nothing is checked here.
  exampleCounts: { recipeDrafts: 50, socialPosts: 100, pictures: 80 },
};

const rates = { textDraft: 10, longFormSection: 10 };
const pictureClasses = [
  { qualityClass: 'Draft' as const, perPicture: 25 },
  { qualityClass: 'Standard' as const, perPicture: 50 },
  { qualityClass: 'High' as const, perPicture: 87.5 },
];

const trial = { ...creator, key: 'trial', name: 'Trial', monthlyPrice: 0, annualMonthlyPrice: 0, annualSaving: 0, recommended: false, trialDays: 14, trialImageCap: 10, dailyImageCap: 5, qualityClasses: ['Draft', 'Standard'], picturesAtDefaultQuality: 10, exampleCounts: { recipeDrafts: 20, socialPosts: 20, pictures: 10 } };

describe('LandingPricingComponent', () => {
  let fixture: ComponentFixture<LandingPricingComponent>;
  let host: HTMLElement;

  async function create(catalogue: PlanCatalogue): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LandingPricingComponent],
      providers: [provideRouter([]), { provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingPricingComponent);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  const published: PlanCatalogue = { currency: 'USD', plans: [trial, creator], packs: [], rates, pictureClasses, defaultPictureClass: 'Standard' as const };
  const text = () => host.textContent ?? '';
  const prices = () => Array.from(host.querySelectorAll('.amount')).map((el) => el.textContent?.trim());
  const secondLines = () => Array.from(host.querySelectorAll('.second-line')).map((el) => el.textContent?.trim());

  /**
   * Picks a billing period and lets the count between the two figures finish. The price starts from what was
   * on screen, so reading it in the same tick would read the figure being left rather than the one chosen.
   */
  async function choose(period: 'monthly' | 'annual'): Promise<void> {
    host.querySelector<HTMLInputElement>(`input[value="${period}"]`)!.click();
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 500));
    fixture.detectChanges();
  }

  afterEach(() => {
    fixture?.destroy();
    // This section is wide — a table of plans in a sideways scroller — and clicking inside it scrolls the
    // runner's own page. Every spec after it reads element positions from the same window, so it is put back.
    window.scrollTo(0, 0);
  });

  /**
   * An unpublished catalogue and an unreachable gateway look the same here, which is the point: a visitor is
   * never shown a pricing section with nothing in it, or an error about one.
   */
  it('renders nothing while the catalogue publishes no plans', async () => {
    await create(PlanCatalogueService.EMPTY);

    expect(host.querySelector('section')).toBeNull();
  });

  it('shows a card per plan, with the catalogue’s own figures', async () => {
    await create(published);

    expect(Array.from(host.querySelectorAll('h3')).map((el) => el.textContent?.trim())).toEqual(['Trial', 'Creator']);
    expect(text()).toContain('6,000 AI credits a month');
    // The trial's credits come once (B-48), and its card says so rather than "a month".
    expect(text()).toContain('6,000 AI credits for the trial');
    expect(text()).toContain('50 recipes drafted');
    expect(text()).toContain('Up to 25 pictures a day');
  });

  /**
   * The pitch (owner, 2026-10-28): each card leads with what its credits make — recipes drafted and pictures,
   * as two figures — under a line that says they come together, so the pair is read as "and", not "or".
   */
  it('leads each card with the recipes and pictures its credits make together', async () => {
    await create(published);
    const pitches = Array.from(host.querySelectorAll('.pitch'));

    expect(pitches.length).toBe(2);
    const [trialPitch, creatorPitch] = pitches.map((pitch) => pitch.textContent?.replace(/\s+/g, ' ') ?? '');

    expect(creatorPitch).toContain('Every month, all together');
    expect(creatorPitch).toContain('50 recipes drafted');
    expect(creatorPitch).toContain('80 standard pictures');
    expect(creatorPitch).toContain('100 social posts');

    // A trial's credits come once, so its pitch is about the trial, never "every month".
    expect(trialPitch).toContain('In the trial, all together');
    expect(trialPitch).not.toContain('Every month');

    // Three rows, each one phrase: the count, then what it counts.
    expect(pitches[1].querySelectorAll('.pitch-rows > li').length).toBe(3);

    // The sentence the counts replace is not said twice on the same card.
    expect(text()).not.toContain('About 50 recipe drafts');
  });

  /** A catalogue with no counts keeps the card it had: the mix as a sentence, and no headline figures. */
  it('says the mix as a sentence when the catalogue states no counts', async () => {
    await create({ ...published, plans: [{ ...creator, exampleCounts: null }] });

    expect(host.querySelector('.pitch')).toBeNull();
    expect(text()).toContain('About 50 recipe drafts, 100 social posts and 80 pictures');
    expect(text()).toContain('About 120 standard pictures a month');
  });

  /**
   * A plan now differs by which picture qualities it includes (AP.4.3), so the page says so: the full list on
   * the card, where it is read, and the best the plan reaches in the table, where plans are compared. Replaces
   * the spec that asserted the page claimed no quality at all, which was right only while a creator could not
   * choose one.
   */
  it('states the picture qualities each plan includes, and the best in the comparison', async () => {
    await create(published);

    // The card lists them in the catalogue's ascending order, in the page's own words.
    expect(text()).toContain('Picture quality: Draft, Standard');
    expect(text()).toContain('Picture quality: Draft, Standard, High, High 2K');

    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    const row = Array.from(host.querySelectorAll('tbody th, tbody td')).map((el) => el.textContent?.trim());
    expect(host.textContent).toContain('Best picture quality');
    expect(row).toContain('Standard');
    expect(row).toContain('High 2K');
  });

  /**
   * <strong>What the credits buy in pictures</strong> (AP.4.5) — the figure that decides the plan, since
   * pictures are what credits mostly go on. The server's number, formatted and never divided here.
   */
  it('states how many pictures each plan buys, on the card and in the comparison', async () => {
    await create(published);

    // Beside the headline, the most pictures alone is the alternative to it, and said as one.
    expect(text()).toContain('Or up to 120 standard pictures a month, if pictures are all you make');

    // The trial's headline already makes as many as its cap allows, so offering "up to 10" as well would offer
    // nothing; the line is left off.
    expect(text()).not.toContain('Or up to 10');

    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    const cells = Array.from(host.querySelectorAll('tbody th, tbody td')).map((el) => el.textContent?.trim());
    expect(host.textContent).toContain('Most pictures a month');
    expect(cells).toContain('120');
    // "In all" on a trial: its credits are granted once for the whole trial, so "a month" would promise twelve
    // times what it gives.
    expect(cells).toContain('10 in all');
  });

  /** A catalogue that publishes no figure prints no line and a dash in the table, rather than a nought. */
  it('prints no capacity line when the catalogue publishes no figure', async () => {
    await create({
      ...published,
      plans: [{ ...creator, picturesAtDefaultQuality: null }],
      defaultPictureClass: null,
    });

    expect(text()).not.toContain('standard pictures');

    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    expect(Array.from(host.querySelectorAll('tbody td')).map((el) => el.textContent?.trim())).toContain('—');
  });

  /** A plan that states no quality prints no line, rather than an empty one. */
  it('prints no quality line for a plan that includes none', async () => {
    await create({ ...published, plans: [{ ...creator, qualityClasses: [] }] });

    expect(text()).not.toContain('Picture quality');
  });

  /** A class this bundle cannot word is left out rather than shown as a configuration key. */
  it('leaves out a quality it has no word for', async () => {
    await create({ ...published, plans: [{ ...creator, qualityClasses: ['Standard', 'Ultra8K'] }] });

    expect(text()).toContain('Picture quality: Standard');
    expect(text()).not.toContain('Ultra8K');
  });

  /**
   * The cards sit side by side as a grid. `cpCardGrid` is an attribute directive, so leaving it out of the
   * component's imports is silent: the attribute stays on the `<ul>`, nothing matches it, and every card drops
   * to full width in a stack. That is exactly what shipped before this test existed.
   */
  it('lays the cards out as a card grid, not a stack', async () => {
    await create(published);
    const grid = fixture.debugElement.query(By.directive(CpCardGridComponent));

    expect(grid).not.toBeNull();
    expect(grid.nativeElement.tagName.toLowerCase()).toBe('ul');
  });

  it('marks the recommended plan in words, not by colour alone', async () => {
    await create(published);
    const recommended = host.querySelectorAll('ul[cpCardGrid] > li > cp-card')[1];

    expect(recommended.textContent).toContain(RECOMMENDED_LABEL);
    expect(recommended.classList).toContain('recommended');
  });

  /** The toggle is a real radio group, and the group is named by the question above it. */
  it('offers the two billing periods as a named radio group', async () => {
    await create(published);
    const group = host.querySelector('[role="radiogroup"]')!;
    const radios = Array.from(group.querySelectorAll<HTMLInputElement>('input[type="radio"]'));

    expect(group.getAttribute('aria-label')).toBe('How would you like to pay?');
    expect(host.querySelector('.period-label')?.textContent?.trim()).toBe('How would you like to pay?');
    expect(radios.map((radio) => radio.value)).toEqual(['monthly', 'annual']);
    expect(radios.find((radio) => radio.checked)?.value).toBe('annual');
  });

  it('shows the yearly price with its saving, and the monthly price without one', async () => {
    await create(published);

    expect(prices()).toEqual(['$0', '$15']);
    expect(secondLines()).toContain('Billed yearly at $180 — $48 saved');

    await choose('monthly');

    expect(prices()).toEqual(['$0', '$19']);
    // Paying monthly says nothing about a year: no yearly figure, and no saving to claim.
    expect(secondLines().some((line) => line?.includes('Billed yearly'))).toBeFalse();
  });

  it('says how long a trial runs rather than what it saves', async () => {
    await create(published);

    expect(text()).toContain('14 days, no card needed to look around');
  });

  /** The trial's gate is a cap on the whole trial (B-52); a reader weighing it needs that beside the daily one. */
  it('says how many pictures a trial makes in all, and says it on the trial alone', async () => {
    await create(published);
    const cards = Array.from(host.querySelectorAll('ul[cpCardGrid] > li > cp-card')).map((card) => card.textContent ?? '');

    expect(cards[0]).toContain('Up to 5 pictures a day, 10 in all');
    expect(cards[1]).toContain('Up to 25 pictures a day');
    expect(cards[1]).not.toContain('in all');
  });

  it('sends each plan’s call to action to sign-up carrying that plan', async () => {
    await create(published);
    const links = Array.from(host.querySelectorAll<HTMLAnchorElement>('cp-card a[href]'));

    expect(links.map((link) => link.getAttribute('href'))).toEqual(['/sign-up?plan=trial', '/sign-up?plan=creator']);
    // routerLink, not a bare href: a public page that reloads the whole app to reach sign-up is a defect.
    expect(links[1].textContent).toContain('Start with Creator');
  });

  it('keeps the comparison closed until it is asked for, and says so', async () => {
    await create(published);
    const toggle = host.querySelector<HTMLButtonElement>('.comparison button')!;
    const panel = host.querySelector<HTMLElement>('#landing-pricing-comparison')!;

    expect(toggle.getAttribute('aria-expanded')).toBe('false');
    expect(toggle.getAttribute('aria-controls')).toBe('landing-pricing-comparison');
    expect(panel.hidden).toBeTrue();

    toggle.click();
    fixture.detectChanges();

    expect(toggle.getAttribute('aria-expanded')).toBe('true');
    expect(panel.hidden).toBeFalse();
  });

  /**
   * A plan to a row, so the table grows down the page rather than across it. The wide version, a column per
   * plan with a column of sentences in it, did not fit a phone.
   */
  it('compares the plans in a real table, one plan to a row', async () => {
    await create(published);
    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    const table = host.querySelector('table')!;
    const columnHeaders = Array.from(table.querySelectorAll('thead th')).map((th) => th.textContent?.trim());
    const rowHeaders = Array.from(table.querySelectorAll('tbody th')).map((th) => th.textContent?.trim());

    expect(table.querySelector('caption')).not.toBeNull();
    expect(columnHeaders).toEqual([
      'Plan',
      'Price',
      'Credits a month',
      // The card's headline, short (owner, 2026-10-28).
      'Recipes and pictures a month',
      // What the credits buy in pictures alone, above the daily cap (AP.4.5).
      'Most pictures a month',
      'Pictures a day',
      'Best picture quality',
      'Free trial',
    ]);
    expect(rowHeaders).toEqual(['Trial', 'Creator']);
    expect(Array.from(table.querySelectorAll('tbody th')).every((th) => th.getAttribute('scope') === 'row')).toBeTrue();
    expect(Array.from(table.querySelectorAll('thead th')).every((th) => th.getAttribute('scope') === 'col')).toBeTrue();
  });

  /** A column of sentences is what made the table too wide; the card is where that sentence belongs. */
  it('keeps the long "what that buys" line off the table and on the card', async () => {
    await create(published);
    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    const table = host.querySelector('table')!;

    expect(table.textContent).not.toContain('recipes drafted');
    expect(table.textContent).not.toContain('social posts');
    expect(host.querySelector('.pitch')?.textContent).toContain('recipes drafted');
    // The short figures the table does carry for the same plan.
    const cells = Array.from(table.querySelectorAll('tbody td')).map((td) => td.textContent?.trim());
    expect(cells).toContain('25');
    expect(cells).toContain('50 recipes, 80 pictures');
    expect(cells).toContain('20 recipes, 10 pictures in all');
  });

  it('puts the trial’s cap on the whole trial beside its daily cap in the table', async () => {
    await create(published);
    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    const cells = Array.from(host.querySelectorAll('table tbody td')).map((td) => td.textContent?.trim());

    expect(cells).toContain('5 (10 in all)');
  });

  /**
   * The count between the two prices is cosmetic — the figure it lands on is the catalogue's either way — so
   * under reduced motion the new price is simply there, with no frame scheduled at all.
   */
  it('does not count the price under reduced motion', async () => {
    const real = window.matchMedia.bind(window);
    spyOn(window, 'matchMedia').and.callFake((query: string) =>
      query.includes('prefers-reduced-motion') ? ({ matches: true, media: query, addEventListener: () => undefined, removeEventListener: () => undefined } as unknown as MediaQueryList) : real(query),
    );
    await create(published);
    const frames = spyOn(window, 'requestAnimationFrame').and.callThrough();

    host.querySelector<HTMLInputElement>('input[value="monthly"]')!.click();
    fixture.detectChanges();

    expect(frames).not.toHaveBeenCalled();
    expect(prices()).toEqual(['$0', '$19']);
  });

  /**
   * A second toggle while the first is still counting abandons it. Two loops running at once drew toward
   * different targets, and whichever finished first cleared the other's figures mid-count.
   */
  it('lands on the catalogue’s figure when the period is changed mid-count', async () => {
    await create(published);
    const monthly = host.querySelector<HTMLInputElement>('input[value="monthly"]')!;
    const annual = host.querySelector<HTMLInputElement>('input[value="annual"]')!;

    monthly.click();
    fixture.detectChanges();
    annual.click();
    fixture.detectChanges();

    // Let every frame either run or find itself abandoned.
    await new Promise((resolve) => setTimeout(resolve, 500));
    fixture.detectChanges();

    expect(prices()).toEqual(['$0', '$15']);
  });

  it('carries no price of its own: every figure on screen came from the catalogue', async () => {
    await create({ currency: 'USD', plans: [{ ...creator, monthlyPrice: 123, annualMonthlyPrice: 99, annualPrice: 1188, annualSaving: 288 }], packs: [], rates, pictureClasses, defaultPictureClass: 'Standard' as const });

    expect(prices()).toEqual(['$99']);
    expect(secondLines()).toContain('Billed yearly at $1,188 — $288 saved');
  });
});
