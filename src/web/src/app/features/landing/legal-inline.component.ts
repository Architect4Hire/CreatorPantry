import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import { LegalInline } from './legal-document';

/** One run of a legal document's text: its words, its emphasis and its links, drawn as elements, never as HTML. */
@Component({
  selector: 'cp-legal-inline',
  standalone: true,
  imports: [RouterLink],
  template: `@for (span of spans(); track $index) {
    @switch (span.kind) {
      @case ('strong') {
        <strong>{{ span.text }}</strong>
      }
      @case ('em') {
        <em>{{ span.text }}</em>
      }
      @case ('code') {
        <code>{{ span.text }}</code>
      }
      @case ('route') {
        <a [routerLink]="span.path">{{ span.text }}</a>
      }
      @case ('mail') {
        <a [href]="'mailto:' + span.text">{{ span.text }}</a>
      }
      @default {
        {{ span.text }}
      }
    }
  }`,
  styles: `
    :host {
      display: contents;
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LegalInlineComponent {
  readonly spans = input.required<readonly LegalInline[]>();
}
