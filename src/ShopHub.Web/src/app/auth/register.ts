import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { fieldErrors } from '../shared/api';
import { AuthApi } from './auth-api';
import { registerMessage, signInAfterRegisterMessage } from './messages';

export const MIN_PASSWORD_LENGTH = 12;
export const MAX_PASSWORD_LENGTH = 200;

/**
 * Registration (FR-001). The API answers 202 whether or not the email is taken, so the page always
 * goes on to sign in with the same email and password. Only that sign-in tells success from
 * failure, and a failure there gets the neutral text of `REGISTER_THEN_SIGNIN_FAILED`.
 */
@Component({
  selector: 'app-register',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="card narrow">
      <h2>Register</h2>
      <form (ngSubmit)="submit()">
        <label for="email">Email</label>
        <input id="email" name="email" type="email" autocomplete="username"
               [(ngModel)]="email" required />
        @if (errors()['email']) {
          <p class="error">{{ errors()['email'] }}</p>
        }

        <label for="password">Password ({{ min }} to {{ max }} characters)</label>
        <input id="password" name="password" type="password" autocomplete="new-password"
               [(ngModel)]="password" required />
        @if (errors()['password']) {
          <p class="error">{{ errors()['password'] }}</p>
        }

        <label for="repeat">Repeat password</label>
        <input id="repeat" name="repeat" type="password" autocomplete="new-password"
               [(ngModel)]="repeat" required />

        @if (error()) {
          <p class="error" role="alert">{{ error() }}</p>
        }
        <button type="submit" [disabled]="busy() || !email || !password">Register</button>
      </form>
      <p class="muted">Have an account? <a routerLink="/signin">Sign in</a></p>
    </section>
  `,
})
export class Register {
  private readonly auth = inject(AuthApi);
  private readonly router = inject(Router);

  protected readonly min = MIN_PASSWORD_LENGTH;
  protected readonly max = MAX_PASSWORD_LENGTH;
  protected email = '';
  protected password = '';
  protected repeat = '';
  protected readonly busy = signal(false);
  protected readonly error = signal('');
  protected readonly errors = signal<Record<string, string>>({});

  protected async submit(): Promise<void> {
    this.error.set('');
    this.errors.set({});
    if (this.password.length < this.min || this.password.length > this.max) {
      this.errors.set({ password: `The password must have ${this.min} to ${this.max} characters.` });
      return;
    }
    if (this.password !== this.repeat) {
      this.error.set('The passwords do not match.');
      return;
    }

    const email = this.email.trim();
    this.busy.set(true);
    try {
      try {
        await this.auth.register(email, this.password);
      } catch (e) {
        this.errors.set(fieldErrors(e));
        if (Object.keys(this.errors()).length === 0) {
          this.error.set(registerMessage(e));
        }
        return;
      }

      try {
        await this.auth.signIn(email, this.password);
      } catch (e) {
        this.error.set(signInAfterRegisterMessage(e));
        return;
      }
      await this.router.navigateByUrl('/');
    } finally {
      this.password = this.repeat = '';
      this.busy.set(false);
    }
  }
}
