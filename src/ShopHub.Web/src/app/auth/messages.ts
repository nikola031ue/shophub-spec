import { HttpErrorResponse } from '@angular/common/http';
import { problemMessage, retryMinutes } from '../shared/api';

/** Sign-in 401: the same for a wrong password and an unknown email (FR-001). */
export const WRONG_CREDENTIALS = 'Wrong email or password.';

/**
 * Registration answered 202, but signing in with the same email and password then got 401. The
 * API answers a new and a taken email alike, so the panel must not say more than that: the text is
 * conditional and never states that the email is taken or that an account exists.
 */
export const REGISTER_THEN_SIGNIN_FAILED =
  'If you already have an account, sign in with its password.';

export const TOO_MANY_REGISTRATIONS =
  'Too many registrations from your network. Try again in an hour.';

function lockedMessage(error: HttpErrorResponse): string {
  const minutes = retryMinutes(error);
  return `Too many failed sign-ins. Try again in ${minutes} minute${minutes === 1 ? '' : 's'}.`;
}

/** Message for a failed `POST /api/signin` from the sign-in page. */
export function signInMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401) {
      return WRONG_CREDENTIALS;
    }
    if (error.status === 429) {
      return lockedMessage(error);
    }
  }
  return problemMessage(error);
}

/** Message for a failed `POST /api/register` (validation errors are shown per field instead). */
export function registerMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse && error.status === 429) {
    return TOO_MANY_REGISTRATIONS;
  }
  return problemMessage(error);
}

/** Message for a failed sign-in right after a 202 registration. */
export function signInAfterRegisterMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 401) {
      return REGISTER_THEN_SIGNIN_FAILED;
    }
    if (error.status === 429) {
      return lockedMessage(error);
    }
  }
  return problemMessage(error);
}
