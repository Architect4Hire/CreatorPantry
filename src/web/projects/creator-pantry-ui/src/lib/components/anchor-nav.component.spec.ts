import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CpAnchorNavComponent, CpAnchorNavItem } from './anchor-nav.component';

describe('CpAnchorNavComponent', () => {
  const items: CpAnchorNavItem[] = [
    { targetId: 'crust', label: 'For the crust', detail: '4 lines' },
    { targetId: 'filling', label: 'For the filling', detail: '1 line' },
    { targetId: 'glaze', label: 'For the glaze' },
  ];

  async function createFixture(): Promise<ComponentFixture<CpAnchorNavComponent>> {
    await TestBed.configureTestingModule({ imports: [CpAnchorNavComponent] }).compileComponents();
    const fixture = TestBed.createComponent(CpAnchorNavComponent);
    fixture.componentRef.setInput('items', items);
    fixture.componentRef.setInput('ariaLabel', 'Ingredient groups');
    fixture.detectChanges();
    return fixture;
  }

  const host = (fixture: ComponentFixture<CpAnchorNavComponent>) => fixture.nativeElement as HTMLElement;
  const links = (fixture: ComponentFixture<CpAnchorNavComponent>) =>
    Array.from(host(fixture).querySelectorAll<HTMLAnchorElement>('a'));

  it('renders each item as an in-page link, in order, inside a named landmark', async () => {
    const fixture = await createFixture();

    const nav = host(fixture).querySelector('nav')!;
    expect(nav.getAttribute('aria-label')).toBe('Ingredient groups');
    expect(links(fixture).map((link) => link.getAttribute('href'))).toEqual(['#crust', '#filling', '#glaze']);
    expect(links(fixture).map((link) => link.querySelector('.label')?.textContent?.trim())).toEqual([
      'For the crust',
      'For the filling',
      'For the glaze',
    ]);
  });

  it('shows a detail line only for the items that have one', async () => {
    const fixture = await createFixture();

    expect(links(fixture).map((link) => link.querySelector('.detail')?.textContent?.trim() ?? null)).toEqual([
      '4 lines',
      '1 line',
      null,
    ]);
  });

  it('is ordinary navigation, not a tablist: every item tabbable, nothing with a role', async () => {
    const fixture = await createFixture();

    // A roving tabindex would make all but one item unreachable by Tab, and a tablist role would promise that
    // these are alternatives with only one of them present. Both would be wrong here.
    expect(links(fixture).some((link) => link.hasAttribute('tabindex'))).toBeFalse();
    expect(host(fixture).querySelector('[role="tablist"]')).toBeNull();
    expect(host(fixture).querySelector('[role="tab"]')).toBeNull();
  });

  it('marks only the active destination with aria-current', async () => {
    const fixture = await createFixture();
    expect(links(fixture).filter((link) => link.hasAttribute('aria-current'))).toEqual([]);

    fixture.componentRef.setInput('activeTargetId', 'filling');
    fixture.detectChanges();

    expect(links(fixture).map((link) => link.getAttribute('aria-current'))).toEqual([null, 'true', null]);
  });

  it('emits the activated item and does not let the fragment reach the URL', async () => {
    const fixture = await createFixture();
    const activated: CpAnchorNavItem[] = [];
    fixture.componentInstance.activated.subscribe((item) => activated.push(item));

    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    links(fixture)[1].dispatchEvent(event);
    fixture.detectChanges();

    expect(activated).toEqual([items[1]]);
    // preventDefault, so a single-page app's router never sees the fragment as a navigation.
    expect(event.defaultPrevented).toBeTrue();
  });

  it('renders nothing at all when there is nothing to navigate', async () => {
    const fixture = await createFixture();
    fixture.componentRef.setInput('items', []);
    fixture.detectChanges();

    // No empty shell, and no bare landmark for a screen reader to announce.
    expect(host(fixture).querySelector('nav')).toBeNull();
    expect(host(fixture).textContent?.trim()).toBe('');
  });

  it('leaves scrolling and focus to the consumer', async () => {
    const fixture = await createFixture();
    const scrollSpy = spyOn(Element.prototype, 'scrollIntoView');
    const before = document.activeElement;

    links(fixture)[0].dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
    fixture.detectChanges();

    // The consumer names what to bring into view and which of its own controls deserves focus; this only says
    // which destination was asked for.
    expect(scrollSpy).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(before);
  });

  it('stacks its items when a consumer asks for a column', async () => {
    const fixture = await createFixture();
    const list = host(fixture).querySelector('ul')!;
    expect(getComputedStyle(list).flexDirection).toBe('row');

    // Set from outside, typically from inside the consumer's own container query — the seam that keeps the
    // gutter decision with whoever knows how much room there is.
    host(fixture).style.setProperty('--cp-anchor-nav-direction', 'column');
    fixture.detectChanges();

    expect(getComputedStyle(list).flexDirection).toBe('column');
  });

  // A tapped link on a touch screen stays :hover until the next tap elsewhere, which on this nav read as a second
  // current item once the page had scrolled on. Asserted on the stylesheet itself, because a test cannot hover.
  it('applies its hover style only where a pointer can hover', async () => {
    await createFixture();

    const hoverRules: { rule: CSSStyleRule; inHoverMedia: boolean }[] = [];
    const walk = (rules: CSSRuleList, inHoverMedia: boolean) => {
      for (const rule of Array.from(rules)) {
        if (rule instanceof CSSMediaRule)
          walk(rule.cssRules, inHoverMedia || /hover:\s*hover/.test(rule.conditionText));
        else if (rule instanceof CSSStyleRule && /^a\[_ngcontent[^\]]*\]:hover$/.test(rule.selectorText))
          hoverRules.push({ rule, inHoverMedia });
      }
    };
    for (const sheet of Array.from(document.styleSheets)) {
      try {
        walk(sheet.cssRules, false);
      } catch {
        // A cross-origin sheet cannot be read, and is not this component's.
      }
    }

    expect(hoverRules.length).withContext('the nav has a hover rule').toBeGreaterThan(0);
    expect(hoverRules.filter((entry) => !entry.inHoverMedia).map((entry) => entry.rule.selectorText)).toEqual([]);
  });
});
