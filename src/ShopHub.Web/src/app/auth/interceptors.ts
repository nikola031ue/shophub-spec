import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
  HttpResponse,
} from '@angular/common/http';
import { DOCUMENT, inject } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, catchError, concatMap, defer, filter, of, take, throwError } from 'rxjs';
import { AuthApi } from './auth-api';

export const XSRF_COOKIE = 'XSRF-TOKEN';
export const XSRF_HEADER = 'X-XSRF-TOKEN';
/** Title of the API's 400 for a missing or stale token (Auth/Sessions.cs `RequireXsrf`). */
export const XSRF_REJECTED_TITLE = 'Missing or invalid XSRF token.';

function isApiWrite(req: HttpRequest<unknown>): boolean {
  return req.url.startsWith('/api/') && !['GET', 'HEAD', 'OPTIONS'].includes(req.method);
}

function readCookie(doc: Document, name: string): string | null {
  for (const part of doc.cookie.split(';')) {
    const [key, ...value] = part.trim().split('=');
    if (key === name) {
      return decodeURIComponent(value.join('='));
    }
  }
  return null;
}

/** `GET /api/session` through the rest of the chain: it (re)issues the XSRF cookie. */
function fetchXsrfCookie(next: HttpHandlerFn): Observable<unknown> {
  return next(new HttpRequest('GET', '/api/session')).pipe(
    filter((e) => e instanceof HttpResponse),
    take(1),
  );
}

/**
 * Sends the XSRF token on every state-changing `/api` request (contracts/panel-api.md). Used
 * instead of Angular's built-in one, which only reads the cookie, because the token can be missing
 * or stale here:
 * - missing: no page has called `/api/session` yet, or sign-out deleted the cookie. The
 *   interceptor fetches it first.
 * - stale: the token is bound to the signed-in user, so it no longer matches once the server ends
 *   the session (expiry, password change elsewhere). On the API's XSRF 400 the interceptor fetches
 *   a fresh token and resends once.
 */
export const xsrfInterceptor: HttpInterceptorFn = (req, next) => {
  if (!isApiWrite(req)) {
    return next(req);
  }

  const doc = inject(DOCUMENT);
  const send = (): Observable<HttpEvent<unknown>> => {
    const token = readCookie(doc, XSRF_COOKIE);
    return next(token ? req.clone({ setHeaders: { [XSRF_HEADER]: token } }) : req);
  };
  const ensureCookie = defer(() => (readCookie(doc, XSRF_COOKIE) ? of(null) : fetchXsrfCookie(next)));

  return ensureCookie.pipe(
    concatMap(send),
    catchError((error: unknown) =>
      error instanceof HttpErrorResponse && error.status === 400 && error.error?.title === XSRF_REJECTED_TITLE
        ? fetchXsrfCookie(next).pipe(concatMap(send))
        : throwError(() => error),
    ),
  );
};

/** 401s that mean "wrong password", not "session gone". */
const PASSWORD_CHECKS = ['/api/signin', '/api/password'];

/**
 * A 401 on any other `/api` call means the session ended on the server: the panel forgets it and
 * goes to the sign-in page, coming back to the current page afterwards.
 */
export const sessionEndedInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthApi);
  const router = inject(Router);
  return next(req).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        req.url.startsWith('/api/') &&
        !PASSWORD_CHECKS.includes(req.url)
      ) {
        auth.forget();
        const returnUrl = router.url.startsWith('/signin') ? undefined : router.url;
        void router.navigate(['/signin'], { queryParams: returnUrl ? { returnUrl } : {} });
      }
      return throwError(() => error);
    }),
  );
};
