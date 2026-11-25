import { provideHttpClient, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { xsrfInterceptor } from './interceptors';
import { REGISTER_THEN_SIGNIN_FAILED, TOO_MANY_REGISTRATIONS, WRONG_CREDENTIALS } from './messages';
import { Register } from './register';
import { answerSession, problems, setXsrfCookie, settle } from './testing';

const EMAIL = 'owner@example.test';
const PASSWORD = 'correct horse battery';

describe('Register (FR-001)', () => {
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    setXsrfCookie('token');
    TestBed.configureTestingModule({
      imports: [Register],
      providers: [
        provideRouter([]),
        provideHttpClient(withNoXsrfProtection(), withInterceptors([xsrfInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
  });

  afterEach(() => {
    http.verify();
    setXsrfCookie(null);
  });

  async function render() {
    const fixture = TestBed.createComponent(Register);
    await fixture.whenStable();
    return fixture;
  }

  async function fill(fixture: Awaited<ReturnType<typeof render>>, email: string, password: string, repeat = password) {
    const el = fixture.nativeElement as HTMLElement;
    for (const [id, value] of [['email', email], ['password', password], ['repeat', repeat]]) {
      const input = el.querySelector<HTMLInputElement>(`#${id}`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await fixture.whenStable();
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  const text = (fixture: Awaited<ReturnType<typeof render>>) => (fixture.nativeElement as HTMLElement).textContent ?? '';

  it('after a 202 signs in with the same email and password, then opens the panel', async () => {
    const fixture = await render();
    await fill(fixture, `  ${EMAIL} `, PASSWORD);

    const register = http.expectOne({ method: 'POST', url: '/api/register' });
    expect(register.request.body).toEqual({ email: EMAIL, password: PASSWORD });
    expect(register.request.headers.get('X-XSRF-TOKEN')).toBe('token');
    register.flush(null, { status: 202, statusText: 'Accepted' });
    await settle();

    const signIn = http.expectOne({ method: 'POST', url: '/api/signin' });
    expect(signIn.request.body).toEqual({ email: EMAIL, password: PASSWORD });
    signIn.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    answerSession(http, EMAIL);
    await settle();

    expect(router.navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('a 401 after the 202 shows only the neutral text, never that the email is taken', async () => {
    const fixture = await render();
    await fill(fixture, EMAIL, PASSWORD);

    http.expectOne('/api/register').flush(null, { status: 202, statusText: 'Accepted' });
    await settle();
    problems.wrongCredentials(http.expectOne('/api/signin'));
    await settle();
    await fixture.whenStable();

    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role=alert]')!;
    expect(alert.textContent!.trim()).toBe(REGISTER_THEN_SIGNIN_FAILED);
    // Nothing beyond the API's answer: no claim that an account exists, no echo of the email.
    const page = text(fixture).toLowerCase();
    for (const leak of ['taken', 'in use', 'already exists', 'already registered', 'exists', WRONG_CREDENTIALS.toLowerCase()]) {
      expect(page).not.toContain(leak);
    }
    expect(alert.textContent).not.toContain(EMAIL);
    // Stays on the page, passwords cleared, no navigation.
    expect(router.navigateByUrl).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>('#password')!.value).toBe('');
  });

  it('the same 202 then 401 always gives the same page, whatever the email', async () => {
    const pages: string[] = [];
    for (const email of ['new@example.test', 'Taken@Example.test']) {
      const fixture = await render();
      await fill(fixture, email, PASSWORD);
      http.expectOne('/api/register').flush(null, { status: 202, statusText: 'Accepted' });
      await settle();
      problems.wrongCredentials(http.expectOne('/api/signin'));
      await settle();
      await fixture.whenStable();
      pages.push(text(fixture).replace(email, ''));
      fixture.destroy();
    }
    expect(pages[0]).toBe(pages[1]);
  });

  it('a lockout after the 202 says when to retry', async () => {
    const fixture = await render();
    await fill(fixture, EMAIL, PASSWORD);
    http.expectOne('/api/register').flush(null, { status: 202, statusText: 'Accepted' });
    await settle();
    problems.locked(http.expectOne('/api/signin'), 600);
    await settle();
    await fixture.whenStable();

    expect(text(fixture)).toContain('Too many failed sign-ins. Try again in 10 minutes.');
  });

  it('shows the API validation errors per field and does not sign in', async () => {
    const fixture = await render();
    await fill(fixture, 'a@b@c', PASSWORD);
    http.expectOne('/api/register').flush(
      { title: 'One or more validation errors occurred.', errors: { email: ['Enter an email address with one @ and at most 254 characters.'] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle();
    await fixture.whenStable();

    expect(text(fixture)).toContain('Enter an email address with one @');
    http.expectNone('/api/signin');
  });

  it('the registration rate limit has its own text and does not sign in', async () => {
    const fixture = await render();
    await fill(fixture, EMAIL, PASSWORD);
    http.expectOne('/api/register').flush({ status: 429 }, { status: 429, statusText: 'Too Many Requests' });
    await settle();
    await fixture.whenStable();

    expect(text(fixture)).toContain(TOO_MANY_REGISTRATIONS);
    http.expectNone('/api/signin');
  });

  it('checks length and repeat before calling the API', async () => {
    const fixture = await render();
    await fill(fixture, EMAIL, 'short');
    await fixture.whenStable();
    expect(text(fixture)).toContain('The password must have 12 to 200 characters.');

    await fill(fixture, EMAIL, PASSWORD, `${PASSWORD}!`);
    await fixture.whenStable();
    expect(text(fixture)).toContain('The passwords do not match.');
    http.expectNone('/api/register');
  });
});
