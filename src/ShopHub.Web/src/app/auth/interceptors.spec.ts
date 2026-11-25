import { HttpClient, provideHttpClient, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthApi } from './auth-api';
import { sessionEndedInterceptor, xsrfInterceptor } from './interceptors';
import { answerSession, problems, setXsrfCookie, settle } from './testing';

describe('xsrfInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withNoXsrfProtection(), withInterceptors([xsrfInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
  });

  afterEach(() => {
    http.verify();
    setXsrfCookie(null);
  });

  it('sends the cookie value as X-XSRF-TOKEN on API writes only', async () => {
    setXsrfCookie('abc%3D');
    void firstValueFrom(client.post('/api/signin', {}));
    void firstValueFrom(client.get('/api/session'));
    void firstValueFrom(client.post('https://elsewhere.example/api/x', {}));
    await settle();

    expect(http.expectOne('/api/signin').request.headers.get('X-XSRF-TOKEN')).toBe('abc=');
    expect(http.expectOne('/api/session').request.headers.has('X-XSRF-TOKEN')).toBe(false);
    expect(http.expectOne('https://elsewhere.example/api/x').request.headers.has('X-XSRF-TOKEN')).toBe(false);
  });

  it('fetches the token first when the cookie is missing', async () => {
    setXsrfCookie(null);
    const done = firstValueFrom(client.post('/api/register', {}));
    await settle();

    http.expectNone('/api/register');
    setXsrfCookie('fresh'); // what the browser does with the session answer's Set-Cookie
    answerSession(http, null);
    await settle();

    const req = http.expectOne('/api/register');
    expect(req.request.headers.get('X-XSRF-TOKEN')).toBe('fresh');
    req.flush(null, { status: 202, statusText: 'Accepted' });
    await done;
  });

  it('on a stale token fetches a new one and resends once', async () => {
    setXsrfCookie('stale');
    const done = firstValueFrom(client.post('/api/signin', {}));
    await settle();

    problems.xsrfRejected(http.expectOne('/api/signin'));
    await settle();
    setXsrfCookie('fresh');
    answerSession(http, null);
    await settle();

    const retry = http.expectOne('/api/signin');
    expect(retry.request.headers.get('X-XSRF-TOKEN')).toBe('fresh');
    retry.flush(null, { status: 204, statusText: 'No Content' });
    await done;
  });

  it('does not resend a second time', async () => {
    setXsrfCookie('stale');
    const done = firstValueFrom(client.post('/api/signin', {})).catch((e: unknown) => e);
    await settle();
    problems.xsrfRejected(http.expectOne('/api/signin'));
    await settle();
    answerSession(http, null);
    await settle();
    problems.xsrfRejected(http.expectOne('/api/signin'));

    expect(await done).toMatchObject({ status: 400 });
  });

  it('passes other errors through without resending', async () => {
    setXsrfCookie('token');
    const done = firstValueFrom(client.post('/api/signin', {})).catch((e: unknown) => e);
    await settle();
    problems.wrongCredentials(http.expectOne('/api/signin'));

    expect(await done).toMatchObject({ status: 401 });
  });
});

describe('sessionEndedInterceptor', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let router: Router;
  let auth: AuthApi;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withNoXsrfProtection(), withInterceptors([sessionEndedInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    router = TestBed.inject(Router);
    auth = TestBed.inject(AuthApi);
    vi.spyOn(router, 'navigate').mockResolvedValue(true);
    vi.spyOn(auth, 'forget');
  });

  afterEach(() => http.verify());

  it('a 401 on an owner call forgets the session and goes to sign-in', async () => {
    const done = firstValueFrom(client.get('/api/shops')).catch((e: unknown) => e);
    http.expectOne('/api/shops').flush(null, { status: 401, statusText: 'Unauthorized' });
    await done;

    expect(auth.forget).toHaveBeenCalled();
    expect(router.navigate).toHaveBeenCalledWith(['/signin'], { queryParams: { returnUrl: '/' } });
  });

  it('a 401 from sign-in or password change is a wrong password, not a lost session', async () => {
    for (const url of ['/api/signin', '/api/password']) {
      const done = firstValueFrom(client.post(url, {})).catch((e: unknown) => e);
      problems.wrongCredentials(http.expectOne(url));
      await done;
    }

    expect(auth.forget).not.toHaveBeenCalled();
    expect(router.navigate).not.toHaveBeenCalled();
  });
});
