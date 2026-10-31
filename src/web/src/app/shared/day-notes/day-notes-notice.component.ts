import {
  ChangeDetectionStrategy,
  Component,
  Injector,
  computed,
  effect,
  inject,
  input,
  model,
  signal,
  untracked,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpCheckboxComponent, CpNoticeComponent } from '@creator-pantry/ui';

import { WeeklyTheme } from '../../models/weekly-theme.models';
import { WeeklyThemesService } from '../../services/weekly-themes.service';

/**
 * Tells a creator that the themed day's standing notes will shape what they are about to generate, shows them
 * the notes, and lets them leave the notes out of this one run (AF.7.4).
 *
 * Shared by every surface that generates for a piece of work with a themed day. It renders nothing at all
 * unless the work's theme has notes — no theme, no notes, a theme that has been deleted and a week that could
 * not be read are all the same quiet absence, because in each of them there is nothing in effect to announce.
 *
 * **It decides nothing about the request.** The switch is a two-way `apply` value the host sends with its own
 * request; the server is what leaves the notes out, and what records that it did.
 */
@Component({
  selector: 'cp-day-notes-notice',
  standalone: true,
  imports: [CpCheckboxComponent, CpNoticeComponent, RouterLink],
  template: `
    @if (theme(); as theme) {
      <cp-notice role="note">
        <div class="body">
          <!-- Polite, so turning the notes off or on is said aloud as well as shown: the checkbox announces its
               own state, and this is what that state means for the run. -->
          <p class="lead" aria-live="polite">
            @if (apply()) {
              <strong>{{ theme.displayName }}</strong
              >’s standing notes are in effect. What you generate here will follow them.
            } @else {
              <strong>{{ theme.displayName }}</strong
              >’s standing notes are switched off for this run.
            }
          </p>

          <details class="cp-disclosure">
            <summary>Read the notes</summary>
            <p class="notes">{{ theme.standingNotes }}</p>
            <a [routerLink]="['/', workspaceSlug(), 'settings', 'themed-days']">Change them in themed days</a>
          </details>

          <cp-checkbox label="Use these notes for this run" [(checked)]="apply" />

          @if (usesAlreadyMade()) {
            <p class="also">
              The titles of what you have already made for this day are used too, so it is not made again. Switching the
              notes off does not change that.
            </p>
          }
        </div>
      </cp-notice>
    }
  `,
  styles: [
    `
      :host {
        display: block;
      }

      /* Takes no room when there is nothing in effect to say. Keyed on what the component knows rather than
         on whether it happens to have rendered a node. */
      :host(:not([data-shown])) {
        display: none;
      }

      .body {
        display: grid;
        gap: var(--cp-space-2);
      }

      .lead {
        margin: 0;
      }

      /* A creator's notes are written in lines, and the lines are part of what they said. */
      .notes {
        margin: 0 0 var(--cp-space-2);
        max-width: var(--cp-measure-field);
        white-space: pre-wrap;
        overflow-wrap: anywhere;
      }

      .also {
        margin: 0;
        color: var(--cp-text-muted);
        font-size: var(--cp-font-size-sm);
      }

      a {
        color: var(--cp-primary);
        font-size: var(--cp-font-size-sm);
      }
    `,
  ],
  // Present exactly while the notice is showing, so a host can space around it without guessing.
  host: { '[attr.data-shown]': 'theme() ? "" : null' },
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DayNotesNoticeComponent {
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();
  /** The work's theme key, or null when it has none. */
  readonly themeKey = input.required<string | null>();

  /**
   * True on a surface whose generation is also told what was already made for the day. Said in the notice, so a
   * creator knows what of their day reaches a model and that the switch below is about the notes alone.
   */
  readonly usesAlreadyMade = input(false);

  /** Whether this run follows the notes. The host sends it with its request; true until the creator says otherwise. */
  readonly apply = model(true);

  private readonly found = signal<WeeklyTheme | null>(null);

  /** The theme, only when it has notes worth announcing. */
  protected readonly theme = computed(() => {
    const theme = this.found();
    return theme !== null && (theme.standingNotes ?? '').trim().length > 0 ? theme : null;
  });

  private loadedFor: string | null = null;

  constructor() {
    // The week is read once per workspace and theme, not on every change of anything else.
    effect(() => {
      const slug = this.workspaceSlug();
      const key = this.themeKey();
      const loading = `${slug}|${key ?? ''}`;
      if (loading === this.loadedFor) return;

      this.loadedFor = loading;
      untracked(() => void this.load(slug, key, loading));
    });
  }

  private async load(slug: string, key: string | null, loading: string): Promise<void> {
    this.found.set(null);
    if (key === null) return;

    // Resolved here rather than at construction: most work has no themed day, and a host showing such work
    // should not need the themes client at all.
    const week = await this.injector.get(WeeklyThemesService).getWeek(slug);

    // The inputs moved on while this was in flight; a later read owns the answer.
    if (loading !== this.loadedFor) return;

    // A retired theme still counts: the work was made for it, and the server follows its notes too.
    this.found.set(week.status === 'found' ? (week.themes.find((theme) => theme.key === key) ?? null) : null);
  }
}
