import { DestroyRef, Injectable, inject, signal } from '@angular/core';
import { Observable, Subscription, map, of, switchMap } from 'rxjs';

import { damThumbnailAltText } from '../../models/dam-asset.models';
import { RecipeAssetLink, RecipeDetail } from '../../models/recipe.models';
import { DamAssetService } from '../../services/dam-asset.service';

// What a recipe's own pages put at the top: the facts a cook checks before starting, and the recipe's lead picture.
// The editor and the kitchen view both show them, so they are worked out once, here.

/** Words a count of minutes the way a cook says it: `45 min`, `1 hr`, `1 hr 20 min`. */
export function formatKitchenMinutes(minutes: number): string {
  const hours = Math.floor(minutes / 60);
  const rest = minutes % 60;

  if (hours === 0) return `${rest} min`;

  return rest === 0 ? `${hours} hr` : `${hours} hr ${rest} min`;
}

/** One term and its value, already worded. */
export interface RecipeFact {
  readonly label: string;
  readonly value: string;
}

/**
 * The yield, serving count and times, in the order a cook checks them before starting. A fact the recipe does
 * not record is left out rather than shown empty.
 *
 * **As stored.** The yield is the creator's own wording, verbatim, and the serving count is a separate fact
 * beside it — never folded into one sentence (.claude/rules/recipes.md). Total is the stored total, never a sum
 * of the three before it: the same rule the recipe itself keeps.
 */
export function recipeFacts(recipe: RecipeDetail): readonly RecipeFact[] {
  const facts: RecipeFact[] = [];
  if (recipe.yieldText) facts.push({ label: 'Makes', value: recipe.yieldText });
  if (recipe.servingCount !== null) facts.push({ label: 'Serves', value: `${recipe.servingCount}` });
  if (recipe.prepTimeMinutes !== null)
    facts.push({ label: 'Prep', value: formatKitchenMinutes(recipe.prepTimeMinutes) });
  if (recipe.cookTimeMinutes !== null)
    facts.push({ label: 'Cook', value: formatKitchenMinutes(recipe.cookTimeMinutes) });
  if (recipe.restTimeMinutes !== null)
    facts.push({ label: 'Rest', value: formatKitchenMinutes(recipe.restTimeMinutes) });
  if (recipe.totalTimeMinutes !== null)
    facts.push({ label: 'Total', value: formatKitchenMinutes(recipe.totalTimeMinutes) });

  return facts;
}

/** The recipe's lead picture: the first `Hero` link by its order, or null when it has none. */
export function recipeHeroLink(links: readonly RecipeAssetLink[]): RecipeAssetLink | null {
  return [...links].filter((link) => link.role === 'Hero').sort((a, b) => a.sortOrder - b.sortOrder)[0] ?? null;
}

export type RecipeHeroPictureState =
  /** No lead picture, or one that cannot be shown. Either way the head has no picture and takes no room for one. */
  | { readonly status: 'none' }
  | { readonly status: 'loading' }
  | { readonly status: 'ready'; readonly url: string; readonly alt: string };

/**
 * The recipe's lead picture for the page head's `[cpPageHeadMedia]` slot (AU.4.3, APP-005).
 *
 * Provided by the page that shows it, so it lives and dies with that page.
 *
 * **The page's own `<img>`.** The head never takes a URL; this reads the asset the link names — its title and the
 * alt text the creator wrote — then its bytes from the authorised route, as the web-sized copy and never the
 * original, and hands the page an object URL. A link held at an older version shows that version.
 *
 * **Alt text is the stored alt text**, or, when none was written, the same sentence the libraries use, which names
 * the picture and says it has no description. Nothing is said about what it shows that the creator did not say
 * (.claude/rules/media.md).
 *
 * **A picture that cannot be shown is no picture here.** An asset gone from the library, or bytes that could not
 * be read, leave the slot empty; the Media tab is where the recipe's pictures are looked after, and says so there.
 *
 * **It owns its object URL and revokes it**, when the picture changes and when the page goes.
 */
@Injectable()
export class RecipeHeroPicture {
  private readonly assets = inject(DamAssetService);

  private readonly stateSignal = signal<RecipeHeroPictureState>({ status: 'none' });
  readonly state = this.stateSignal.asReadonly();

  /** What is showing or being fetched, so the same links applied again — after a save — fetch nothing. */
  private key: string | null = null;
  private fetching: Subscription | null = null;
  private held: string | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.fetching?.unsubscribe();
      this.release();
    });
  }

  /** Shows the lead picture among these links, or none. */
  show(workspaceSlug: string, links: readonly RecipeAssetLink[]): void {
    const hero = recipeHeroLink(links);
    const key = hero === null ? null : `${workspaceSlug}|${hero.mediaAssetId}|${hero.mediaAssetVersionNumber ?? ''}`;
    if (key === this.key) return;

    this.key = key;
    this.fetching?.unsubscribe();
    this.release();

    if (hero === null) {
      this.stateSignal.set({ status: 'none' });
      return;
    }

    this.stateSignal.set({ status: 'loading' });
    this.fetching = this.read(workspaceSlug, hero).subscribe((picture) => {
      if (picture === null) {
        this.stateSignal.set({ status: 'none' });
        return;
      }

      this.held = URL.createObjectURL(picture.bytes);
      this.stateSignal.set({ status: 'ready', url: this.held, alt: picture.alt });
    });
  }

  private read(workspaceSlug: string, hero: RecipeAssetLink): Observable<{ bytes: Blob; alt: string } | null> {
    return this.assets.detail(workspaceSlug, hero.mediaAssetId).pipe(
      switchMap((outcome) => {
        if (outcome.status !== 'found') return of(null);

        const asset = outcome.asset;
        const pinned = hero.mediaAssetVersionNumber;
        // The current version's own number is the current version: the render route is the one built for it.
        const bytes =
          pinned === null || pinned === asset.currentVersion?.versionNumber
            ? this.assets.content(workspaceSlug, asset.id, 'web')
            : this.assets.versionContent(workspaceSlug, asset.id, pinned, 'web');

        return bytes.pipe(
          map((content) =>
            content.status === 'found' ? { bytes: content.bytes, alt: damThumbnailAltText(asset) } : null,
          ),
        );
      }),
    );
  }

  private release(): void {
    if (this.held !== null) {
      URL.revokeObjectURL(this.held);
      this.held = null;
    }
  }
}
