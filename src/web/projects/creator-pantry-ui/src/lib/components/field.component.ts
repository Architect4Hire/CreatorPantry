import { ChangeDetectionStrategy, Component, input } from '@angular/core';
@Component({
 selector:'cp-field', standalone:true,
 template:`<label [for]="forId()">{{label()}} @if(required()){<span aria-hidden="true">*</span>}</label><ng-content />@if(hint()){<small [id]="forId()+'-hint'">{{hint()}}</small>}@if(error()){<small class="error" role="alert">{{error()}}</small>}`,
 styles:[`:host{display:grid;gap:var(--cp-space-2)}label{font-size:var(--cp-font-size-sm);font-weight:700}label span,.error{color:var(--cp-danger)}small{font-size:var(--cp-font-size-xs);color:var(--cp-text-muted)}:host ::ng-deep input,:host ::ng-deep textarea,:host ::ng-deep select{width:100%;min-height:2.75rem;border:1px solid var(--cp-border);border-radius:var(--cp-radius-md);background:var(--cp-bg);color:var(--cp-text);padding:.7rem .8rem}:host ::ng-deep textarea{min-height:6rem;resize:vertical}:host ::ng-deep input:hover,:host ::ng-deep textarea:hover,:host ::ng-deep select:hover{border-color:var(--cp-border-strong)}:host ::ng-deep [aria-invalid='true']{border-color:var(--cp-danger)}`],
 changeDetection:ChangeDetectionStrategy.OnPush
}) export class CpFieldComponent { readonly label=input.required<string>(); readonly forId=input.required<string>(); readonly hint=input(''); readonly error=input(''); readonly required=input(false); }
