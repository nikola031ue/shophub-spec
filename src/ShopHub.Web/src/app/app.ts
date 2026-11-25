import { Component, inject } from '@angular/core';
import { Router, RouterOutlet } from '@angular/router';
import { AuthApi } from './auth/auth-api';

@Component({
  imports: [RouterOutlet],
  selector: 'app-root',
  styleUrl: './app.css',
  templateUrl: './app.html',
})
export class App {
  protected readonly auth = inject(AuthApi);
  private readonly router = inject(Router);

  protected async signOut(): Promise<void> {
    try {
      await this.auth.signOut();
    } finally {
      await this.router.navigateByUrl('/signin');
    }
  }
}
