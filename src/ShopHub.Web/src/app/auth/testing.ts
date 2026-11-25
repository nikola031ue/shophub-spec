import { HttpHeaders } from '@angular/common/http';
import { HttpTestingController, TestRequest } from '@angular/common/http/testing';

// Test helpers for the auth specs (not part of the app build: only *.spec.ts import it).

/** Lets awaited promises and follow-up requests run. */
export const settle = () => new Promise((resolve) => setTimeout(resolve));

export function setXsrfCookie(value: string | null): void {
  document.cookie = value === null
    ? 'XSRF-TOKEN=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/'
    : `XSRF-TOKEN=${value}; path=/`;
}

/** The API's RFC 7807 answers, as the server sends them. */
export const problems = {
  wrongCredentials: (req: TestRequest) =>
    req.flush({ title: 'Wrong email or password.', status: 401 }, { status: 401, statusText: 'Unauthorized' }),
  locked: (req: TestRequest, retryAfterSeconds: number) =>
    req.flush(
      { title: 'Too many failed sign-ins. Try again later.', status: 429 },
      { status: 429, statusText: 'Too Many Requests', headers: new HttpHeaders({ 'Retry-After': String(retryAfterSeconds) }) },
    ),
  xsrfRejected: (req: TestRequest) =>
    req.flush({ title: 'Missing or invalid XSRF token.', status: 400 }, { status: 400, statusText: 'Bad Request' }),
};

/** Answers the next `GET /api/session`. */
export function answerSession(http: HttpTestingController, email: string | null): void {
  http.expectOne({ method: 'GET', url: '/api/session' }).flush(email === null ? { signedIn: false } : { signedIn: true, email });
}
