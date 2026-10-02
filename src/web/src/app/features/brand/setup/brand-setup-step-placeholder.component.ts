import { ChangeDetectionStrategy, Component, DestroyRef, Injector, afterNextRender, inject, input, output } from '@angular/core';

import { BrandSetupStepDefinition, BrandSetupStepReport } from '../../../models/brand-setup.models';

/**
 * The body of every step until its own prompt builds it. It honours the same contract a real body will:
 * the shell hands it the step and its saved slice (`draft`), and it reports `canContinue`, `isDirty` and a
 * draft slice through `reported`. The shell never looks inside the slice. A real step replaces this in the
 * shell's template and nothing else changes.
 *
 * It claims nothing it does not do: it says the step is coming and lets the creator move on.
 */
@Component({
  selector: 'cp-brand-setup-step-placeholder',
  standalone: true,
  template: `<p class="coming">{{ step().comingSoon }}</p>
    <p class="note">You can keep moving through setup. Nothing here is switched on.</p>`,
  styles: [
    `:host{display:grid;gap:var(--cp-space-2)}
    p{margin:0}
    .note{color:var(--cp-text-muted);font-size:var(--cp-font-size-sm)}`,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandSetupStepPlaceholderComponent {
  readonly step = input.required<BrandSetupStepDefinition>();
  /** This step's saved slice; the placeholder has no fields and ignores it. */
  readonly draft = input<Readonly<Record<string, unknown>> | null>(null);
  readonly reported = output<BrandSetupStepReport>();

  constructor() {
    const destroyRef = inject(DestroyRef);
    let destroyed = false;
    destroyRef.onDestroy(() => (destroyed = true));
    afterNextRender(
      () => {
        if (!destroyed) this.reported.emit({ canContinue: true, isDirty: false, draft: null });
      },
      { injector: inject(Injector) },
    );
  }
}
