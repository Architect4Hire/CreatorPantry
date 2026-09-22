import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type CpTheme = 'light' | 'dark' | 'system';
type ResolvedTheme = Exclude<CpTheme, 'system'>;

@Injectable({ providedIn: 'root' })
export class CpThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly storageKey = 'creator-pantry-theme';
  private readonly media = typeof matchMedia === 'function' ? matchMedia('(prefers-color-scheme: dark)') : null;
  readonly preference = signal<CpTheme>('system');
  readonly resolved = signal<ResolvedTheme>('light');

  constructor() {
    const saved = this.document.defaultView?.localStorage.getItem(this.storageKey) as CpTheme | null;
    this.set(saved === 'light' || saved === 'dark' || saved === 'system' ? saved : 'system');
    this.media?.addEventListener('change', () => this.apply(this.preference()));
  }

  set(theme: CpTheme): void {
    this.preference.set(theme);
    this.document.defaultView?.localStorage.setItem(this.storageKey, theme);
    this.apply(theme);
  }

  toggle(): void { this.set(this.resolved() === 'dark' ? 'light' : 'dark'); }

  private apply(theme: CpTheme): void {
    const resolved: ResolvedTheme = theme === 'system' ? (this.media?.matches ? 'dark' : 'light') : theme;
    this.resolved.set(resolved);
    this.document.documentElement.dataset['cpTheme'] = resolved;
  }
}
