import { Component, computed, inject, input, signal } from '@angular/core';
import { problemMessage } from '../shared/api';
import { Shop, ShopsApi, conflictReason } from './shops-api';

/** FR-016: shown with the password; the shop itself asks for a new one at the first sign-in. */
export const FIRST_SIGNIN_HINT =
  'Sign in to the shop admin area with this password. At your first sign-in the shop asks you to choose your own password; after that, this one no longer works.';

export const PASSWORD_CHANGED =
  'The shop admin has already changed this password. Sign in with the password you chose.';

export const NOT_READY = "The shop isn't ready yet. Try again when it is.";

/**
 * "Show shop admin password" (FR-016): fetched only on an explicit click, kept only in this
 * component while shown, and dropped on "Hide".
 */
@Component({
  selector: 'app-admin-password',
  template: `
    @if (password()) {
      <div class="notice" role="status">
        <p>Shop admin password: <code class="mono secret">{{ password() }}</code></p>
        <p class="hint">{{ hint }}</p>
        <p>
          <a [href]="adminUrl()" target="_blank" rel="noopener noreferrer">Open the shop admin area</a>
        </p>
        <button type="button" class="secondary" (click)="hide()">Hide password</button>
      </div>
    } @else {
      <button type="button" class="secondary" (click)="show()" [disabled]="busy()">
        Show shop admin password
      </button>
    }
    @if (message()) {
      <p class="muted" role="status">{{ message() }}</p>
    }
  `,
})
export class AdminPassword {
  private readonly api = inject(ShopsApi);

  readonly shop = input.required<Shop>();

  protected readonly hint = FIRST_SIGNIN_HINT;
  protected readonly password = signal('');
  protected readonly busy = signal(false);
  protected readonly message = signal('');
  protected readonly adminUrl = computed(() => `${this.shop().url ?? ''}/admin/login`);

  protected async show(): Promise<void> {
    this.busy.set(true);
    this.message.set('');
    try {
      this.password.set(await this.api.adminPassword(this.shop().id));
    } catch (e) {
      const reason = conflictReason(e);
      this.message.set(
        reason === 'changed' ? PASSWORD_CHANGED : reason === 'notReady' ? NOT_READY : problemMessage(e),
      );
    } finally {
      this.busy.set(false);
    }
  }

  protected hide(): void {
    this.password.set('');
  }
}
