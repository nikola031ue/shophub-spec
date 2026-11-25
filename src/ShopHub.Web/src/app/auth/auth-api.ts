import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** `GET /api/session` (contracts/panel-api.md "Accounts"). */
export interface SessionInfo {
  signedIn: boolean;
  email?: string | null;
}

/**
 * Accounts API client and the panel's view of the session. Cookies are sent by the browser; the
 * X-XSRF-TOKEN header is added by `xsrfInterceptor`.
 */
@Injectable({ providedIn: 'root' })
export class AuthApi {
  private readonly http = inject(HttpClient);
  private readonly current = signal<string | null>(null);

  /** The signed-in owner's email, or null. */
  readonly email = this.current.asReadonly();
  readonly signedIn = computed(() => this.current() !== null);

  /** Asks the server who is signed in; this also (re)issues the XSRF cookie. */
  async refresh(): Promise<boolean> {
    const session = await firstValueFrom(this.http.get<SessionInfo>('/api/session'));
    this.current.set(session.signedIn ? (session.email ?? '') : null);
    return session.signedIn;
  }

  /** 202 whether or not the email already has an account (FR-001); the caller then signs in. */
  async register(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post('/api/register', { email, password }));
  }

  async signIn(email: string, password: string): Promise<void> {
    await firstValueFrom(this.http.post('/api/signin', { email, password }));
    await this.refresh();
  }

  async signOut(): Promise<void> {
    try {
      await firstValueFrom(this.http.post('/api/signout', null));
    } finally {
      this.current.set(null);
    }
  }

  /** The server refused the session (expired, signed out elsewhere, password changed elsewhere). */
  forget(): void {
    this.current.set(null);
  }
}

/** Only same-site paths ("/x", not "//host" or "/\host") are followed after sign-in. */
export function safeReturnUrl(url: string | null | undefined): string {
  return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/\\') ? url : '/';
}
