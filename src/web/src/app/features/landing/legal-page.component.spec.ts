import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';

import { LegalPageComponent } from './legal-page.component';
import { LegalPageKey } from './legal-links';

describe('LegalPageComponent', () => {
  async function createFixture(legal: LegalPageKey) {
    await TestBed.configureTestingModule({
      imports: [LegalPageComponent],
      providers: [provideRouter([]), { provide: ActivatedRoute, useValue: { snapshot: { data: { legal } } } }],
    }).compileComponents();

    const fixture = TestBed.createComponent(LegalPageComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture;
  }

  it('titles the page with the document’s own heading, as its one h1', async () => {
    const fixture = await createFixture('privacy');
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelectorAll('h1').length).toBe(1);
    expect(el.querySelector('h1')?.textContent?.trim()).toBe('CreatorPantry Privacy Policy');
    expect(el.querySelector('main h2#who-we-are')).not.toBeNull();
  });

  it('draws tables as tables, each in a named region a keyboard can scroll', async () => {
    const fixture = await createFixture('privacy');
    const regions = Array.from(
      (fixture.nativeElement as HTMLElement).querySelectorAll<HTMLElement>('.table[role="region"]'),
    );

    expect(regions.length).toBe(2);
    expect(regions.map((region) => region.getAttribute('aria-label'))).toEqual([
      'Cookies and storage in your browser',
      'How long we keep it',
    ]);
    regions.forEach((region) => {
      expect(region.tabIndex).toBe(0);
      expect(region.querySelector('thead th[scope="col"]')).not.toBeNull();
    });
  });

  it('links a sibling document by its route and an email address by mailto', async () => {
    const fixture = await createFixture('terms');
    const main: HTMLElement = fixture.nativeElement.querySelector('main');

    expect(main.querySelector('a[href="/acceptable-use"]')?.textContent).toBe('Acceptable Use Policy');
    expect(main.querySelector('a[href="/privacy"]')?.textContent).toBe('Privacy Policy');
    expect(main.querySelector('a[href="mailto:robert@creatorpantry.com"]')).not.toBeNull();
  });

  it('leaves the reviewers’ notes off the page and keeps the owner’s placeholders on it', async () => {
    const fixture = await createFixture('terms');
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';

    expect(text).not.toContain('MOCK-UP');
    expect(text).not.toContain('product fact');
    expect(text).not.toContain('Billing is not live yet');
    expect(text).toContain('[LEGAL ENTITY NAME]');
  });

  it('links home from its logo and ends in the landing footer, outside main', async () => {
    const fixture = await createFixture('acceptable-use');
    const el: HTMLElement = fixture.nativeElement;

    expect(el.querySelector('main a.home')?.getAttribute('href')).toBe('/');
    expect(el.querySelector('cp-landing-footer')?.parentElement?.tagName.toLowerCase()).not.toBe('main');
  });

  it('puts the reader’s place at its heading when it opens', async () => {
    const fixture = await createFixture('acceptable-use');

    expect(document.activeElement).toBe(fixture.nativeElement.querySelector('h1'));
  });
});
