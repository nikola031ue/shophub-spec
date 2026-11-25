import { Component, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { fieldErrors, problemMessage } from '../shared/api';
import { AVAILABILITY_LABELS, DATABASE_LABELS, Shop, ShopChange, ShopsApi } from './shops-api';

/** FR-012: what a new wallet address changes, said before saving. */
export const WALLET_HINT =
  'Orders placed after the change are paid to the new address. Orders placed before keep the address they were placed with.';

export const SAVED = 'Saved. The shop picks up the change within a minute or two.';

/**
 * "Change settings" for one shop (FR-012): availability and wallet address can change; name and
 * database are shown but can't. Only changed fields are sent, so an unchanged form can't be saved.
 */
@Component({
  selector: 'app-shop-edit',
  imports: [FormsModule],
  template: `
    @if (open()) {
      <form class="item-form" (ngSubmit)="save()">
        <h3>Change settings</h3>
        <p class="muted">Name: {{ shop().name }} · Database: {{ databaseLabels[shop().database] }} (can't be changed)</p>

        <label [for]="'edit-availability-' + shop().id">Availability</label>
        <select [id]="'edit-availability-' + shop().id" name="availability" [(ngModel)]="availability">
          <option value="standard">{{ availabilityLabels.standard }}</option>
          <option value="high">{{ availabilityLabels.high }}</option>
        </select>
        @if (errors()['availability']) {
          <p class="error">{{ errors()['availability'] }}</p>
        }

        <label [for]="'edit-wallet-' + shop().id">Wallet address for customer payments</label>
        <input [id]="'edit-wallet-' + shop().id" name="walletAddress" class="mono" [(ngModel)]="walletAddress"
               autocomplete="off" spellcheck="false" required />
        <p class="muted hint">{{ walletHint }}</p>
        @if (errors()['walletAddress']) {
          <p class="error">{{ errors()['walletAddress'] }}</p>
        }

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <div class="actions">
          <button type="submit" [disabled]="busy() || !changes()">Save changes</button>
          <button type="button" class="secondary" (click)="close()" [disabled]="busy()">Cancel</button>
        </div>
      </form>
    } @else {
      <button type="button" class="secondary" (click)="start()">Change settings</button>
    }
    @if (message()) {
      <p class="success" role="status">{{ message() }}</p>
    }
  `,
})
export class ShopEdit {
  private readonly api = inject(ShopsApi);

  readonly shop = input.required<Shop>();

  /** The shop as saved. */
  readonly saved = output<Shop>();

  protected readonly availabilityLabels = AVAILABILITY_LABELS;
  protected readonly databaseLabels = DATABASE_LABELS;
  protected readonly walletHint = WALLET_HINT;
  protected readonly open = signal(false);
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly errors = signal<Record<string, string>>({});
  protected readonly message = signal('');
  protected availability: Shop['availability'] = 'standard';
  protected walletAddress = '';

  protected start(): void {
    this.availability = this.shop().availability;
    this.walletAddress = this.shop().walletAddress;
    this.error.set('');
    this.errors.set({});
    this.message.set('');
    this.open.set(true);
  }

  protected close(): void {
    this.open.set(false);
  }

  /** The fields that differ from the shop as shown, or null when nothing does. */
  protected changes(): ShopChange | null {
    const change: ShopChange = {};
    if (this.availability !== this.shop().availability) {
      change.availability = this.availability;
    }
    const wallet = this.walletAddress.trim();
    if (wallet !== this.shop().walletAddress) {
      change.walletAddress = wallet;
    }
    return Object.keys(change).length > 0 ? change : null;
  }

  protected async save(): Promise<void> {
    const change = this.changes();
    if (!change) {
      return;
    }
    this.busy.set(true);
    this.error.set('');
    this.errors.set({});
    try {
      const shop = await this.api.update(this.shop().id, change);
      this.open.set(false);
      this.message.set(SAVED);
      this.saved.emit(shop);
    } catch (e) {
      // 400 carries field errors; 409 (being deleted), 404 and others a title.
      const fields = fieldErrors(e);
      delete fields[''];
      this.errors.set(fields);
      if (Object.keys(fields).length === 0) {
        this.error.set(problemMessage(e));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
