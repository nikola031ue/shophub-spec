import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { SAVED, ShopEdit, WALLET_HINT } from './shop-edit';
import { Shop } from './shops-api';
import { shop, wait } from './testing';

const NEW_WALLET = '0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359';

describe('ShopEdit (FR-012)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ShopEdit],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function openForm(current: Shop = shop()) {
    const fixture = TestBed.createComponent(ShopEdit);
    fixture.componentRef.setInput('shop', current);
    const saved: Shop[] = [];
    fixture.componentInstance.saved.subscribe((s) => saved.push(s));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    const button = [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Change settings'))!;
    button.click();
    await fixture.whenStable();
    return { fixture, el, saved };
  }

  async function set(fixture: { whenStable(): Promise<unknown> }, el: HTMLElement, selector: string, value: string) {
    const input = el.querySelector<HTMLInputElement | HTMLSelectElement>(selector)!;
    input.value = value;
    input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
    await fixture.whenStable();
  }

  const saveButton = (el: HTMLElement) =>
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Save changes'))!;

  async function submit(fixture: { whenStable(): Promise<unknown> }, el: HTMLElement) {
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await wait(0);
  }

  it('shows name and database read-only, and the current availability and wallet to change', async () => {
    const { el } = await openForm(shop({ availability: 'high', database: 'light' }));

    expect(el.textContent).toContain("Name: Acme · Database: Light (Redis) (can't be changed)");
    expect(el.querySelector('input[name=name], select[name=database]')).toBeNull();
    expect(el.querySelector<HTMLSelectElement>('select[name=availability]')!.value).toBe('high');
    expect(el.querySelector<HTMLInputElement>('input[name=walletAddress]')!.value).toBe(shop().walletAddress);
    expect(el.textContent).toContain(WALLET_HINT);
  });

  it('an unchanged form cannot be saved', async () => {
    const { fixture, el } = await openForm();
    expect(saveButton(el).disabled).toBe(true);

    await set(fixture, el, 'input[name=walletAddress]', `  ${shop().walletAddress} `);
    expect(saveButton(el).disabled).toBe(true);

    await set(fixture, el, 'select[name=availability]', 'high');
    expect(saveButton(el).disabled).toBe(false);
  });

  it('sends only the availability when only it changed', async () => {
    const { fixture, el, saved } = await openForm();
    await set(fixture, el, 'select[name=availability]', 'high');
    await submit(fixture, el);

    const req = http.expectOne({ method: 'PATCH', url: '/api/shops/s7k2m4qxab' });
    expect(req.request.body).toEqual({ availability: 'high' });
    req.flush(shop({ availability: 'high', replicas: 3 }));
    await wait(0);
    await fixture.whenStable();

    expect(saved.map((s) => s.availability)).toEqual(['high']);
    expect(el.querySelector('form')).toBeNull();
    expect(el.querySelector('[role=status]')?.textContent).toContain(SAVED);
  });

  it('sends the trimmed new wallet address', async () => {
    const { fixture, el } = await openForm();
    await set(fixture, el, 'input[name=walletAddress]', ` ${NEW_WALLET} `);
    await submit(fixture, el);

    const req = http.expectOne({ method: 'PATCH', url: '/api/shops/s7k2m4qxab' });
    expect(req.request.body).toEqual({ walletAddress: NEW_WALLET });
    req.flush(shop({ walletAddress: NEW_WALLET }));
    await wait(0);
  });

  it('shows the API field errors next to their fields and keeps the form open', async () => {
    const { fixture, el, saved } = await openForm();
    await set(fixture, el, 'input[name=walletAddress]', '0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAEd');
    await submit(fixture, el);
    http.expectOne('/api/shops/s7k2m4qxab').flush(
      { title: 'One or more validation errors occurred.', errors: { walletAddress: ["This wallet address has a typo: its letter case doesn't match its checksum."] } },
      { status: 400, statusText: 'Bad Request' },
    );
    await wait(0);
    await fixture.whenStable();

    const walletError = el.querySelector('input[name=walletAddress]')!.parentElement!.textContent;
    expect(walletError).toContain('This wallet address has a typo');
    expect(el.querySelector('form')).not.toBeNull();
    expect(saved).toHaveLength(0);
  });

  it('a shop being deleted shows the message the API gives', async () => {
    const { fixture, el } = await openForm();
    await set(fixture, el, 'select[name=availability]', 'high');
    await submit(fixture, el);
    const title = "This shop is being deleted and can't be changed.";
    http.expectOne('/api/shops/s7k2m4qxab').flush({ title, reason: 'deleting' }, { status: 409, statusText: 'Conflict' });
    await wait(0);
    await fixture.whenStable();

    expect(el.querySelector('[role=alert]')?.textContent).toContain(title);
  });

  it('cancel closes the form without a request', async () => {
    const { fixture, el } = await openForm();
    await set(fixture, el, 'select[name=availability]', 'high');
    [...el.querySelectorAll('button')].find((b) => b.textContent?.includes('Cancel'))!.click();
    await fixture.whenStable();

    expect(el.querySelector('form')).toBeNull();
    http.expectNone('/api/shops/s7k2m4qxab');
  });
});
