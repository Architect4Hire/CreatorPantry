import { TestBed } from '@angular/core/testing';

import { DEFAULT_VALUE_PROPS, LandingValuePropsComponent } from './landing-value-props.component';

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

  it('labels the three not-yet-built capabilities "In the works" by text, not tone alone', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const badges = el.querySelectorAll('cp-badge');

    const inTheWorks = Array.from(badges).filter((badge) => badge.textContent?.includes('In the works'));
    expect(inTheWorks.length).toBe(3);
  });

  it('marks only the recipes capability as generally available', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const firstBadge = el.querySelector('cp-card cp-badge');

    expect(firstBadge?.textContent).toContain('Recipes');
    expect(firstBadge?.getAttribute('data-tone')).toBe('success');
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
  });

  it('uses list semantics for the card grid', async () => {
    const fixture = await createFixture();
    const el: HTMLElement = fixture.nativeElement;
    const list = el.querySelector('ul.grid')!;

    expect(list.getAttribute('role')).toBe('list');
    expect(list.querySelectorAll(':scope > li').length).toBe(DEFAULT_VALUE_PROPS.length);
  });
});
