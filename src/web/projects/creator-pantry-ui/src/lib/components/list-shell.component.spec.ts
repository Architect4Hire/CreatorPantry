import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { CpListShellComponent, CpListShellState } from './list-shell.component';

@Component({
  standalone: true,
  imports: [CpListShellComponent],
  template: `
    <cp-list-shell [heading]="heading" [state]="state" [errorMessage]="errorMessage" [emptyMessage]="emptyMessage">
      @if (showActions) { <span cpListShellActions>Bulk actions</span> }
      @if (showEmpty) { <div cpListShellEmpty>Nothing here yet, custom copy.</div> }
      @if (showPagination) { <span cpListShellPagination>Page 1 of 3</span> }
      <table><tbody><tr><td>Row content</td></tr></tbody></table>
    </cp-list-shell>
  `,
})
class HostComponent {
  heading = 'Recipes';
  state: CpListShellState = 'ready';
  errorMessage = '';
  emptyMessage = 'No results.';
  showActions = false;
  showEmpty = false;
  showPagination = false;
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
