import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthApi } from './auth-api';

/** Owner pages: signed-in owners pass; everyone else goes to sign-in and comes back afterwards. */
export const ownerGuard: CanActivateFn = async (_route, state) => {
  const auth = inject(AuthApi);
  const router = inject(Router);
  try {
    if (await auth.refresh()) {
      return true;
    }
  } catch {
    // Unreachable API: treat as signed out; the sign-in page shows the error on submit.
  }
  return router.createUrlTree(['/signin'], { queryParams: { returnUrl: state.url } });
};

/** Sign-in and registration: a signed-in owner goes straight to the panel. */
export const guestGuard: CanActivateFn = async () => {
  const auth = inject(AuthApi);
  const router = inject(Router);
  try {
    return (await auth.refresh()) ? router.createUrlTree(['/']) : true;
  } catch {
    return true;
  }
};
