import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, switchMap, tap } from 'rxjs';
import { environment } from '../../environments/environment';

const TOKEN_KEY = 'lasttime.token';

/** Holds the JWT (in localStorage) and the signed-in state. The password is never kept. */
@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly base = `${environment.apiBaseUrl}/api/auth`;

  private readonly _token = signal<string | null>(readToken());

  readonly token = this._token.asReadonly();
  readonly isAuthenticated = computed(() => this._token() !== null);

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

  logout(): void {
    this.clear();
    this.router.navigate(['/login']);
  }

  /** Drops the token without navigating (used when the API answers 401). */
  clear(): void {
    this._token.set(null);
    try {
      localStorage.removeItem(TOKEN_KEY);
    } catch {
      // storage unavailable: the in-memory state is already cleared
    }
  }

  private setToken(token: string): void {
    this._token.set(token);
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
