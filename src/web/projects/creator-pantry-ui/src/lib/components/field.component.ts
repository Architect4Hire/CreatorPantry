import { ChangeDetectionStrategy, Component, ElementRef, afterRenderEffect, computed, inject, input } from '@angular/core';
/**
 * The label, hint and error of one field, around a control the consumer projects.
 *
 * The control is projected rather than rendered here, so no template binding can reach it — which is why the
 * hint, the error and the required state are wired onto it from {@link constructor} after each render instead.
 * Without that the `*` and the hint are decoration: visible, and invisible to everyone who cannot see them.
 */
@Component({
 selector:'cp-field', standalone:true,
 template:`<label [for]="forId()">{{label()}} @if(required()){<span aria-hidden="true">*</span>}</label><ng-content />@if(hint()){<small [id]="hintId()">{{hint()}}</small>}@if(error()){<small class="error" [id]="errorId()" role="alert">{{error()}}</small>}`,
 styles:[`:host{display:grid;gap:var(--cp-space-2)}label{font-size:var(--cp-font-size-sm);font-weight:700}label span,.error{color:var(--cp-danger)}small{font-size:var(--cp-font-size-xs);color:var(--cp-text-muted)}:host ::ng-deep input,:host ::ng-deep textarea,:host ::ng-deep select{box-sizing:border-box;width:100%;min-height:2.75rem;border:1px solid var(--cp-border);border-radius:var(--cp-radius-md);background:var(--cp-bg);color:var(--cp-text);padding:.7rem .8rem}:host ::ng-deep textarea{min-height:6rem;resize:vertical}:host ::ng-deep input:hover,:host ::ng-deep textarea:hover,:host ::ng-deep select:hover{border-color:var(--cp-border-strong)}:host ::ng-deep [aria-invalid='true']{border-color:var(--cp-danger)}`],
 changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpFieldComponent {
  readonly label=input.required<string>();
  readonly forId=input.required<string>();
  readonly hint=input('');
  readonly error=input('');
  readonly required=input(false);

  protected readonly hintId=computed(()=>`${this.forId()}-hint`);
  protected readonly errorId=computed(()=>`${this.forId()}-error`);

  private readonly elementRef:ElementRef<HTMLElement>=inject(ElementRef);

  constructor() {
    afterRenderEffect(() => {
      // An attribute selector, not `#id`: these ids carry uuids and other spellings a CSS id selector would
      // have to escape. It finds the control wherever it sits, including one nested inside another component
      // — a combobox's own input takes this id, and is the thing a screen reader lands on.
      const control = this.elementRef.nativeElement.querySelector(`[id="${this.forId()}"]`);
      if (control === null) return;

      // Merged rather than assigned. A consumer may describe its own control by something this field knows
      // nothing about — a banner explaining why the control is disabled, say — and overwriting that would
      // trade one missing announcement for another. Only the two ids this field owns are replaced, which is
      // also what makes running again after every render idempotent.
      const owned = new Set([this.hintId(), this.errorId()]);
      const theirs = (control.getAttribute('aria-describedby') ?? '')
        .split(/\s+/)
        .filter((id) => id.length > 0 && !owned.has(id));
      const describedBy = [
        ...theirs,
        ...(this.hint() ? [this.hintId()] : []),
        ...(this.error() ? [this.errorId()] : []),
      ];

      if (describedBy.length === 0) control.removeAttribute('aria-describedby');
      else control.setAttribute('aria-describedby', describedBy.join(' '));

      // `aria-required`, never the native `required`: this states a fact for assistive technology and leaves
      // browser validation UI — and the moment it fires — to the consumer, whose form owns that decision.
      if (this.required()) control.setAttribute('aria-required', 'true');
      else control.removeAttribute('aria-required');
    });
  }
}
