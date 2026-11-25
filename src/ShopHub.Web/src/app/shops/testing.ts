import { Shop } from './shops-api';

// Test data for the shop specs (only *.spec.ts import this file).

export function shop(overrides: Partial<Shop> = {}): Shop {
  return {
    id: 's7k2m4qxab',
    name: 'Acme',
    availability: 'standard',
    database: 'standard',
    walletAddress: '0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed',
    state: 'ready',
    reason: null,
    url: 'https://s7k2m4qxab.shops.localhost',
    readyReplicas: 2,
    replicas: 2,
    createdAt: '2026-11-25T10:00:00Z',
    ...overrides,
  };
}

export const preparing = (overrides: Partial<Shop> = {}) =>
  shop({ id: 's2prep2222', name: 'Light', state: 'preparing', reason: 'The database is being prepared.', url: null, readyReplicas: 0, ...overrides });

/** Real-time wait (the specs use a short refresh interval instead of fake timers). */
export const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
