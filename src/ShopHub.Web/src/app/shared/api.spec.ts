import { HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { fieldErrors, problemMessage, retryMinutes } from './api';

describe('fieldErrors', () => {
  it('maps the first message of each field', () => {
    const error = new HttpErrorResponse({
      status: 400,
      error: { title: 'Invalid.', errors: { email: ['Bad email.'], password: ['Short', 'Other'] } },
    });
    expect(fieldErrors(error)).toEqual({ email: 'Bad email.', password: 'Short' });
  });

  it('returns nothing for other errors', () => {
    expect(fieldErrors(new HttpErrorResponse({ status: 500 }))).toEqual({});
    expect(fieldErrors(new Error('x'))).toEqual({});
  });
});

describe('problemMessage', () => {
  it('uses the problem title, or a fixed text for 0 and 503', () => {
    expect(problemMessage(new HttpErrorResponse({ status: 404, error: { title: 'Not found.' } }))).toBe('Not found.');
    expect(problemMessage(new HttpErrorResponse({ status: 503 }))).toContain('temporarily unavailable');
    expect(problemMessage(new HttpErrorResponse({ status: 0 }))).toContain('cannot be reached');
    expect(problemMessage('nope')).toBe('Something went wrong.');
  });
});

describe('retryMinutes', () => {
  const withRetry = (value?: string) =>
    new HttpErrorResponse({ status: 429, headers: value ? new HttpHeaders({ 'Retry-After': value }) : new HttpHeaders() });

  it('rounds Retry-After seconds up to minutes, 15 when missing', () => {
    expect(retryMinutes(withRetry('1'))).toBe(1);
    expect(retryMinutes(withRetry('61'))).toBe(2);
    expect(retryMinutes(withRetry('900'))).toBe(15);
    expect(retryMinutes(withRetry())).toBe(15);
    expect(retryMinutes(withRetry('soon'))).toBe(15);
  });
});
