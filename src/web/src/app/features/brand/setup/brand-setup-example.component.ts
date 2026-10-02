import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CpNoticeComponent, CpStatusPillComponent, CpStatusPillTone } from '@creator-pantry/ui';

/**
 * One of the creator's examples, as both the step that adds them and the step that checks their text show it.
 *
 * **Why the two steps share this.** They are consecutive steps about the same object: a file with a name, a
 * line of metadata, a state, a sentence about that state, and a row of things to do to it. Written separately
 * they drew it two different ways — a flex row in one, a bordered panel inside a form section in the other —
 * so the same example changed shape when the creator pressed Continue.
 *
 * **Why the title is not a heading.** A step's examples are a list of one kind of thing, not sections of the
 * step; making each a heading put a row of `h2`s beside the step's own. The card names itself for assistive
 * technology from its title instead, which is what a region needs without claiming a place in the outline.
 *
 * It owns no behaviour: every action is projected by the step that knows what the action means.
 */
@Component({
  selector: 'cp-brand-setup-example',
  standalone: true,
  imports: [CpNoticeComponent, CpStatusPillComponent],
  template: `<article class="example" [attr.aria-label]="title()">
    <div class="head">
      <span class="who">
        <strong class="title">{{ title() }}</strong>
        @if (meta()) {
          <span class="cp-muted">{{ meta() }}</span>
        }
      </span>
      <cp-status-pill [tone]="tone()">{{ status() }}</cp-status-pill>
    </div>

    @if (note()) {
      <p class="note">{{ note() }}</p>
    }

    <ng-content />

    <div class="cp-actions">
      <ng-content select="[exampleActions]" />
    </div>

    @if (problem()) {
      <cp-notice tone="error" role="alert">{{ problem() }}</cp-notice>
    }
  </article>`,
  styles: [
    `
      :host {
        display: block;
      }
      /* The choice tile's surface at a larger size, so a list of examples and a list of answers read as the
         same product rather than two. */
      .example {
        display: grid;
        grid-template-columns: minmax(0, 1fr);
        gap: var(--cp-space-3);
        min-width: 0;
        padding: var(--cp-space-4);
        border: 1px solid var(--cp-border);
        border-radius: var(--cp-radius-lg);
        background: var(--cp-surface);
      }
      .head {
        display: flex;
        flex-wrap: wrap;
        align-items: baseline;
        justify-content: space-between;
        gap: var(--cp-space-2) var(--cp-space-4);
      }
      .who {
        display: grid;
        min-width: 0;
      }
      .title {
        overflow-wrap: anywhere;
      }
      .note {
        margin: 0;
      }
      /* An empty actions row would otherwise still take its gap. */
      .cp-actions:empty {
        display: none;
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupExampleComponent {
  readonly title = input.required<string>();

  /** What it is and where it came from: the kind, the file name, the size. */
  readonly meta = input('');

  readonly tone = input<CpStatusPillTone>('neutral');

  /** The state in a word, for the pill beside the title. */
  readonly status = input.required<string>();

  /** One sentence saying what that state means for the creator. */
  readonly note = input('');

  /** Something that went wrong acting on this example. Announced where it happened. */
  readonly problem = input('');
}
