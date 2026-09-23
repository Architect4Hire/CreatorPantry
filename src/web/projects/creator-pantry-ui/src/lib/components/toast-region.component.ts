import { ChangeDetectionStrategy, Component, computed, effect, input, output, signal } from '@angular/core';

import { CpButtonComponent } from './button.component';

export type CpToastSeverity = 'info' | 'success' | 'warning' | 'error';

export interface CpToast {
  id: string;
  message: string;
  severity: CpToastSeverity;
  timeoutMs?: number;
}

const DEFAULT_TIMEOUT_MS = 5000;

interface CpToastTimerState {
  handle: ReturnType<typeof setTimeout> | null;
  deadline: number | null;
  remainingMs: number | null;
}

@Component({
  selector: 'cp-toast-region', standalone: true,
  imports: [CpButtonComponent],
  template: `<div class="region polite" role="status" aria-live="polite" aria-atomic="false">@for(toast of politeToasts(); track toast.id){<div class="toast" [attr.data-severity]="toast.severity" (mouseenter)="pause(toast.id)" (mouseleave)="resume(toast.id)" (focusin)="pause(toast.id)" (focusout)="resume(toast.id)"><span class="message">{{toast.message}}</span><button cpButton type="button" variant="ghost" size="sm" (click)="dismiss(toast.id)">Dismiss</button></div>}</div><div class="region assertive" role="alert" aria-live="assertive" aria-atomic="false">@for(toast of assertiveToasts(); track toast.id){<div class="toast" [attr.data-severity]="toast.severity" (mouseenter)="pause(toast.id)" (mouseleave)="resume(toast.id)" (focusin)="pause(toast.id)" (focusout)="resume(toast.id)"><span class="message">{{toast.message}}</span><button cpButton type="button" variant="ghost" size="sm" (click)="dismiss(toast.id)">Dismiss</button></div>}</div>`,
  styles: [`:host{display:block}.region{position:fixed;right:var(--cp-space-5);z-index:var(--cp-z-modal);display:flex;flex-direction:column;gap:var(--cp-space-2);pointer-events:none}.region.polite{bottom:var(--cp-space-5)}.region.assertive{top:var(--cp-space-5)}.toast{pointer-events:auto;display:flex;align-items:center;gap:var(--cp-space-3);min-width:16rem;max-width:24rem;padding:var(--cp-space-3) var(--cp-space-4);border:1px solid var(--cp-border);border-left:4px solid var(--cp-border-strong);border-radius:var(--cp-radius-md);background:var(--cp-surface-raised);color:var(--cp-text);box-shadow:var(--cp-shadow-sm);font:400 var(--cp-font-size-sm)/var(--cp-leading-normal) var(--cp-font-sans);opacity:1;transform:translateY(0);transition:opacity var(--cp-duration-normal) var(--cp-ease),transform var(--cp-duration-normal) var(--cp-ease)}.toast[data-severity='info']{border-left-color:var(--cp-blue)}.toast[data-severity='success']{border-left-color:var(--cp-primary)}.toast[data-severity='warning']{border-left-color:var(--cp-orange)}.toast[data-severity='error']{border-left-color:var(--cp-danger)}.message{flex:1}`],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpToastRegionComponent {
  readonly toasts = input<CpToast[]>([]);
  readonly dismissed = output<string>();

  private readonly dismissedIds = signal<ReadonlySet<string>>(new Set());
  private readonly timers = new Map<string, CpToastTimerState>();

  private readonly visibleToasts = computed(() => {
    const dismissedIds = this.dismissedIds();
    const seen = new Set<string>();
    const result: CpToast[] = [];
    for (const toast of this.toasts()) {
      if (dismissedIds.has(toast.id)) continue;
      const key = `${toast.severity}::${toast.message}`;
      if (seen.has(key)) continue;
      seen.add(key);
      result.push(toast);
    }
    return result;
  });

  readonly politeToasts = computed(() => this.visibleToasts().filter(toast => toast.severity === 'info' || toast.severity === 'success'));
  readonly assertiveToasts = computed(() => this.visibleToasts().filter(toast => toast.severity === 'warning' || toast.severity === 'error'));

  constructor() {
    effect(() => {
      const toasts = this.toasts();
      const visible = this.visibleToasts();
      const currentIds = new Set(toasts.map(toast => toast.id));

      for (const toast of visible) {
        if (!this.timers.has(toast.id)) this.startTimer(toast);
      }

      for (const id of Array.from(this.timers.keys())) {
        if (!currentIds.has(id)) {
          const state = this.timers.get(id);
          if (state?.handle !== null && state?.handle !== undefined) clearTimeout(state.handle);
          this.timers.delete(id);
        }
      }

      const dismissedIds = this.dismissedIds();
      if (dismissedIds.size > 0) {
        const next = new Set(Array.from(dismissedIds).filter(id => currentIds.has(id)));
        if (next.size !== dismissedIds.size) this.dismissedIds.set(next);
      }
    });
  }

  dismiss(id: string): void {
    if (this.dismissedIds().has(id)) return;
    const state = this.timers.get(id);
    if (state?.handle !== null && state?.handle !== undefined) clearTimeout(state.handle);
    this.timers.delete(id);
    const next = new Set(this.dismissedIds());
    next.add(id);
    this.dismissedIds.set(next);
    this.dismissed.emit(id);
  }

  pause(id: string): void {
    const state = this.timers.get(id);
    if (!state || state.handle === null || state.deadline === null) return;
    clearTimeout(state.handle);
    state.remainingMs = Math.max(0, state.deadline - Date.now());
    state.handle = null;
    state.deadline = null;
  }

  resume(id: string): void {
    const state = this.timers.get(id);
    if (!state || state.handle !== null || state.remainingMs === null) return;
    this.armTimer(id);
  }

  private startTimer(toast: CpToast): void {
    const timeoutMs = this.effectiveTimeout(toast);
    if (timeoutMs === undefined) {
      this.timers.set(toast.id, { handle: null, deadline: null, remainingMs: null });
      return;
    }
    this.timers.set(toast.id, { handle: null, deadline: null, remainingMs: timeoutMs });
    this.armTimer(toast.id);
  }

  private armTimer(id: string): void {
    const state = this.timers.get(id);
    if (!state || state.remainingMs === null) return;
    const duration = state.remainingMs;
    state.deadline = Date.now() + duration;
    state.handle = setTimeout(() => this.handleTimeout(id), duration);
  }

  private handleTimeout(id: string): void {
    const state = this.timers.get(id);
    if (state) state.handle = null;
    this.dismiss(id);
  }

  private effectiveTimeout(toast: CpToast): number | undefined {
    if (toast.timeoutMs !== undefined) return toast.timeoutMs;
    if (toast.severity === 'info' || toast.severity === 'success') return DEFAULT_TIMEOUT_MS;
    return undefined;
  }
}
