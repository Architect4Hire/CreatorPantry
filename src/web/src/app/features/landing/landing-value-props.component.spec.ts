import { TestBed } from '@angular/core/testing';

import { COMING_TO_EVERY_ACCOUNT, DEFAULT_VALUE_PROPS, LandingValuePropsComponent } from './landing-value-props.component';

describe('LandingValuePropsComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({
      imports: [LandingValuePropsComponent],
    }).compileComponents();

    const fixture = TestBed.createComponent(LandingValuePropsComponent);
    fixture.detectChanges();
    return fixture;
  }

  it('renders one card per default value prop, in order', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const headings = el.querySelectorAll('cp-card h3');

    expect(headings.length).toBe(DEFAULT_VALUE_PROPS.length);
    headings.forEach((heading, i) => expect(heading.textContent).toContain(DEFAULT_VALUE_PROPS[i].heading));
  });

  it('renders each card’s badge text and body copy', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const cards = el.querySelectorAll('cp-card');

    DEFAULT_VALUE_PROPS.forEach((item, i) => {
      expect(cards[i].querySelector('cp-badge')?.textContent).toContain(item.badgeLabel);
      expect(cards[i].querySelector('p')?.textContent).toContain(item.body);
    });
  });

  // LP.1 finding 4: every "In the works" label was stale.
  it('labels nothing "In the works"', async () => {
    const fixture = await createFixture();
    expect((fixture.nativeElement as HTMLElement).textContent).not.toMatch(/in the works|active development|near-term roadmap/i);
  });

  it('says publishing is not yet for every account in the badge text, not the tone alone', async () => {
    const fixture = await createFixture();
    const cards = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('cp-card'));
    const publishing = cards.find((card) => card.querySelector('h3')?.textContent?.includes('Schedule to your social channels'));
    const badge = publishing?.querySelector('cp-badge');

    expect(badge?.textContent?.trim()).toBe(COMING_TO_EVERY_ACCOUNT);
    expect(badge?.getAttribute('data-tone')).toBe('neutral');
  });

  it('gives every other card its category name, never the not-yet label, and a tone that is not neutral', async () => {
    const fixture = await createFixture();
    const badges = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('cp-card cp-badge'));
    const shipped = badges.filter((badge) => badge.textContent?.trim() !== COMING_TO_EVERY_ACCOUNT);

    expect(shipped.length).toBe(DEFAULT_VALUE_PROPS.length - 1);
    shipped.forEach((badge) => expect(badge.getAttribute('data-tone')).not.toBe('neutral'));
  });

  it('keeps the AI-as-proposal and explicit-confirmation wording', async () => {
    const fixture = await createFixture();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).toContain('it becomes a recipe only when you say so');
    expect(text).toContain('each with an explicit confirmation');
  });

  // LP.1 finding 5 and LP.2: claims the product does not back.
  it('claims no recipe import, self-connected channels, AI scaling, named versions or equipment', async () => {
    const fixture = await createFixture();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).not.toMatch(/import/i);
    expect(text).not.toMatch(/connect (the )?(your )?channels?|connected provider/i);
    expect(text).not.toMatch(/scal(e|ing)/i);
    expect(text).not.toMatch(/named version|equipment/i);
  });

  it('links no card to the tour until the tour exists', async () => {
    const fixture = await createFixture();
    expect((fixture.nativeElement as HTMLElement).querySelector('a')).toBeNull();
  });

  it('links each card that has a tour panel to it once tour links are on, naming the card for a screen reader', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('tourLinks', true);
    fixture.detectChanges();

    const links = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('cp-card a'));
    const withPanels = DEFAULT_VALUE_PROPS.filter((item) => item.tourPanel);

    expect(links.length).toBe(withPanels.length);
    links.forEach((link, i) => {
      expect(link.getAttribute('href')).toBe(`#tour-${withPanels[i].tourPanel}`);
      expect(link.textContent).toContain('See it in the tour');
      expect(link.textContent).toContain(withPanels[i].heading);
    });
  });

  it('renders custom items supplied via input', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('items', [
      { badgeLabel: 'Custom', badgeTone: 'neutral', heading: 'Custom heading', body: 'Custom body' },
    ]);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('cp-card').length).toBe(1);
    expect(el.querySelector('cp-card h3')?.textContent).toContain('Custom heading');
  });

  it("labels the section by its own heading, giving it an accessible name", async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const section = el.querySelector('section')!;
    const heading = el.querySelector('h2')!;

    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
    expect(heading.id).toBeTruthy();
    expect(heading.textContent).toContain('What’s in CreatorPantry today');
  });

  it('lays the cards out with the library card grid, keeping list semantics', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const list = el.querySelector('ul[cpCardGrid]')!;

    expect(el.querySelector('ul.grid')).toBeNull();
    expect(list.getAttribute('role')).toBe('list');
    expect(list.querySelectorAll(':scope > li').length).toBe(DEFAULT_VALUE_PROPS.length);
  });
});
