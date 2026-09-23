import { ChangeDetectionStrategy, Component, ElementRef, computed, effect, inject, input, model, signal } from '@angular/core';

export interface CpTabDefinition { id: string; label: string; disabled?: boolean; }

@Component({
  selector: 'cp-tabs', standalone: true,
  template: `<div role="tablist" [attr.aria-label]="ariaLabel()" (keydown)="onKeydown($event)">@for(tab of tabs(); track tab.id){<button type="button" role="tab" [id]="'tab-'+tab.id" [attr.data-tab-id]="tab.id" [attr.aria-controls]="'panel-'+tab.id" [attr.aria-selected]="isSelected(tab.id)" [attr.aria-disabled]="tab.disabled || null" [attr.tabindex]="isSelected(tab.id) ? 0 : -1" [disabled]="tab.disabled || false" (click)="selectTab(tab.id)">{{tab.label}}</button>}</div><ng-content />`,
  styles: [`:host{display:block}[role='tablist']{display:flex;gap:var(--cp-space-1);border-bottom:1px solid var(--cp-border)}[role='tab']{appearance:none;border:0;background:transparent;padding:var(--cp-space-2) var(--cp-space-4);font:600 var(--cp-font-size-sm)/var(--cp-leading-tight) var(--cp-font-sans);color:var(--cp-text-muted);cursor:pointer;border-radius:var(--cp-radius-md) var(--cp-radius-md) 0 0;margin-bottom:-1px;border-bottom:2px solid transparent}[role='tab']:hover:not(:disabled){color:var(--cp-text)}[role='tab'][aria-selected='true']{color:var(--cp-primary);border-bottom-color:var(--cp-primary)}[role='tab']:disabled{color:var(--cp-text-faint);cursor:not-allowed}`],
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpTabsComponent {
  readonly tabs = input.required<CpTabDefinition[]>();
  readonly ariaLabel = input.required<string>();
  readonly selectedId = model<string>();

  private readonly elementRef: ElementRef<HTMLElement> = inject(ElementRef);
  private readonly firstEnabledId = computed(() => this.tabs().find(tab => !tab.disabled)?.id);

  constructor() {
    effect(() => {
      const tabs = this.tabs();
      const current = this.selectedId();
      if (current !== undefined && tabs.some(tab => tab.id === current && !tab.disabled)) return;
      const fallback = tabs.find(tab => !tab.disabled)?.id;
      if (fallback !== undefined && fallback !== current) this.selectedId.set(fallback);
    });
  }

  isSelected(id: string): boolean {
    return (this.selectedId() ?? this.firstEnabledId()) === id;
  }

  selectTab(id: string): void {
    const tab = this.tabs().find(candidate => candidate.id === id);
    if (!tab || tab.disabled) return;
    this.selectedId.set(id);
  }

  onKeydown(event: KeyboardEvent): void {
    const enabled = this.tabs().filter(tab => !tab.disabled);
    if (enabled.length === 0) return;
    const currentId = this.selectedId() ?? this.firstEnabledId();
    const currentIndex = enabled.findIndex(tab => tab.id === currentId);
    let targetIndex: number;
    switch (event.key) {
      case 'ArrowRight':
        targetIndex = currentIndex === -1 ? 0 : (currentIndex + 1) % enabled.length;
        break;
      case 'ArrowLeft':
        targetIndex = currentIndex === -1 ? 0 : (currentIndex - 1 + enabled.length) % enabled.length;
        break;
      case 'Home':
        targetIndex = 0;
        break;
      case 'End':
        targetIndex = enabled.length - 1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const target = enabled[targetIndex];
    this.selectedId.set(target.id);
    this.focusTab(target.id);
  }

  private focusTab(id: string): void {
    const buttons = this.elementRef.nativeElement.querySelectorAll<HTMLButtonElement>('button[role="tab"]');
    for (const button of Array.from(buttons)) {
      if (button.dataset['tabId'] === id) { button.focus(); return; }
    }
  }
}

@Component({
  selector: 'cp-tab-panel', standalone: true,
  template: `@if(hasActivated()){<ng-content />}`,
  host: { 'role': 'tabpanel', '[attr.id]': "'panel-'+id()", '[attr.aria-labelledby]': "'tab-'+id()", '[attr.hidden]': "isHidden() ? '' : null", 'tabindex': '0' },
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CpTabPanelComponent {
  readonly id = input.required<string>();

  private readonly tabsHost = inject(CpTabsComponent);
  private readonly activated = signal(false);
  readonly hasActivated = this.activated.asReadonly();
  readonly isHidden = computed(() => !this.tabsHost.isSelected(this.id()));

  constructor() {
    effect(() => {
      if (this.tabsHost.isSelected(this.id())) this.activated.set(true);
    });
  }
}
