import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { fieldErrors, problemMessage } from '../shared/api';
import { Shop, ShopsApi } from './shops-api';

/** FR-013: what deleting loses, said before the name is typed. */
export const DELETE_WARNING =
  "Deleting removes the shop, its address, all its items, orders and data, and its notification channel. This can't be undone.";

/**
 * "Delete shop" for one shop (FR-013): the owner types the shop's name exactly as shown; the
 * button stays disabled until it matches, and the API checks it again.
 */
@Component({
  selector: 'app-shop-delete',
  imports: [FormsModule],
  template: `
    @if (open()) {
      <form class="item-form danger-zone" (ngSubmit)="confirm()">
        <h3>Delete {{ shop().name }}</h3>
        <p>{{ warning }}</p>
        <label [for]="'delete-name-' + shop().id">Type <strong>{{ shop().name }}</strong> to confirm</label>
        <input [id]="'delete-name-' + shop().id" name="confirmName" [(ngModel)]="typed" autocomplete="off" spellcheck="false" />
        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <div class="actions">
          <button type="submit" class="danger" [disabled]="busy() || !matches()">Delete shop</button>
          <button type="button" class="secondary" (click)="close()" [disabled]="busy()">Cancel</button>
        </div>
      </form>
    } @else {
      <button type="button" class="secondary" (click)="start()">Delete shop</button>
    }
  `,
})
export class ShopDelete {
  private readonly api = inject(ShopsApi);

  readonly shop = input.required<Shop>();

  /** The shop as "being deleted". */
  readonly deleted = output<Shop>();

  protected readonly warning = DELETE_WARNING;
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected typed = '';

  protected start(): void {
    this.typed = '';
    this.error.set('');
    this.open.set(true);
  }

  protected close(): void {
    this.open.set(false);
  }

  /** The same rule as the API: trimmed, exact case. */
  protected matches(): boolean {
    return this.typed.trim() === this.shop().name;
  }

  protected async confirm(): Promise<void> {
    if (!this.matches()) {
      return;
    }
    this.busy.set(true);
    this.error.set('');
    try {
      const shop = await this.api.remove(this.shop().id, this.typed.trim());
      this.open.set(false);
      this.deleted.emit(shop);
    } catch (e) {
      this.error.set(fieldErrors(e)['confirmName'] ?? problemMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
