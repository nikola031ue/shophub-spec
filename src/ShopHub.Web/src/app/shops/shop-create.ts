import { Component, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { fieldErrors, problemMessage } from '../shared/api';
import { AVAILABILITY_LABELS, DATABASE_LABELS, NewShop, Shop, ShopsApi } from './shops-api';

/**
 * Create form (FR-006, FR-011): field errors from the API, a duplicate name on the name field, and
 * the limit message.
 */
@Component({
  selector: 'app-shop-create',
  imports: [FormsModule],
  template: `
    <section class="card narrow">
      <h2>Create a shop</h2>
      <form (ngSubmit)="submit()">
        <label for="shop-name">Name</label>
        <input id="shop-name" name="name" [(ngModel)]="form.name" maxlength="100" required />
        @if (errors()['name']) {
          <p class="error">{{ errors()['name'] }}</p>
        }

        <label for="shop-availability">Availability</label>
        <select id="shop-availability" name="availability" [(ngModel)]="form.availability">
          <option value="standard">{{ availabilityLabels.standard }}</option>
          <option value="high">{{ availabilityLabels.high }}</option>
        </select>
        @if (errors()['availability']) {
          <p class="error">{{ errors()['availability'] }}</p>
        }

        <label for="shop-database">Database</label>
        <select id="shop-database" name="database" [(ngModel)]="form.database">
          <option value="standard">{{ databaseLabels.standard }}</option>
          <option value="light">{{ databaseLabels.light }}</option>
        </select>
        @if (errors()['database']) {
          <p class="error">{{ errors()['database'] }}</p>
        }

        <label for="shop-wallet">Wallet address for customer payments</label>
        <input id="shop-wallet" name="walletAddress" class="mono" [(ngModel)]="form.walletAddress"
               placeholder="0x…" autocomplete="off" spellcheck="false" required />
        @if (errors()['walletAddress']) {
          <p class="error">{{ errors()['walletAddress'] }}</p>
        }

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="busy() || !form.name.trim() || !form.walletAddress.trim()">
          Create shop
        </button>
      </form>
    </section>
  `,
})
export class ShopCreate {
  private readonly api = inject(ShopsApi);

  /** The new shop (or the existing one, for a repeated create). */
  readonly created = output<Shop>();

  protected readonly availabilityLabels = AVAILABILITY_LABELS;
  protected readonly databaseLabels = DATABASE_LABELS;
  protected form: NewShop = ShopCreate.empty();
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly errors = signal<Record<string, string>>({});

  private static empty(): NewShop {
    return { name: '', availability: 'standard', database: 'standard', walletAddress: '' };
  }

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    this.errors.set({});
    try {
      const shop = await this.api.create({
        ...this.form,
        name: this.form.name.trim(),
        walletAddress: this.form.walletAddress.trim(),
      });
      this.form = ShopCreate.empty();
      this.created.emit(shop);
    } catch (e) {
      // 400 validation and 409 duplicateName carry field errors; 409 limit and others a title.
      const fields = fieldErrors(e);
      this.errors.set(fields);
      if (Object.keys(fields).length === 0) {
        this.error.set(problemMessage(e));
      }
    } finally {
      this.busy.set(false);
    }
  }
}
