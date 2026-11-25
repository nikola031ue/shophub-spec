import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ShopCreate } from './shop-create';
import { Shop } from './shops-api';
import { shop, wait } from './testing';

describe('ShopCreate (FR-006, FR-011)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [ShopCreate],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  async function submit(values: Record<string, string>) {
    const fixture = TestBed.createComponent(ShopCreate);
    const created: Shop[] = [];
    fixture.componentInstance.created.subscribe((s) => created.push(s));
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;
    for (const [id, value] of Object.entries(values)) {
      const input = el.querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`)!;
      input.value = value;
      input.dispatchEvent(new Event(input instanceof HTMLSelectElement ? 'change' : 'input'));
    }
    await fixture.whenStable();
    el.querySelector('form')!.dispatchEvent(new Event('submit'));
    await wait(0);
    return { fixture, el, created };
  }

  const valid = {
    'shop-name': '  Acme  ',
    'shop-availability': 'high',
    'shop-database': 'light',
    'shop-wallet': ' 0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed ',
  };

  it('sends the trimmed form and reports the created shop', async () => {
    const { fixture, el, created } = await submit(valid);

    const req = http.expectOne({ method: 'POST', url: '/api/shops' });
    expect(req.request.body).toEqual({
      name: 'Acme',
      availability: 'high',
      database: 'light',
      walletAddress: '0x5aaeb6053f3e94c9b9a09f33669435e7ef1beaed',
    });
    req.flush(shop(), { status: 201, statusText: 'Created' });
    await wait(0);
    await fixture.whenStable();

    expect(created.map((s) => s.id)).toEqual(['s7k2m4qxab']);
    expect(el.querySelector<HTMLInputElement>('#shop-name')!.value).toBe('');
  });

  it('a repeated create (200 with the existing shop) counts as created', async () => {
    const { created } = await submit(valid);
    http.expectOne('/api/shops').flush(shop(), { status: 200, statusText: 'OK' });
    await wait(0);
    expect(created).toHaveLength(1);
  });

  it('shows the API field errors next to their fields', async () => {
    const { fixture, el, created } = await submit(valid);
    http.expectOne('/api/shops').flush(
      {
        title: 'One or more validation errors occurred.',
        errors: {
          walletAddress: ["This wallet address has a typo: its letter case doesn't match its checksum."],
          availability: ['Choose standard or high availability.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await wait(0);
    await fixture.whenStable();

    expect(el.textContent).toContain('This wallet address has a typo');
    expect(el.textContent).toContain('Choose standard or high availability.');
    expect(created).toHaveLength(0);
  });

  it('a duplicate name is shown on the name field', async () => {
    const { fixture, el } = await submit(valid);
    http.expectOne('/api/shops').flush(
      { title: 'You already have a shop with this name.', reason: 'duplicateName', errors: { name: ['You already have a shop with this name.'] } },
      { status: 409, statusText: 'Conflict' },
    );
    await wait(0);
    await fixture.whenStable();

    const nameError = el.querySelector('#shop-name')!.nextElementSibling;
    expect(nameError?.textContent).toContain('You already have a shop with this name.');
  });

  it('the limit is shown as the message the API gives', async () => {
    const { fixture, el } = await submit(valid);
    const limit = 'You already have 5 shops, the most an owner can have. Delete one to create another.';
    http.expectOne('/api/shops').flush({ title: limit, reason: 'limit' }, { status: 409, statusText: 'Conflict' });
    await wait(0);
    await fixture.whenStable();

    expect(el.querySelector('[role=alert]')?.textContent).toContain(limit);
  });
});
