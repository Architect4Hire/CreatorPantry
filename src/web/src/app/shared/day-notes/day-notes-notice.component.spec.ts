import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { WeeklyTheme } from '../../models/weekly-theme.models';
import { WeeklyThemeWeekOutcome, WeeklyThemesService } from '../../services/weekly-themes.service';
import { DayNotesNoticeComponent } from './day-notes-notice.component';

function theme(overrides: Partial<WeeklyTheme> = {}): WeeklyTheme {
  return {
    day: 'Monday',
    key: 'meat-free-monday',
    displayName: 'Meat-free Monday',
    description: null,
    standingNotes: 'Always under 30 minutes.\nNever tofu.',
    retiredAt: null,
    revision: 2,
    ...overrides,
  };
}

class StubThemes {
  outcome: WeeklyThemeWeekOutcome = { status: 'found', themes: [theme()] };
  readonly reads: string[] = [];

  getWeek(slug: string): Promise<WeeklyThemeWeekOutcome> {
    this.reads.push(slug);
    return Promise.resolve(this.outcome);
  }
}

@Component({
  imports: [DayNotesNoticeComponent],
  template: `<cp-day-notes-notice
    [workspaceSlug]="slug()"
    [themeKey]="key()"
    [usesAlreadyMade]="alreadyMade()"
    [(apply)]="apply"
  />`,
})
class HostComponent {
  readonly slug = signal('sams-kitchen');
  readonly key = signal<string | null>('meat-free-monday');
  readonly apply = signal(true);
  readonly alreadyMade = signal(false);
}

describe('DayNotesNoticeComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let el: HTMLElement;
  let themes: StubThemes;

  async function settle(): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  async function create(): Promise<void> {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    el = fixture.nativeElement;
    await settle();
  }

  function notice(): HTMLElement | null {
    return el.querySelector('cp-notice');
  }

  function checkbox(): HTMLInputElement {
    return el.querySelector('input[type="checkbox"]') as HTMLInputElement;
  }

  beforeEach(() => {
    themes = new StubThemes();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), { provide: WeeklyThemesService, useValue: themes }],
    });
  });

  it('says the day’s notes are in effect, names the theme, and lets the creator read them', async () => {
    await create();

    expect(notice()?.textContent).toContain('Meat-free Monday');
    expect(notice()?.textContent).toContain('standing notes are in effect');
    expect(el.querySelector('.notes')?.textContent).toBe('Always under 30 minutes.\nNever tofu.');
    expect(el.querySelector('details a')?.getAttribute('href')).toBe('/sams-kitchen/settings/themed-days');

    // On until the creator says otherwise, and labelled for what it does.
    expect(checkbox().checked).toBeTrue();
    expect(el.querySelector('label')?.textContent).toContain('Use these notes for this run');
  });

  it('hands the creator’s choice to its host, and says the notes are off for this run', async () => {
    await create();

    checkbox().click();
    await settle();

    expect(host.apply()).toBeFalse();
    expect(notice()?.textContent).toContain('switched off for this run');
    // Still readable, and still one press to turn back on.
    expect(el.querySelector('.notes')).not.toBeNull();

    checkbox().click();
    await settle();

    expect(host.apply()).toBeTrue();
    expect(notice()?.textContent).toContain('are in effect');
  });

  it('says the change aloud, and tells its host whether it is showing', async () => {
    await create();
    const host = el.querySelector('cp-day-notes-notice') as HTMLElement;

    // The sentence that changes with the switch is a polite live region; the checkbox says its own state.
    expect(el.querySelector('.lead')?.getAttribute('aria-live')).toBe('polite');
    expect(host.hasAttribute('data-shown')).toBeTrue();

    themes.outcome = { status: 'found', themes: [] };
    await create();

    expect((el.querySelector('cp-day-notes-notice') as HTMLElement).hasAttribute('data-shown')).toBeFalse();
  });

  it('says when what was already made for the day is used too, and that the switch does not change that', async () => {
    await create();
    expect(el.querySelector('.also')).toBeNull();

    host.alreadyMade.set(true);
    await settle();

    expect(el.querySelector('.also')?.textContent).toContain('already made for this day');
    expect(el.querySelector('.also')?.textContent).toContain('Switching');
  });

  it('renders nothing when the work has no theme, and reads nothing to find that out', async () => {
    themes.outcome = { status: 'found', themes: [theme()] };
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.key.set(null);
    el = fixture.nativeElement;
    await settle();

    expect(notice()).toBeNull();
    expect(themes.reads.length).toBe(0);
  });

  it('renders nothing for a theme with no notes, a theme that is gone, or a week that could not be read', async () => {
    themes.outcome = { status: 'found', themes: [theme({ standingNotes: '   ' })] };
    await create();
    expect(notice()).toBeNull();

    themes.outcome = { status: 'found', themes: [] };
    await create();
    expect(notice()).toBeNull();

    themes.outcome = { status: 'unavailable' };
    await create();
    expect(notice()).toBeNull();
  });

  it('still announces a retired theme’s notes: the work was made for it, and they are still followed', async () => {
    themes.outcome = { status: 'found', themes: [theme({ retiredAt: '2026-10-01T08:00:00+00:00' })] };

    await create();

    expect(notice()?.textContent).toContain('standing notes are in effect');
  });

  it('reads the week again when the work’s theme changes, and not otherwise', async () => {
    themes.outcome = {
      status: 'found',
      themes: [theme(), theme({ day: 'Friday', key: 'fakeaway-friday', displayName: 'Fakeaway Friday', standingNotes: 'Nothing deep-fried.' })],
    };
    await create();
    expect(themes.reads.length).toBe(1);

    host.apply.set(false);
    await settle();
    expect(themes.reads.length).toBe(1);

    host.key.set('fakeaway-friday');
    await settle();

    expect(themes.reads.length).toBe(2);
    expect(notice()?.textContent).toContain('Fakeaway Friday');
    expect(el.querySelector('.notes')?.textContent).toBe('Nothing deep-fried.');
  });
});
