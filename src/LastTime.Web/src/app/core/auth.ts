import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, map, switchMap, tap } from 'rxjs';
import { environment } from '../../environments/environment';

const TOKEN_KEY = 'lasttime.token';
/** Remembers (per user id) that this browser has seen the account's email verified. */
const VERIFIED_KEY = 'lasttime.verified';

/** Holds the JWT (in localStorage) and the signed-in state. The password is never kept. */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly base = `${environment.apiBaseUrl}/api/auth`;

  private readonly _token = signal<string | null>(readToken());
  private readonly _verified = signal(isVerified(readToken()));

  readonly token = this._token.asReadonly();
  readonly isAuthenticated = computed(() => this._token() !== null);

  /**
   * Whether the signed-in account's email is known to be verified. The API does not report this yet, so it is known
   * from an "email_verified" claim in the token (if the API ever adds one), or because this browser verified the
   * email (or learned it was already verified). Until then an account is treated as unverified.
   */
  readonly emailVerified = this._verified.asReadonly();
  readonly needsVerification = computed(() => this.isAuthenticated() && !this._verified());

  login(email: string, password: string): Observable<unknown> {
    return this.http
      .post<{ token: string }>(`${this.base}/login`, { email, password })
      .pipe(tap((res) => this.setToken(res.token)));
  }

  /** Creates the account, then signs in with the same credentials. */
  register(email: string, password: string): Observable<unknown> {
    return this.http
      .post(`${this.base}/register`, { email, password })
      .pipe(switchMap(() => this.login(email, password)));
  }

  /** Checks the 6-digit code emailed to the signed-in user. On success the account counts as verified from now on. */
  verifyEmail(code: string): Observable<unknown> {
    return this.http.post(`${this.base}/verify-email`, { code }).pipe(tap(() => this.markEmailVerified()));
  }

  /** Emails a new verification code (the API allows one per minute, and answers 409 if already verified). */
  resendVerification(): Observable<unknown> {
    return this.http.post(`${this.base}/resend-verification`, null);
  }

  /** Asks for a password reset code. The API answers the same way whether or not the account exists. */
  forgotPassword(email: string): Observable<unknown> {
    return this.http.post(`${this.base}/forgot-password`, { email });
  }

  /** Checks the emailed reset code and returns the one-time reset token. Callers keep it in memory only. */
  verifyPasswordReset(email: string, code: string): Observable<string> {
    return this.http
      .post<{ resetToken: string }>(`${this.base}/verify-password-reset`, { email, code })
      .pipe(map((res) => res.resetToken));
  }

  resetPassword(email: string, resetToken: string, newPassword: string): Observable<unknown> {
    return this.http.post(`${this.base}/reset-password`, { email, resetToken, newPassword });
  }

  /** Records that the signed-in account's email is verified, so the "please verify" warning goes away. */
  markEmailVerified(): void {
    this._verified.set(true);
    const id = claimsOf(this._token())?.['sub'];
    if (typeof id !== 'string') return;
    try {
      localStorage.setItem(VERIFIED_KEY, id);
    } catch {
      // storage unavailable: it stays verified for this session
    }
  }

  logout(): void {
    this.clear();
    this.router.navigate(['/login']);
  }

  /** Drops the token without navigating (used when the API answers 401). */
  clear(): void {
    this._token.set(null);
    this._verified.set(false);
    try {
      localStorage.removeItem(TOKEN_KEY);
    } catch {
      // storage unavailable: the in-memory state is already cleared
    }
  }

  private setToken(token: string): void {
    this._token.set(token);
    this._verified.set(isVerified(token));
    try {
      localStorage.setItem(TOKEN_KEY, token);
    } catch {
      // storage unavailable: stay signed in for this session only
    }
  }
}

function readToken(): string | null {
  try {
    return localStorage.getItem(TOKEN_KEY);
  } catch {
    return null;
  }
}

/** The claims of a JWT, read without checking the signature (the API checks it; this is only for display). */
function claimsOf(token: string | null): Record<string, unknown> | null {
  try {
    const payload = token?.split('.')[1];
    if (!payload) return null;
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0));
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch {
    return null;
  }
}

function isVerified(token: string | null): boolean {
  const claims = claimsOf(token);
  if (!claims) return false;
  if (claims['email_verified'] === true || claims['email_verified'] === 'true') return true;
  try {
    return typeof claims['sub'] === 'string' && localStorage.getItem(VERIFIED_KEY) === claims['sub'];
  } catch {
    return false;
  }
}
