import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

/** contracts/panel-api.md `Shop.state`. */
export type ShopState = 'preparing' | 'ready' | 'attention' | 'deleting';

/** contracts/panel-api.md `Shop`. */
export interface Shop {
  id: string;
  name: string;
  availability: 'standard' | 'high';
  database: 'standard' | 'light';
  walletAddress: string;
  state: ShopState;
  reason: string | null;
  url: string | null;
  readyReplicas: number;
  replicas: number;
  createdAt: string;
}

export interface NewShop {
  name: string;
  availability: 'standard' | 'high';
  database: 'standard' | 'light';
  walletAddress: string;
}

/** contracts/panel-api.md PATCH body: only the fields that change. */
export interface ShopChange {
  availability?: Shop['availability'];
  walletAddress?: string;
}

export const STATE_LABELS: Record<ShopState, string> = {
  preparing: 'Being prepared',
  ready: 'Ready',
  attention: 'Needs attention',
  deleting: 'Being deleted',
};

export const AVAILABILITY_LABELS: Record<Shop['availability'], string> = {
  standard: 'Standard (2 copies)',
  high: 'High (3 copies)',
};

export const DATABASE_LABELS: Record<Shop['database'], string> = {
  standard: 'Standard (PostgreSQL)',
  light: 'Light (Redis)',
};

/** The `reason` of a 409 problem (`duplicateName`, `limit`, `notReady`, `changed`, `deleting`), if any. */
export function conflictReason(error: unknown): string | undefined {
  return error instanceof HttpErrorResponse && error.status === 409 && typeof error.error?.reason === 'string'
    ? error.error.reason
    : undefined;
}

/** Shops API client (contracts/panel-api.md "Shops"). */
@Injectable({ providedIn: 'root' })
export class ShopsApi {
  private readonly http = inject(HttpClient);

  async list(): Promise<Shop[]> {
    return (await firstValueFrom(this.http.get<{ shops: Shop[] }>('/api/shops'))).shops;
  }

  /** 201 for a new shop, 200 with the existing one for a repeated create; both resolve. */
  create(shop: NewShop): Promise<Shop> {
    return firstValueFrom(this.http.post<Shop>('/api/shops', shop));
  }

  update(id: string, change: ShopChange): Promise<Shop> {
    return firstValueFrom(this.http.patch<Shop>(`/api/shops/${encodeURIComponent(id)}`, change));
  }

  async adminPassword(id: string): Promise<string> {
    const answer = await firstValueFrom(
      this.http.post<{ password: string }>(`/api/shops/${encodeURIComponent(id)}/admin-password`, null),
    );
    return answer.password;
  }
}
