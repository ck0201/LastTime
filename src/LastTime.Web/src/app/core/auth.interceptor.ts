import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { Auth } from './auth';

const API = `${environment.apiBaseUrl}/api/`;
const LOGIN_API = `${environment.apiBaseUrl}/api/auth/login`;

/** Adds the Bearer token to calls to our API, and signs the user out when the API rejects it (401). */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const router = inject(Router);

  const token = auth.token();
  const outgoing =
    token && req.url.startsWith(API) ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

  return next(outgoing).pipe(
    catchError((e: unknown) => {
      // A failed login answers 401 for bad credentials; that is not an expired session. Every other 401 is.
      if (e instanceof HttpErrorResponse && e.status === 401 && !req.url.startsWith(LOGIN_API)) {
        auth.clear();
        // Already on an auth page: stay put, so there is no redirect loop.
        if (!router.url.startsWith('/login') && !router.url.startsWith('/register')) {
          router.navigate(['/login']);
        }
      }
      return throwError(() => e);
    }),
  );
};
