import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';

import { PlanCatalogue } from '../../models/plan-catalogue.models';
import { PlanCatalogueService } from '../../services/plan-catalogue.service';
import { BEYOND_TOP_PLAN_CONTACT, LandingCreditsComponent, NOTHING_CHOSEN } from './landing-credits.component';

const rates = { textDraft: 10, longFormSection: 10, imageDraft: 25, imageStandard: 50, imageHigh: 90 };

const plan = (key: string, name: string, credits: number, trialDays: number | null = null) => ({
  key,
  name,
  monthlyPrice: 19,
  annualMonthlyPrice: 15,
  annualPrice: 180,
  annualSaving: 48,
  credits,
  exampleMix: '50 recipe drafts',
  dailyImageCap: 25,
  qualityClasses: ['draft', 'standard'],
  recommended: false,
  trialDays,
});

/** The trial first, as the catalogue publishes it cheapest-first, so the specs prove it is skipped. */
const published: PlanCatalogue = {
  currency: 'USD',
  plans: [plan('trial', 'Trial', 1000, 14), plan('creator', 'Creator', 6000), plan('pro', 'Pro', 18000), plan('studio', 'Studio', 40000)],
  packs: [],
  rates,
};

describe('LandingCreditsComponent', () => {
  let fixture: ComponentFixture<LandingCreditsComponent>;
  let host: HTMLElement;

  async function create(catalogue: PlanCatalogue): Promise<void> {
    await TestBed.configureTestingModule({
      imports: [LandingCreditsComponent],
      providers: [{ provide: PlanCatalogueService, useValue: { catalogue: () => of(catalogue) } }],
    }).compileComponents();
    fixture = TestBed.createComponent(LandingCreditsComponent);
    host = fixture.nativeElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  /** Moves a slider the way a creator does, and lets the figure settle wherever the count leaves it. */
  async function drag(id: string, value: number): Promise<void> {
    const slider = host.querySelector<HTMLInputElement>(`#${id}`)!;
    slider.value = String(value);
    slider.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 500));
    fixture.detectChanges();
  }

  /** What a reader sees: the live region also carries the settled figure, which only a listener hears. */
  const verdict = () => {
    const seen = host.querySelector('.verdict')!.cloneNode(true) as HTMLElement;
    seen.querySelector('.cp-sr-only')?.remove();
    return seen.textContent?.trim() ?? '';
  };

  /** What a listener hears, which is the same sentence with the figure said first. */
  const announced = () => host.querySelector('.verdict')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';
  const figure = () => host.querySelector('.figure')?.textContent?.trim() ?? '';

  afterEach(() => {
    fixture?.destroy();
    // Dragging a slider scrolls the runner's own page, and the specs after this one read element positions.
    window.scrollTo(0, 0);
  });

  it('renders nothing while the catalogue publishes no plans', async () => {
    await create(PlanCatalogueService.EMPTY);

    expect(host.querySelector('section')).toBeNull();
  });

  it('prices every kind of action from the catalogue, and says the figures are typical', async () => {
    await create(published);
    const lines = Array.from(host.querySelectorAll('.menu li')).map((li) => [
      li.querySelector('.item')?.textContent?.trim(),
      li.querySelector('.cost')?.textContent?.trim(),
    ]);

    expect(lines).toEqual([
      ['A text draft', '10 credits'],
      ['A long-form section', '10 credits'],
      // One kind of picture: there is no quality to choose, so the draft and high rates are never shown.
      ['A picture', '50 credits'],
    ]);
    expect(host.querySelector('.typical')?.textContent).toContain('Typical figures');
  });

  it('offers two native sliders a keyboard can move, each with its value in sight', async () => {
    await create(published);
    const sliders = Array.from(host.querySelectorAll<HTMLInputElement>('input[type="range"]'));

    expect(sliders.length).toBe(2);
    sliders.forEach((slider) => {
      expect(slider.min).not.toBe('');
      expect(slider.max).not.toBe('');
      expect(slider.step).not.toBe('');
      // Named by its own cp-field label, so the slider is announced as what it counts.
      expect(host.querySelector(`label[for="${slider.id}"]`)).not.toBeNull();
    });
    expect(Array.from(host.querySelectorAll('.slider-value')).map((el) => el.textContent?.trim())).toEqual([
      '60 pictures',
      '20 writing actions',
    ]);
  });

  it('estimates a month at the catalogue’s typical figures', async () => {
    await create(published);

    await drag('landing-credits-pictures', 100);
    await drag('landing-credits-writing', 40);

    // 100 pictures at 50, plus 40 writing actions at 10.
    expect(figure()).toBe('5,400');
    expect(host.querySelector('.unit')?.textContent).toContain('estimated');
  });

  it('names the smallest plan that covers the estimate, and what is left over', async () => {
    await create(published);

    await drag('landing-credits-pictures', 100);
    await drag('landing-credits-writing', 40);

    expect(verdict()).toBe('Your estimate fits Creator, with about 600 credits to spare.');
  });

  /** The trial's credits are a one-off, so it is never the answer to "which plan fits my month". */
  it('never answers with the trial, even when its credits would cover the estimate', async () => {
    await create(published);

    await drag('landing-credits-pictures', 10);
    await drag('landing-credits-writing', 0);

    expect(verdict()).toContain('Creator');
    expect(verdict()).not.toContain('Trial');
  });

  it('asks for a slider rather than naming a plan when nothing has been chosen', async () => {
    await create(published);

    await drag('landing-credits-pictures', 0);
    await drag('landing-credits-writing', 0);

    expect(figure()).toBe('0');
    expect(verdict()).toBe(NOTHING_CHOSEN);
  });

  it('says "none to spare" when the estimate is exactly a plan’s allowance', async () => {
    await create(published);

    await drag('landing-credits-pictures', 120);
    await drag('landing-credits-writing', 0);

    expect(figure()).toBe('6,000');
    expect(verdict()).toBe('Your estimate fits Creator exactly, with none to spare.');
  });

  it('sends a creator past the largest plan somewhere, rather than refusing them', async () => {
    await create(published);

    await drag('landing-credits-pictures', 300);
    await drag('landing-credits-writing', 200);

    // 300 pictures at 50 and 200 writing actions at 10: 17,000, which Pro's 18,000 covers and Creator's does not.
    expect(figure()).toBe('17,000');
    expect(verdict()).toContain('Pro');
  });

  it('offers a way out when what a creator makes is past every plan', async () => {
    await create({ ...published, plans: [plan('creator', 'Creator', 600)] });

    await drag('landing-credits-pictures', 100);

    expect(verdict()).toContain('more than Creator includes');
    expect(verdict()).toContain(BEYOND_TOP_PLAN_CONTACT);
    // The packs band (LP.8) renders them, so a visitor sent to add one has one to find.
    expect(verdict()).toContain('Add a pack');
  });

  /**
   * The counted figure changes every frame, so it is out of the live region: what is announced is the settled
   * estimate and the verdict, which change only when a slider does.
   */
  it('announces the settled estimate, not every frame of the count', async () => {
    await create(published);
    const live = host.querySelector('.verdict')!;

    expect(live.getAttribute('role')).toBe('status');
    expect(live.getAttribute('aria-live')).toBeNull();
    expect(host.querySelector('.figure')?.getAttribute('aria-hidden')).toBe('true');
    expect(announced()).toContain('credits a month, estimated.');
  });

  it('says what each slider’s number means, which a bare range does not', async () => {
    await create(published);

    expect(host.querySelector('#landing-credits-pictures')?.getAttribute('aria-valuetext')).toBe('60 pictures a month');
    expect(host.querySelector('#landing-credits-writing')?.getAttribute('aria-valuetext')).toBe('20 writing actions a month');
  });

  /**
   * Credits are not the only thing a plan has to carry. A plan whose month covers the estimate but whose daily
   * cap could not deliver the pictures does not fit, and saying it does would be the page's own mistake.
   */
  it('passes over a plan whose daily picture cap could not carry the month', async () => {
    const capped = { ...plan('creator', 'Creator', 40000), dailyImageCap: 1 };
    await create({ ...published, plans: [capped, plan('pro', 'Pro', 40000)] });

    await drag('landing-credits-pictures', 200);

    expect(verdict()).toContain('Pro');
    expect(verdict()).not.toContain('Creator');
  });

  it('renders nothing when the catalogue states plans but no rates', async () => {
    await create({ ...published, rates: null });

    expect(host.querySelector('section')).toBeNull();
  });
});
