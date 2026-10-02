import { provideHttpClient, withInterceptors, HttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Auth } from './auth';
import { authInterceptor } from './auth.interceptor';
import { fakeJwt } from './fake-jwt';
import { passwordValidators } from './password-rules';
import { FormControl } from '@angular/forms';

@Component({ template: '' })
class Blank {}

describe('Auth: email verification and password reset', () => {
  let http: HttpTestingController;

  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: Blank },
          { path: '', component: Blank },
          { path: 'verify-email', component: Blank },
        ]),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    return TestBed.inject(Auth);
  }

  function signIn(auth: Auth, claims: Record<string, unknown> = { sub: 'user-1', email: 'a@example.com' }) {
    auth.login('a@example.com', 'Password123!').subscribe();
    http.expectOne('/api/auth/login').flush({ token: fakeJwt(claims) });
  }

  beforeEach(() => localStorage.clear());
  afterEach(() => http.verify());

  describe('whether the email is known to be verified', () => {
    it('is not a concern when nobody is signed in', () => {
      const auth = setup();
      expect(auth.needsVerification()).toBe(false);
    });

    it('treats a signed-in account as unverified when nothing says otherwise', () => {
      const auth = setup();
      signIn(auth);
      expect(auth.emailVerified()).toBe(false);
      expect(auth.needsVerification()).toBe(true);
    });

    it('trusts an "email_verified" claim if the API ever sends one', () => {
      const auth = setup();
      signIn(auth, { sub: 'user-1', email_verified: true });
      expect(auth.emailVerified()).toBe(true);
      expect(auth.needsVerification()).toBe(false);
    });

    it('remembers a verification per user in this browser, and not for other users', () => {
      const first = setup();
      signIn(first);
      first.markEmailVerified();
      expect(localStorage.getItem('lasttime.verified')).toBe('user-1');

      // same user signs in again (a new Auth, as after a reload): still verified
      first.logout();
      TestBed.resetTestingModule();
      const again = setup();
      signIn(again);
      expect(again.emailVerified()).toBe(true);

      // a different user on the same browser is not
      again.clear();
      signIn(again, { sub: 'user-2', email: 'b@example.com' });
      expect(again.emailVerified()).toBe(false);
    });

    it('is reset by signing out', () => {
      const auth = setup();
      signIn(auth);
      auth.markEmailVerified();
      expect(auth.emailVerified()).toBe(true);

      auth.clear();

      expect(auth.emailVerified()).toBe(false);
      expect(auth.needsVerification()).toBe(false);
    });

    it('survives an unreadable token without breaking', () => {
      const auth = setup();
      auth.login('a@example.com', 'x').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'not-a-jwt' });
      expect(auth.needsVerification()).toBe(true);
      expect(() => auth.markEmailVerified()).not.toThrow();
    });
  });

  describe('API calls', () => {
    it('verifyEmail sends the code and, on success, marks the email verified', () => {
      const auth = setup();
      signIn(auth);

      auth.verifyEmail('123456').subscribe();
      const req = http.expectOne('/api/auth/verify-email');
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ code: '123456' });
      expect(req.request.headers.get('Authorization')).toMatch(/^Bearer /);
      expect(auth.emailVerified()).toBe(false); // not until the API agrees
      req.flush(null, { status: 204, statusText: 'No Content' });

      expect(auth.emailVerified()).toBe(true);
      expect(auth.needsVerification()).toBe(false);
    });

    it('verifyEmail with a wrong code leaves the account unverified', () => {
      const auth = setup();
      signIn(auth);

      auth.verifyEmail('000000').subscribe({ error: () => {} });
      http.expectOne('/api/auth/verify-email').flush(null, { status: 400, statusText: 'Bad Request' });

      expect(auth.emailVerified()).toBe(false);
    });

    it('resendVerification posts to the API (authenticated)', () => {
      const auth = setup();
      signIn(auth);

      auth.resendVerification().subscribe();
      const req = http.expectOne('/api/auth/resend-verification');
      expect(req.request.method).toBe('POST');
      expect(req.request.headers.get('Authorization')).toMatch(/^Bearer /);
      req.flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('forgotPassword sends only the email, with no sign-in needed', () => {
      const auth = setup();

      auth.forgotPassword('a@example.com').subscribe();
      const req = http.expectOne('/api/auth/forgot-password');
      expect(req.request.body).toEqual({ email: 'a@example.com' });
      expect(req.request.headers.has('Authorization')).toBe(false);
      req.flush(null, { status: 202, statusText: 'Accepted' });
    });

    it('verifyPasswordReset returns the reset token, and writes nothing to storage', () => {
      const auth = setup();
      const setItem = vi.spyOn(Storage.prototype, 'setItem');
      let token = '';

      auth.verifyPasswordReset('a@example.com', '123456').subscribe((t) => (token = t));
      const req = http.expectOne('/api/auth/verify-password-reset');
      expect(req.request.body).toEqual({ email: 'a@example.com', code: '123456' });
      req.flush({ resetToken: 'reset-token-abc' });

      expect(token).toBe('reset-token-abc');
      expect(setItem).not.toHaveBeenCalled();
      expect(JSON.stringify({ ...localStorage, ...sessionStorage })).not.toContain('reset-token-abc');
      setItem.mockRestore();
    });

    it('resetPassword sends the email, the reset token and the new password', () => {
      const auth = setup();

      auth.resetPassword('a@example.com', 'reset-token-abc', 'New-password-1').subscribe();
      const req = http.expectOne('/api/auth/reset-password');
      expect(req.request.body).toEqual({ email: 'a@example.com', resetToken: 'reset-token-abc', newPassword: 'New-password-1' });
      req.flush(null, { status: 204, statusText: 'No Content' });
    });
  });

  describe('the shared password rule', () => {
    const accepts = (value: string) => new FormControl(value, passwordValidators).valid;

    it('is required, 8 to 128 characters', () => {
      expect(accepts('')).toBe(false);
      expect(accepts('1234567')).toBe(false);
      expect(accepts('12345678')).toBe(true);
      expect(accepts('a'.repeat(128))).toBe(true);
      expect(accepts('a'.repeat(129))).toBe(false);
    });
  });

  describe('401 handling for the verification endpoints', () => {
    it('a rejected session on verify-email signs the user out (only a failed login is exempt)', async () => {
      const auth = setup();
      signIn(auth);
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/verify-email');

      TestBed.inject(HttpClient).post('/api/auth/verify-email', { code: '123456' }).subscribe({ error: () => {} });
      http.expectOne('/api/auth/verify-email').flush(null, { status: 401, statusText: 'Unauthorized' });
      await new Promise((r) => setTimeout(r));

      expect(auth.isAuthenticated()).toBe(false);
      expect(router.url).toBe('/login');
    });

    it('a failed login still does not count as an expired session', async () => {
      const auth = setup();
      const router = TestBed.inject(Router);
      await router.navigateByUrl('/');
      const navigate = vi.spyOn(router, 'navigate');

      auth.login('a@example.com', 'wrong').subscribe({ error: () => {} });
      http.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
