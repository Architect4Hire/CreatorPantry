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
  qualityClasses: ['draft', 'standard', 'high'],
  recommended: true,
  trialDays: null,
};

const rates = { textDraft: 10, longFormSection: 10, imageDraft: 25, imageStandard: 50, imageHigh: 90 };

const trial = { ...creator, key: 'trial', name: 'Trial', monthlyPrice: 0, annualMonthlyPrice: 0, annualSaving: 0, recommended: false, trialDays: 14 };

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

  const published: PlanCatalogue = { currency: 'USD', plans: [trial, creator], packs: [], rates };
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
    expect(text()).toContain('About 50 recipe drafts, 100 social posts and 80 pictures');
    expect(text()).toContain('Up to 25 pictures a day');
  });

  /**
   * The catalogue carries quality classes for AP.3.1, but a picture has no quality to choose today, so the
   * page must not offer one — on a card or in the table.
   */
  it('claims no picture quality the product cannot make', async () => {
    await create(published);
    host.querySelector<HTMLButtonElement>('.comparison button')!.click();
    fixture.detectChanges();

    expect(text().toLowerCase()).not.toContain('quality');
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
    const recommended = host.querySelectorAll('cp-card')[1];

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
    expect(columnHeaders).toEqual(['Plan', 'Price', 'Credits a month', 'Pictures a day', 'Free trial']);
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

    expect(table.textContent).not.toContain('50 recipe drafts');
    expect(host.querySelector('.includes')?.textContent).toContain('50 recipe drafts');
    // The short figure the table does carry for the same plan.
    expect(Array.from(table.querySelectorAll('tbody td')).map((td) => td.textContent?.trim())).toContain('25');
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
    await create({ currency: 'USD', plans: [{ ...creator, monthlyPrice: 123, annualMonthlyPrice: 99, annualPrice: 1188, annualSaving: 288 }], packs: [], rates });

    expect(prices()).toEqual(['$99']);
    expect(secondLines()).toContain('Billed yearly at $1,188 — $288 saved');
  });
});
