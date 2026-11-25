import { Component, inject } from '@angular/core';
import { AuthApi } from './auth/auth-api';

/** Owner start page; the shop list replaces it with US3. */
@Component({
  selector: 'app-home',
  template: `
    <section class="card">
      <h2>Your shops</h2>
      <p>Signed in as <strong>{{ auth.email() }}</strong>. Your shops will appear here.</p>
    </section>
  `,
})
export class Home {
  protected readonly auth = inject(AuthApi);
}
