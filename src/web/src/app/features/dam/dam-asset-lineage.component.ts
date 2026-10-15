import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';

import {
  DAM_RECIPE_ROLE_LABELS,
  DamAssetDetail,
  DamAssetPrompt,
  DamAssetRecipeLink,
  damPromptName,
} from '../../models/dam-asset.models';
import { PROMPT_SOURCE_LABELS } from '../../models/prompt-library.models';

/**
 * Where one library picture came from: the recipes that use it and the saved prompts that produced it, each by
 * name and linked to its own page (DAM-UI-003). Shared by the picture's page and the guided "Describe it" step
 * (13A.21), so the two say it the same way.
 *
 * **Names, never words.** A prompt is named by its label and opened on the Prompt Library page that shows it; its
 * text is not published by the asset's route, so it is not shown here either.
 */
@Component({
  selector: 'cp-dam-asset-lineage',
  standalone: true,
  imports: [DatePipe, RouterLink],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <h3>Recipes</h3>
    @if (asset().recipeLinks.length === 0) {
      <p class="plain cp-muted">Not linked to a recipe.</p>
    } @else {
      <ul class="links" aria-label="Recipes that use this picture">
        @for (link of asset().recipeLinks; track link.recipeId + link.role) {
          <li>
            <a [routerLink]="recipeLink(link)">{{ link.title }}</a>
            <span class="note">{{ roleLabel(link) }}</span>
            @if (link.caption) {
              <span class="note prose">“{{ link.caption }}”</span>
            }
          </li>
        }
      </ul>
    }

    <h3>Prompts</h3>
    @if (asset().prompts.length === 0) {
      <p class="plain cp-muted">No saved prompt is linked to this picture.</p>
    } @else {
      <ul class="links" aria-label="Prompts that produced this picture">
        @for (prompt of asset().prompts; track prompt.id) {
          <li>
            <a [routerLink]="promptLink(prompt)">{{ promptName(prompt) }}</a>
            <span class="note">{{ promptSource(prompt) }}</span>
            <span class="note">Saved {{ prompt.createdAt | date: 'mediumDate' }}</span>
          </li>
        }
      </ul>
    }
  `,
  styles: `
    :host {
      display: grid;
      grid-template-columns: minmax(0, 1fr);
      gap: var(--cp-space-3);
    }

    h3 {
      margin: var(--cp-space-2) 0 0;
      font: 600 var(--cp-font-size-md) / var(--cp-leading-tight) var(--cp-font-sans);
    }

    .plain {
      margin: 0;
    }

    .note {
      color: var(--cp-text-muted);
      font-size: var(--cp-font-size-sm);
    }

    /* The creator's own words, line breaks included. */
    .prose {
      white-space: pre-wrap;
      overflow-wrap: anywhere;
    }

    .links {
      display: grid;
      gap: var(--cp-space-2);
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .links li {
      display: flex;
      flex-wrap: wrap;
      align-items: baseline;
      gap: var(--cp-space-2) var(--cp-space-3);
    }

    .links a {
      display: inline-flex;
      align-items: center;
      min-height: 40px;
      overflow-wrap: anywhere;
    }

    .links a:focus-visible {
      outline: 2px solid var(--cp-focus);
      outline-offset: 2px;
    }
  `,
})
export class DamAssetLineageComponent {
  readonly workspaceSlug = input.required<string>();
  readonly asset = input.required<DamAssetDetail>();

  protected recipeLink(link: DamAssetRecipeLink): readonly string[] {
    return ['/', this.workspaceSlug(), 'recipes', link.recipeId];
  }

  protected roleLabel(link: DamAssetRecipeLink): string {
    return DAM_RECIPE_ROLE_LABELS[link.role];
  }

  protected promptLink(prompt: DamAssetPrompt): readonly string[] {
    return ['/', this.workspaceSlug(), 'prompt-library', prompt.id];
  }

  protected promptName(prompt: DamAssetPrompt): string {
    return damPromptName(prompt);
  }

  protected promptSource(prompt: DamAssetPrompt): string {
    return PROMPT_SOURCE_LABELS[prompt.source];
  }
}
