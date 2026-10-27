import { ChangeDetectionStrategy, Component, computed, input, model } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { CpFieldComponent } from '@creator-pantry/ui';

import { ContentChannel } from '../../models/brand-profile.models';
import { ContentCardFields } from '../../models/content-board.models';
import { CONTENT_CARD_DESCRIPTION_MAX, CONTENT_CARD_TITLE_MAX } from './content-board-presentation';

/**
 * The fields a creator fills in for a card: its title, what it is for, its channel and its planned date.
 *
 * Shared by the form that makes a card and the one that edits it, so the two cannot ask for different things.
 * It holds no request and no rule: `value` is two-way, `errors` are the server's own, keyed by request field,
 * and what happens on submit is the form's that hosts it.
 *
 * **What a card links to is not edited here.** A card's recipe, picture, prompt and posts are set by what made
 * it; this form leaves every one of them alone, and an edit sends only the fields that changed.
 */
@Component({
  selector: 'cp-content-card-fields',
  standalone: true,
  imports: [FormsModule, CpFieldComponent],
  template: `<cp-field label="Title" [forId]="id('title')" [required]="true" [error]="errorFor('title')">
      <input
        type="text"
        [id]="id('title')"
        name="title"
        autocomplete="off"
        [attr.autofocus]="autofocus() ? '' : null"
        [maxlength]="titleMax"
        [disabled]="disabled()"
        [ngModel]="value().title"
        (ngModelChange)="set('title', $event)"
      />
    </cp-field>

    <cp-field
      label="What it is for"
      [forId]="id('description')"
      hint="Notes for you and your collaborators. Optional."
      [error]="errorFor('description')"
    >
      <textarea
        rows="4"
        [id]="id('description')"
        name="description"
        [maxlength]="descriptionMax"
        [disabled]="disabled()"
        [ngModel]="value().description ?? ''"
        (ngModelChange)="set('description', $event)"
      ></textarea>
    </cp-field>

    <cp-field label="Channel" [forId]="id('channel')" hint="Optional." [error]="errorFor('channelKey')">
      <select
        [id]="id('channel')"
        name="channelKey"
        [disabled]="disabled()"
        [ngModel]="value().channelKey ?? ''"
        (ngModelChange)="set('channelKey', $event || null)"
      >
        <option value="">No channel in particular</option>
        @for (option of channelOptions(); track option.key) {
          <option [value]="option.key">{{ option.label }}</option>
        }
      </select>
    </cp-field>

    <cp-field
      label="Planned date"
      [forId]="id('date')"
      hint="The day you plan this for. It schedules nothing. Optional."
      [error]="errorFor('plannedDate')"
    >
      <input
        type="date"
        [id]="id('date')"
        name="plannedDate"
        [disabled]="disabled()"
        [ngModel]="value().plannedDate ?? ''"
        (ngModelChange)="set('plannedDate', $event || null)"
      />
    </cp-field>`,
  styles: [
    `
      :host {
        display: grid;
        gap: var(--cp-space-4);
      }
    `,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ContentCardFieldsComponent {
  /** Makes each control's id unique, so two forms on one page do not share a label. */
  readonly idPrefix = input.required<string>();
  readonly value = model.required<ContentCardFields>();
  readonly channels = input<readonly ContentChannel[]>([]);
  /** The server's field errors, keyed by request field, plus `title` for the one check made here. */
  readonly errors = input<Record<string, readonly string[]>>({});
  readonly disabled = input(false);
  /** True where the host wants focus to land on the title when it opens. */
  readonly autofocus = input(false);

  protected readonly titleMax = CONTENT_CARD_TITLE_MAX;
  protected readonly descriptionMax = CONTENT_CARD_DESCRIPTION_MAX;

  /**
   * Channels that can be chosen now, plus the one the card already has if it has since been retired: a card
   * keeps a retired channel until the creator changes it, and the control must be able to show that.
   */
  protected readonly channelOptions = computed(() => {
    const current = this.value().channelKey;
    const options = this.channels()
      .filter((channel) => channel.isActive || channel.key === current)
      .map((channel) => ({
        key: channel.key,
        label: channel.isActive ? channel.displayName : `${channel.displayName} (retired)`,
      }));

    if (current !== null && !options.some((option) => option.key === current)) {
      options.push({ key: current, label: current });
    }

    return options;
  });

  protected id(name: string): string {
    return `${this.idPrefix()}-${name}`;
  }

  protected errorFor(field: string): string {
    return this.errors()[field]?.[0] ?? '';
  }

  protected set<K extends keyof ContentCardFields>(field: K, value: ContentCardFields[K]): void {
    this.value.update((fields) => ({ ...fields, [field]: value }));
  }
}
