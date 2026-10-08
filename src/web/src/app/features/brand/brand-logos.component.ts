import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { CpButtonComponent, CpDialogComponent, CpNoticeComponent } from '@creator-pantry/ui';

import {
  BRAND_LIMITS,
  BrandAsset,
  brandAlternateLogos,
  brandPrimaryLogo,
  withAlternateLogo,
  withPrimaryLogo,
  withoutLogo,
} from '../../models/brand-profile.models';
import { DamAssetSummary } from '../../models/dam-asset.models';
import { DamAssetPickerComponent } from '../dam/dam-asset-picker.component';
import { DamLinkedAssetComponent } from '../dam/dam-linked-asset.component';

/** Which logo the open picker is choosing, or null while it is closed. */
type Picking = 'primary' | 'alternate' | null;

const CHOOSE_PRIMARY = 'brand-choose-logo';
const ADD_ALTERNATE = 'brand-add-logo';

/**
 * The brand's logos: one primary and any alternates, each a picture already in the workspace's library
 * (12.10k).
 *
 * **Part of the brand settings form, not a command of its own.** Choosing or unlinking a logo changes the
 * form's draft and nothing else; the one Save writes it with everything else, and leaving without saving loses
 * it like any other edit. That is why unlinking asks no question here: nothing has happened until Save.
 *
 * **Links only.** A logo is chosen from the library through the same picker the recipe editor uses. Nothing
 * here uploads, edits or removes a picture, and unlinking one leaves it in the library untouched.
 *
 * **One primary logo.** Choosing another replaces it rather than keeping the old one as an alternate — a new
 * logo is not a request to keep the last one.
 */
@Component({
  selector: 'cp-brand-logos',
  standalone: true,
  imports: [CpButtonComponent, CpDialogComponent, CpNoticeComponent, DamAssetPickerComponent, DamLinkedAssetComponent],
  templateUrl: './brand-logos.component.html',
  styleUrl: './brand-logos.component.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class BrandLogosComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly injector = inject(Injector);

  readonly workspaceSlug = input.required<string>();

  /** The draft's logos, in the order they will be saved. */
  readonly assets = input.required<readonly BrandAsset[]>();

  /** Editor and above. Without it the logos are shown and nothing can be changed. */
  readonly canEdit = input(false);

  /** The logos the last save refused, by asset id, and what the server said about them. */
  readonly refusedIds = input<readonly string[]>([]);
  readonly refusedMessage = input('');

  /** The whole new list, for the form's draft. */
  readonly changed = output<readonly BrandAsset[]>();

  protected readonly ids = { choosePrimary: CHOOSE_PRIMARY, addAlternate: ADD_ALTERNATE };
  protected readonly maxLogos = BRAND_LIMITS.maxLogos;

  protected readonly primary = computed(() => brandPrimaryLogo(this.assets()));
  protected readonly alternates = computed(() => brandAlternateLogos(this.assets()));
  protected readonly atLimit = computed(() => this.assets().length >= BRAND_LIMITS.maxLogos);

  protected readonly picking = signal<Picking>(null);

  /** Said when a choice changed nothing, so the dialog closing is not the only answer the creator gets. */
  protected readonly note = signal('');

  protected readonly pickerTitle = computed(() =>
    this.picking() === 'alternate' ? 'Add another logo from the library' : 'Choose the primary logo from the library',
  );

  protected isRefused(asset: BrandAsset): boolean {
    return this.refusedIds().includes(asset.mediaAssetId);
  }

  protected open(which: Exclude<Picking, null>): void {
    if (!this.canEdit()) return;

    this.note.set('');
    this.picking.set(which);
  }

  protected close(): void {
    const was = this.picking();

    this.picking.set(null);
    this.focusAfterRender(was === 'alternate' ? ADD_ALTERNATE : CHOOSE_PRIMARY);
  }

  protected choose(asset: DamAssetSummary): void {
    const which = this.picking();
    if (which === null) return;

    const current = this.assets();
    const next = which === 'primary' ? withPrimaryLogo(current, asset.id) : withAlternateLogo(current, asset.id);

    this.picking.set(null);

    if (next === current) {
      // Each picture can be a logo once. Said rather than silently ignored.
      this.note.set(`${asset.title} is already one of this brand’s logos.`);
    } else {
      this.changed.emit(next);
    }

    this.focusAfterRender(which === 'alternate' ? ADD_ALTERNATE : CHOOSE_PRIMARY);
  }

  protected unlink(asset: BrandAsset): void {
    if (!this.canEdit()) return;

    this.note.set('');
    this.changed.emit(withoutLogo(this.assets(), asset.mediaAssetId));

    // The row and its button are gone; the action that would put a logo back is the nearest thing left.
    this.focusAfterRender(asset.role === 'PrimaryLogo' ? CHOOSE_PRIMARY : ADD_ALTERNATE);
  }

  /** Focus is moved after the next render, once the control it is going to exists and is enabled. */
  private focusAfterRender(id: string): void {
    afterNextRender(() => this.host.nativeElement.querySelector<HTMLElement>(`[id="${id}"]`)?.focus(), {
      injector: this.injector,
    });
  }
}
