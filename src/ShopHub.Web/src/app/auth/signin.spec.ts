import { provideHttpClient, withInterceptors, withNoXsrfProtection } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { AuthApi, safeReturnUrl } from './auth-api';
import { xsrfInterceptor } from './interceptors';
import { WRONG_CREDENTIALS } from './messages';
import { SignIn } from './signin';
import { answerSession, problems, setXsrfCookie, settle } from './testing';

describe('SignIn', () => {
  let http: HttpTestingController;
  let router: Router;

  beforeEach(() => {
    setXsrfCookie('token');
    TestBed.configureTestingModule({
      imports: [SignIn],
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

  async function submit(returnUrl?: string) {
    const fixture = TestBed.createComponent(SignIn);
    if (returnUrl !== undefined) {
      fixture.componentRef.setInput('returnUrl', returnUrl);
    }
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    for (const [id, value] of [['email', 'owner@example.test'], ['password', 'some password!']]) {
      const input = el.querySelector<HTMLInputElement>(`#${id}`)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    }
    await fixture.whenStable();
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
    return fixture;
  }

  it('signs in, loads the session and follows a same-site return URL', async () => {
    await submit('/shops/s7k2m9qxa1');
    http.expectOne({ method: 'POST', url: '/api/signin' }).flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    answerSession(http, 'owner@example.test');
    await settle();

    expect(TestBed.inject(AuthApi).email()).toBe('owner@example.test');
    expect(router.navigateByUrl).toHaveBeenCalledWith('/shops/s7k2m9qxa1');
  });

  it('ignores a return URL to another site', async () => {
    await submit('//evil.example/x');
    http.expectOne('/api/signin').flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    answerSession(http, 'owner@example.test');
    await settle();

    expect(router.navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('a 401 shows the one text for wrong password and unknown email', async () => {
    const fixture = await submit();
    problems.wrongCredentials(http.expectOne('/api/signin'));
    await settle();
    await fixture.whenStable();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role=alert]')!.textContent!.trim()).toBe(WRONG_CREDENTIALS);
    expect(el.querySelector<HTMLInputElement>('#password')!.value).toBe('');
    expect(router.navigateByUrl).not.toHaveBeenCalled();
  });

  it('a lockout says how long to wait', async () => {
    const fixture = await submit();
    problems.locked(http.expectOne('/api/signin'), 60);
    await settle();
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Try again in 1 minute.');
  });
});

describe('safeReturnUrl', () => {
  it('keeps same-site paths only', () => {
    expect(safeReturnUrl('/shops')).toBe('/shops');
    expect(safeReturnUrl('//evil.example')).toBe('/');
    expect(safeReturnUrl('/\\evil.example')).toBe('/');
    expect(safeReturnUrl('https://evil.example')).toBe('/');
    expect(safeReturnUrl(undefined)).toBe('/');
  });
});
