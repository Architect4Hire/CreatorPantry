import { ChangeDetectionStrategy, Component, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CpFactComponent, CpFactsComponent, CpFormSectionComponent } from '@creator-pantry/ui';

import { BrandProfileService } from '../../services/brand-profile.service';
import { BrandVisualGuideService } from '../../services/brand-visual-guide.service';

/** What the brand says about one thing: not read yet, read, or could not be read. */
type Read<T> =
  { readonly state: 'loading' } | { readonly state: 'read'; readonly value: T } | { readonly state: 'unavailable' };

interface Voice {
  readonly name: string;
  readonly versionNumber: number;
}

/**
 * What a long-form piece takes from the brand, shown and never asked (LF.6.2, B-38).
 *
 * **Read, not entered.** The voice is the workspace's active style guide and the audience is the brand
 * profile's; both are answered once, where the brand is kept, and a creator starting a piece is told what they
 * are rather than asked again. There is no control here: nothing a run does writes to the brand, and a
 * long-form request has no field that overrides either for one run.
 *
 * **Never a reason to stop.** A workspace with no guide or no profile still writes a piece; this says so in
 * place of the answer. A read that fails says that, and blocks nothing.
 */
@Component({
  selector: 'cp-long-form-brand-read',
  standalone: true,
  imports: [RouterLink, CpFactComponent, CpFactsComponent, CpFormSectionComponent],
  template: `
    <cp-form-section
      heading="From your brand"
      intro="These come from your brand and are not asked again here. Change them where your brand is kept."
      [sectionId]="sectionId()"
    >
      <dl cpFacts data-testid="brand-read">
        <div cpFact label="Voice">
          @switch (voice().state) {
            @case ('loading') {
              Reading your style guide…
            }
            @case ('unavailable') {
              Your style guide could not be read just now. The piece will still be written in it.
            }
            @case ('read') {
              @if (voiceValue(); as guide) {
                Written in the voice of {{ guide.name }}, version {{ guide.versionNumber }}.
              } @else {
                No style guide is active, so no brand voice is applied.
              }
            }
          }
          <a [routerLink]="['/', workspaceSlug(), 'brand']">Your brand</a>
        </div>

        <div cpFact label="Audience">
          @switch (audience().state) {
            @case ('loading') {
              Reading your brand profile…
            }
            @case ('unavailable') {
              Your brand profile could not be read just now.
            }
            @case ('read') {
              @if (audienceValue(); as readers) {
                Written for {{ readers }}.
              } @else {
                No audience is set in your brand profile.
              }
            }
          }
          <a [routerLink]="['/', workspaceSlug(), 'brand']">Brand profile</a>
        </div>
      </dl>
    </cp-form-section>
  `,
  styles: [':host { display: block; } a { display: block; }'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LongFormBrandReadComponent {
  private readonly profiles = inject(BrandProfileService);
  private readonly guides = inject(BrandVisualGuideService);

  readonly workspaceSlug = input.required<string>();
  readonly sectionId = input('cp-long-form-brand');

  protected readonly voice = signal<Read<Voice | null>>({ state: 'loading' });
  protected readonly audience = signal<Read<string | null>>({ state: 'loading' });

  protected voiceValue(): Voice | null {
    const voice = this.voice();

    return voice.state === 'read' ? voice.value : null;
  }

  protected audienceValue(): string | null {
    const audience = this.audience();

    return audience.state === 'read' ? audience.value : null;
  }

  constructor() {
    effect(() => {
      const slug = this.workspaceSlug();

      untracked(() => void this.read(slug));
    });
  }

  private async read(slug: string): Promise<void> {
    this.voice.set({ state: 'loading' });
    this.audience.set({ state: 'loading' });

    // The workspace has one active style guide, and it is the same guide whichever task asks: this read is
    // used only for which guide that is — its name and version — never for how a picture should look.
    const [guide, profile] = await Promise.all([
      this.guides.get(slug, 'photography-concept'),
      this.profiles.getBrandProfile(slug),
    ]);

    // Another workspace arrived while this was being read: the answer is for the one before.
    if (slug !== this.workspaceSlug()) return;

    this.voice.set(
      guide.status === 'found'
        ? {
            state: 'read',
            value:
              guide.guide.activeGuide === null
                ? null
                : { name: guide.guide.activeGuide.name, versionNumber: guide.guide.activeGuide.versionNumber },
          }
        : { state: 'unavailable' },
    );

    this.audience.set(
      profile.status === 'found'
        ? { state: 'read', value: profile.profile.defaultAudience?.trim() || null }
        : // A workspace with no profile has no audience set; only a failed read is "could not be read".
          profile.status === 'unavailable'
          ? { state: 'unavailable' }
          : { state: 'read', value: null },
    );
  }
}
