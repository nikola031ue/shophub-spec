import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthApi, safeReturnUrl } from './auth-api';
import { signInMessage } from './messages';

@Component({
  selector: 'app-signin',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="card narrow">
      <h2>Sign in</h2>
      <form (ngSubmit)="submit()">
        <label for="email">Email</label>
        <input id="email" name="email" type="email" autocomplete="username"
               [(ngModel)]="email" required />

        <label for="password">Password</label>
        <input id="password" name="password" type="password" autocomplete="current-password"
               [(ngModel)]="password" required />

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="busy() || !email || !password">Sign in</button>
      </form>
      <p class="muted">No account yet? <a routerLink="/register">Register</a></p>
    </section>
  `,
})
export class SignIn {
  private readonly auth = inject(AuthApi);
  private readonly router = inject(Router);

  /** Query parameter: where to go after signing in (same-site paths only). */
  readonly returnUrl = input<string>();

  protected email = '';
  protected password = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');

  protected async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try {
      await this.auth.signIn(this.email.trim(), this.password);
      await this.router.navigateByUrl(safeReturnUrl(this.returnUrl()));
    } catch (e) {
      this.error.set(signInMessage(e));
    } finally {
      this.password = '';
      this.busy.set(false);
    }
  }
}
