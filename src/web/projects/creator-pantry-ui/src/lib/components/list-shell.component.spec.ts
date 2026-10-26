import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpListShellComponent, CpListShellLoadingLayout, CpListShellState } from './list-shell.component';

@Component({
  standalone: true,
  imports: [CpListShellComponent],
  template: `
    <cp-list-shell [heading]="heading" [state]="state" [loadingLayout]="loadingLayout" [errorMessage]="errorMessage" [emptyMessage]="emptyMessage">
      @if (showActions) { <span cpListShellActions>Bulk actions</span> }
      @if (showEmpty) { <div cpListShellEmpty>Nothing here yet, custom copy.</div> }
      @if (showLoading) { <div cpListShellLoading class="own-loading">Four columns arriving</div> }
      @if (showPagination) { <span cpListShellPagination>Page 1 of 3</span> }
      <table><tbody><tr><td>Row content</td></tr></tbody></table>
    </cp-list-shell>
  `,
})
class HostComponent {
  heading = 'Recipes';
  state: CpListShellState = 'ready';
  loadingLayout: CpListShellLoadingLayout = 'rows';
  errorMessage = '';
  emptyMessage = 'No results.';
  showActions = false;
  showEmpty = false;
  showLoading = false;
  showPagination = false;
}

/** The same shell driven by a signal, for a test that changes its state after the first render. */
@Component({
  standalone: true,
  imports: [CpListShellComponent],
  template: `<cp-list-shell heading="Recipes" [state]="state()"><p>Row</p></cp-list-shell>`,
})
class SignalHostComponent {
  readonly state = signal<CpListShellState>('ready');
}

