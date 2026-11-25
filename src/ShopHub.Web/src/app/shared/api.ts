import { HttpErrorResponse } from '@angular/common/http';

// Ported from shop 0.2.0 (src/Shop.Web/src/app/shared/api.ts).

/**
 * Field errors from an RFC 7807 validation problem (`errors: {field: [message]}`), first message
 * per field.
 */
export function fieldErrors(error: unknown): Record<string, string> {
  const errors = error instanceof HttpErrorResponse ? error.error?.errors : undefined;
  const result: Record<string, string> = {};
  if (errors && typeof errors === 'object') {
    for (const [field, messages] of Object.entries(errors)) {
      if (Array.isArray(messages) && messages.length > 0) {
        result[field] = String(messages[0]);
      }
    }
  }
  return result;
}

/** A short message for a failed request; never echoes request data. */
export function problemMessage(error: unknown): string {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 0) {
      return 'ShopHub cannot be reached. Check your connection.';
    }
    if (error.status === 503) {
      return 'ShopHub is temporarily unavailable. Try again shortly.';
    }
    const title = error.error?.title;
    if (typeof title === 'string' && title.length > 0) {
      return title;
    }
  }
  return 'Something went wrong.';
}

/** Minutes to wait from a 429's `Retry-After` (seconds), at least 1; 15 when missing. */
export function retryMinutes(error: HttpErrorResponse): number {
  const seconds = Number(error.headers.get('Retry-After'));
  return Number.isFinite(seconds) && seconds > 0 ? Math.ceil(seconds / 60) : 15;
}
