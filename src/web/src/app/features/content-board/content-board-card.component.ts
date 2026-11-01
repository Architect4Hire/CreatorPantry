import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import {
  CpBadgeComponent,
  CpButtonComponent,
  CpCardComponent,
  CpFactComponent,
  CpFactsComponent,
  CpStatusPillComponent,
} from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import { ContentCard } from '../../models/content-board.models';
import { channelNameOf, publishingStatusOf } from './content-board-presentation';

/**
 * One card in a column of the content board (BOARD-UI-001/002).
 *
 * **The title is the link; the card is not one.** It opens the card's own page, and the whole card stays free
 * to be picked up and dragged without a click on it going anywhere.
 *
 * **"Move" is the keyboard's way to do everything a drag does.** It is on every card, named for the card it
 * moves, and opens a short form that asks which column and where. Dragging is a shortcut for it, never the
 * only way (SC 2.5.7).
 *
 * **Status is a word and a glyph, never a colour alone.** Publishing is said from the server's own numbers; a
 * card says nothing about delivery by being in a column.
 *
 * **It shows a preview, not the description.** The whole text is the card page's to read.
 */
@Component({
  selector: 'cp-content-board-card',
  standalone: true,
  imports: [
    DatePipe,
    RouterLink,
    CpBadgeComponent,
    CpButtonComponent,
    CpCardComponent,
    CpFactComponent,
    CpFactsComponent,
    CpStatusPillComponent,
  ],
  template: `<cp-card>
    <article [attr.aria-busy]="saving() ? 'true' : null">
      <!-- One below the column that holds it, which is an h3 under the list's h2. -->
      <h4 class="title"
        ><a [routerLink]="link()">{{ card().title }}</a></h4
      >

      @if (card().descriptionPreview; as preview) {
        <p class="cp-muted preview">{{ preview }}</p>
      }

      <div class="badges">
        <cp-status-pill [tone]="publishing().tone">{{ publishing().label }}</cp-status-pill>
        @if (channelName(); as channel) {
          <cp-badge tone="blue">{{ channel }}</cp-badge>
        }
        @if (card().hasPackage) {
          <cp-badge tone="purple">Recipe package</cp-badge>
        }
      </div>

      <dl cpFacts layout="inline">
        @if (card().plannedDate; as planned) {
          <!-- A calendar day, not an instant: read and shown as UTC midnight so it is that day for every reader. A bare date would be read as local midnight, a day out east of UTC. -->
          <div cpFact label="Planned">{{ planned + 'T00:00:00Z' | date: 'EEE, MMM d, y' : 'UTC' }}</div>
        }
        <div cpFact label="Moves">{{ card().moveCount }}</div>
      </dl>

      @if (saving()) {
        <p class="cp-muted saving" role="status">Saving its new place…</p>
      }

      <div class="cp-actions">
        <button
          cpButton
          type="button"
          variant="secondary"
          size="sm"
          [disabled]="locked()"
          [attr.aria-label]="'Move ' + card().title"
          (click)="moveRequested.emit(card())"
        >
          Move
        </button>
      </div>
    </article>
  </cp-card>`,
  styles: [
    `
      :host {
        display: block;
      }
      article {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-2);
      }
      .title {
        margin: 0;
        font: 600 var(--cp-font-size-md) / var(--cp-leading-tight) var(--cp-font-sans);
        overflow-wrap: anywhere;
      }
      .title a {
        color: inherit;
        text-decoration: none;
      }
      .title a:hover {
        color: var(--cp-primary);
        text-decoration: underline;
      }
      /* :focus, not only :focus-visible: after a move, focus is put back on this link by script. */
      .title a:focus {
        outline: 2px solid var(--cp-focus);
        outline-offset: 2px;
      }
      .preview,
      .saving {
        margin: 0;
        font-size: var(--cp-font-size-sm);
        overflow-wrap: anywhere;
      }
      .badges {
        display: flex;
        flex-wrap: wrap;
        gap: var(--cp-space-2);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentBoardCardComponent {
  readonly card = input.required<ContentCard>();
  readonly workspaceSlug = input.required<string>();
  readonly channels = input<readonly ContentChannel[]>([]);
  /** True while this card's own move is waiting for the server. */
  readonly saving = input(false);
  /** True while any move is out: one at a time, so every card's Move waits. */
  readonly locked = input(false);

  readonly moveRequested = output<ContentCard>();

  protected readonly link = computed(() => ['/', this.workspaceSlug(), 'content-board', this.card().id]);
  protected readonly publishing = computed(() => publishingStatusOf(this.card().publishing));
  protected readonly channelName = computed(() => channelNameOf(this.channels(), this.card().channelKey));
}