describe('CpListShellComponent', () => {
  async function createFixture() {
    await TestBed.configureTestingModule({ imports: [HostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(HostComponent);
    return fixture;
  }

  it('renders the heading text and associates it via aria-labelledby on the section', async () => {
    const fixture = await createFixture();
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    const section = host.querySelector('section') as HTMLElement;
    const heading = host.querySelector('h2') as HTMLElement;

    expect(heading.textContent).toContain('Recipes');
    expect(heading.id).toBeTruthy();
    expect(section.getAttribute('aria-labelledby')).toBe(heading.id);
  });

  it('renders a projected actions slot next to the heading', async () => {
    const fixture = await createFixture();
    fixture.componentInstance.showActions = true;
    fixture.detectChanges();

    const host = fixture.nativeElement as HTMLElement;
    expect(host.textContent).toContain('Bulk actions');
  });

  describe('loading state', () => {
    it('renders an aria-live status region and suppresses the default-slot content', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'loading';
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const status = host.querySelector('[role="status"]') as HTMLElement;

      expect(status).toBeTruthy();
      expect(status.getAttribute('aria-live')).toBe('polite');
      expect(status.textContent).toContain('Loading');
      expect(host.querySelector('table')).toBeNull();

      const body = host.querySelector('.body') as HTMLElement;
      expect(body.getAttribute('aria-busy')).toBe('true');
    });

    // AU.2.3: the shape of what is coming, and the sentence for a screen reader only — said once, never seen.
    it('draws rows of skeleton by default, says "Loading…" exactly once, and shows no sentence', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'loading';
      fixture.detectChanges();
      document.body.appendChild(fixture.nativeElement);

      const host = fixture.nativeElement as HTMLElement;
      const skeleton = host.querySelector('cp-skeleton') as HTMLElement;
      expect(skeleton.getAttribute('data-shape')).toBe('line');
      expect(skeleton.getAttribute('aria-hidden')).toBe('true');
      expect(skeleton.querySelectorAll('.bone').length).toBe(5);

      const statuses = host.querySelectorAll('[role="status"]');
      expect(statuses.length).toBe(1);
      const status = statuses[0] as HTMLElement;
      expect(status.textContent?.trim()).toBe('Loading…');
      expect(status.getBoundingClientRect().width).toBeLessThanOrEqual(1);
      fixture.nativeElement.remove();
    });

    // A live region inserted with its text, or held inside an aria-busy subtree, may never be read; one that is
    // already in the page and changes its text is. So the region is there before loading, outside the busy body.
    it('keeps its status region in the page in every state, outside the busy body, and only changes its words', async () => {
      await TestBed.configureTestingModule({ imports: [SignalHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(SignalHostComponent);
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      const before = host.querySelector('[role="status"]') as HTMLElement;
      expect(before).toBeTruthy();
      expect(before.textContent?.trim()).toBe('');

      fixture.componentInstance.state.set('loading');
      fixture.detectChanges();
      const during = host.querySelector('[role="status"]') as HTMLElement;
      expect(during).toBe(before);
      expect(during.textContent?.trim()).toBe('Loading…');
      expect(during.closest('[aria-busy]')).toBeNull();
    });

    it('draws a grid of card skeletons when the list is a library of cards', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'loading';
      fixture.componentInstance.loadingLayout = 'cards';
      fixture.detectChanges();

      const skeleton = (fixture.nativeElement as HTMLElement).querySelector('cp-skeleton') as HTMLElement;
      expect(skeleton.getAttribute('data-shape')).toBe('card');
      expect(skeleton.querySelectorAll('.card').length).toBe(6);
    });

    // AU.6.1: a board is neither rows nor cards, so it draws its own columns — under the shell's one status.
    it('draws a projected loading layout in place of its own skeleton, still busy and still saying it once', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'loading';
      fixture.componentInstance.showLoading = true;
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const own = host.querySelector('.own-loading') as HTMLElement;
      expect(own).toBeTruthy();
      expect(own.closest('[aria-busy="true"]')).toBeTruthy();
      // Hidden by the shell itself, as its own skeleton is, whether or not the feature remembered to.
      expect(own.closest('[aria-hidden="true"]')).toBeTruthy();
      expect(host.querySelector('cp-skeleton')).toBeNull();
      expect(host.querySelectorAll('[role="status"]').length).toBe(1);
    });

    it('shows neither the projected loading layout nor its wrapper once the list is ready', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.showLoading = true;
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('.own-loading')).toBeNull();
      expect(host.querySelector('.loading-slot')).toBeNull();
      expect(host.querySelector('table')).toBeTruthy();
    });
  });

  // AU.0: a table header hidden for sight but not for a screen reader is absolutely positioned. In a scroll region
  // nobody positioned, it escapes the region and widens whatever is — the picture page was 550px at 375.
  it('clips what is visually hidden inside its wide content rather than letting it widen the page', async () => {
    @Component({
      standalone: true,
      imports: [CpListShellComponent],
      template: `<div class="frame" style="position: relative; width: 300px; overflow: auto">
        <cp-list-shell heading="Versions">
          <table style="width: 900px"><thead><tr><th>One</th><th><span class="cp-sr-only">Download</span></th></tr></thead></table>
        </cp-list-shell>
      </div>`,
    })
    class WideHostComponent {}

    await TestBed.configureTestingModule({ imports: [WideHostComponent] }).compileComponents();
    const fixture = TestBed.createComponent(WideHostComponent);
    fixture.detectChanges();
    document.body.appendChild(fixture.nativeElement);

    // Measured once the list has landed: its entrance moves it by `translate`, which also contains what is
    // positioned inside it, and would hide the escape this checks for.
    const scroll = (fixture.nativeElement as HTMLElement).querySelector('.scroll') as HTMLElement;
    scroll.getAnimations().forEach((animation) => animation.cancel());

    const frame = (fixture.nativeElement as HTMLElement).querySelector('.frame') as HTMLElement;
    expect(frame.scrollWidth).toBeLessThanOrEqual(300);
    fixture.nativeElement.remove();
  });

  // AU.0 found "No recipes yet." twice on an empty library: the empty state, and the feature's result summary in
  // the footer. The footer keeps saying it to a screen reader — it is how a filter that left nothing is heard —
  // and stops showing it.
  describe('the pagination footer', () => {
    it('is in the page but not shown while the list is empty', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'empty';
      fixture.componentInstance.showPagination = true;
      fixture.detectChanges();
      document.body.appendChild(fixture.nativeElement);

      const footer = (fixture.nativeElement as HTMLElement).querySelector('footer.pagination') as HTMLElement;
      expect(footer.textContent).toContain('Page 1 of 3');
      expect(footer.getBoundingClientRect().width).toBeLessThanOrEqual(1);
      fixture.nativeElement.remove();
    });

    // A pager that is fetching the next page keeps the creator's focus where it is, so loading does not hide it.
    for (const state of ['ready', 'loading', 'error'] as const) {
      it(`is shown while ${state}`, async () => {
        const fixture = await createFixture();
        fixture.componentInstance.state = state;
        fixture.componentInstance.showPagination = true;
        fixture.detectChanges();
        document.body.appendChild(fixture.nativeElement);

        const footer = (fixture.nativeElement as HTMLElement).querySelector('footer.pagination') as HTMLElement;
        expect(footer.getBoundingClientRect().width).toBeGreaterThan(1);
        fixture.nativeElement.remove();
      });
    }
  });

  describe('error state', () => {
    it('renders role=alert with the error message and suppresses the default-slot content', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'error';
      fixture.componentInstance.errorMessage = 'Could not load recipes.';
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const alert = host.querySelector('[role="alert"]') as HTMLElement;

      expect(alert).toBeTruthy();
      expect(alert.textContent).toContain('Could not load recipes.');
      expect(host.querySelector('table')).toBeNull();
    });
  });

  describe('empty state', () => {
    it('renders the emptyMessage text when no custom empty content is projected', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'empty';
      fixture.componentInstance.emptyMessage = 'Nothing to show.';
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      expect(host.querySelector('.empty-fallback')?.textContent).toContain('Nothing to show.');
      expect(host.textContent).not.toContain('Nothing here yet, custom copy.');
      expect(host.querySelector('table')).toBeNull();
    });

    it('renders projected [cpListShellEmpty] content instead of emptyMessage when present', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'empty';
      fixture.componentInstance.showEmpty = true;
      fixture.componentInstance.emptyMessage = 'Nothing to show.';
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      expect(host.textContent).toContain('Nothing here yet, custom copy.');
      expect(host.querySelector('.empty-fallback')).toBeNull();
    });
  });

  describe('ready state', () => {
    it('renders the default-slot content inside the keyboard-scrollable wrapper', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.state = 'ready';
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const scroll = host.querySelector('.scroll') as HTMLElement;

      expect(scroll).toBeTruthy();
      expect(scroll.getAttribute('tabindex')).toBe('0');
      expect(scroll.getAttribute('role')).toBe('region');
      expect(scroll.querySelector('table')).toBeTruthy();
      expect(scroll.textContent).toContain('Row content');
    });

    // AU.3.4: the list landing is an arrival; the same list reloaded by a filter or a save is not.
    it('lets its ready content arrive the first time it is ready, and not again', async () => {
      await TestBed.configureTestingModule({ imports: [SignalHostComponent] }).compileComponents();
      const fixture = TestBed.createComponent(SignalHostComponent);
      fixture.componentInstance.state.set('loading');
      fixture.detectChanges();

      fixture.componentInstance.state.set('ready');
      fixture.detectChanges();
      const host = fixture.nativeElement as HTMLElement;
      expect((host.querySelector('.scroll') as HTMLElement).hasAttribute('data-cp-enter')).toBeTrue();

      // The shell remembers after the render that showed it, so wait for that render to settle.
      await fixture.whenStable();
      fixture.componentInstance.state.set('loading');
      fixture.detectChanges();
      fixture.componentInstance.state.set('ready');
      fixture.detectChanges();

      const again = host.querySelector('.scroll') as HTMLElement;
      expect(again.hasAttribute('data-cp-enter')).toBeFalse();
      expect(again.style.animationDelay).toBe('');
    });

    it('renders the projected pagination slot in the footer', async () => {
      const fixture = await createFixture();
      fixture.componentInstance.showPagination = true;
      fixture.detectChanges();

      const host = fixture.nativeElement as HTMLElement;
      const footer = host.querySelector('footer') as HTMLElement;
      expect(footer.textContent).toContain('Page 1 of 3');
    });
  });
});
