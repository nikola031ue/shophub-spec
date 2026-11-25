import { Component, InjectionToken, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { problemMessage } from '../shared/api';
import { AdminPassword } from './admin-password';
import { ShopCreate } from './shop-create';
import { ShopDelete } from './shop-delete';
import { ShopEdit } from './shop-edit';
import { AVAILABILITY_LABELS, DATABASE_LABELS, STATE_LABELS, Shop, ShopsApi } from './shops-api';

/** FR-008 asks for at least every 10 s while a shop is not ready; research R13 chose 5 s. */
export const REFRESH_INTERVAL_MS = new InjectionToken<number>('REFRESH_INTERVAL_MS', {
  factory: () => 5000,
});

/**
 * The owner's shops (FR-008) with "Change settings" (FR-012) and "Delete shop" (FR-013), and the
 * create form. A shop being deleted stays listed (and polled) until it is gone. While any shop is not ready the list reloads on
 * its own; once all are ready it stops. Storefront links open in a new tab without access to the
 * panel window (FR-010).
 */
@Component({
  selector: 'app-shop-list',
  imports: [AdminPassword, ShopCreate, ShopDelete, ShopEdit],
  template: `
    <section class="card">
      <h2>Your shops</h2>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      @if (loaded() && shops().length === 0) {
        <p class="muted">You have no shops yet. Create one below.</p>
      }
      <ul class="shops">
        @for (shop of shops(); track shop.id) {
          <li class="shop" [attr.data-shop]="shop.id">
            <div class="shop-head">
              @if (isOpenable(shop)) {
                <a class="shop-name" [href]="shop.url" target="_blank" rel="noopener noreferrer">{{ shop.name }}</a>
              } @else {
                <span class="shop-name">{{ shop.name }}</span>
              }
              <span class="state state-{{ shop.state }}">{{ stateLabels[shop.state] }}</span>
            </div>
            @if (shop.reason) {
              <p class="muted reason">{{ shop.reason }}</p>
            }
            <dl class="facts">
              <dt>Address</dt>
              <dd>
                @if (isOpenable(shop)) {
                  <a [href]="shop.url" target="_blank" rel="noopener noreferrer">{{ shop.url }}</a>
                } @else {
                  <span class="muted">{{ shop.url ?? 'Not known yet' }}</span>
                }
              </dd>
              <dt>Availability</dt>
              <dd>{{ availabilityLabels[shop.availability] }}</dd>
              <dt>Database</dt>
              <dd>{{ databaseLabels[shop.database] }}</dd>
              <dt>Wallet</dt>
              <dd class="mono">{{ shop.walletAddress }}</dd>
            </dl>
            @if (isOpenable(shop)) {
              <app-admin-password [shop]="shop" />
            }
            @if (shop.state !== 'deleting') {
              <app-shop-edit [shop]="shop" (saved)="reload()" />
              <app-shop-delete [shop]="shop" (deleted)="reload()" />
            }
          </li>
        }
      </ul>
    </section>
    <app-shop-create (created)="reload()" />
  `,
})
export class ShopList implements OnInit, OnDestroy {
  private readonly api = inject(ShopsApi);
  private readonly interval = inject(REFRESH_INTERVAL_MS);
  private timer: ReturnType<typeof setTimeout> | undefined;
  private destroyed = false;

  protected readonly stateLabels = STATE_LABELS;
  protected readonly availabilityLabels = AVAILABILITY_LABELS;
  protected readonly databaseLabels = DATABASE_LABELS;
  protected readonly shops = signal<Shop[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal('');

  ngOnInit(): void {
    void this.reload();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    clearTimeout(this.timer);
  }

  protected isOpenable(shop: Shop): boolean {
    return shop.state === 'ready' && !!shop.url;
  }

  /** Loads the list, then schedules the next load while any shop isn't ready (or loading failed). */
  async reload(): Promise<void> {
    clearTimeout(this.timer);
    try {
      this.shops.set(await this.api.list());
      this.error.set('');
    } catch (e) {
      this.error.set(problemMessage(e));
    } finally {
      this.loaded.set(true);
      this.schedule();
    }
  }

  private schedule(): void {
    clearTimeout(this.timer);
    if (this.destroyed) {
      return;
    }
    if (this.error() || this.shops().some((s) => s.state !== 'ready')) {
      this.timer = setTimeout(() => void this.reload(), this.interval);
    }
  }
}
