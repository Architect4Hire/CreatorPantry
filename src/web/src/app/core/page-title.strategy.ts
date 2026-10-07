import { Injectable, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { ActivatedRouteSnapshot, RouterStateSnapshot, TitleStrategy } from '@angular/router';

export const APP_NAME = 'CreatorPantry';

/**
 * Sets the browser tab title after every navigation, so no component has to.
 *
 * A route names its page with `data: { title: 'Recipes' }` — the same value the shell's heading reads. The
 * deepest primary route that names itself wins, so a section can title its index and each child page
 * separately. A route that needs a dynamic title (a recipe's own name) can use Angular's native `title`
 * resolver instead, which takes precedence; no component changes either way.
 */
@Injectable({ providedIn: 'root' })
export class PageTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const pageTitle = this.buildTitle(snapshot) ?? deepestDataTitle(snapshot.root);
    this.title.setTitle(pageTitle ? `${pageTitle} · ${APP_NAME}` : APP_NAME);
  }
}

function deepestDataTitle(root: ActivatedRouteSnapshot): string | undefined {
  let title: string | undefined;
  for (let node: ActivatedRouteSnapshot | null = root; node; node = node.firstChild) {
    const value = node.data['title'];
    if (typeof value === 'string' && value.trim().length > 0) title = value;
  }
  return title;
}
