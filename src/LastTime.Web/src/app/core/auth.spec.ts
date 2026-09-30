import { HttpClient } from '@angular/common/http';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Auth } from './auth';
import { authInterceptor } from './auth.interceptor';

@Component({ template: '' })
class Blank {}

describe('Auth', () => {
  let auth: Auth;
  let http: HttpTestingController;
  let router: Router;

  function setup() {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        provideRouter([
          { path: 'login', component: Blank },
          { path: 'register', component: Blank },
          { path: '', component: Blank },
        ]),
      ],
    });
    auth = TestBed.inject(Auth);
    http = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
  }

  beforeEach(() => localStorage.clear());
  afterEach(() => http.verify());

  describe('login / logout', () => {
    beforeEach(setup);

    it('stores the token and authenticates on success', () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      const req = http.expectOne('/api/auth/login');
      expect(req.request.body).toEqual({ email: 'a@example.com', password: 'Password123!' });
      req.flush({ token: 'jwt-1' });

      expect(auth.isAuthenticated()).toBe(true);
      expect(auth.token()).toBe('jwt-1');
      expect(localStorage.getItem('lasttime.token')).toBe('jwt-1');
    });

    it('does not store the password', () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'jwt-1' });

      const stored = JSON.stringify({ ...localStorage });
      expect(stored).not.toContain('Password123!');
    });

    it('does not authenticate when login fails', () => {
      let status = 0;
      auth.login('a@example.com', 'wrong').subscribe({ error: (e) => (status = e.status) });
      http.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(status).toBe(401);
      expect(auth.isAuthenticated()).toBe(false);
      expect(localStorage.getItem('lasttime.token')).toBeNull();
    });

    it('register creates the account and then signs in', () => {
      auth.register('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/register').flush({ id: '1' }, { status: 201, statusText: 'Created' });
      http.expectOne('/api/auth/login').flush({ token: 'jwt-2' });

      expect(auth.isAuthenticated()).toBe(true);
    });

    it('register failure (409) does not sign in', () => {
      auth.register('a@example.com', 'Password123!').subscribe({ error: () => {} });
      http.expectOne('/api/auth/register').flush(null, { status: 409, statusText: 'Conflict' });

      expect(auth.isAuthenticated()).toBe(false);
    });

    it('logout removes the token and clears state', () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'jwt-1' });

      auth.logout();

      expect(auth.isAuthenticated()).toBe(false);
      expect(localStorage.getItem('lasttime.token')).toBeNull();
    });

    it('logout navigates to /login', async () => {
      await router.navigateByUrl('/');
      auth.logout();
      await new Promise((r) => setTimeout(r));
      expect(router.url).toBe('/login');
    });
  });

  describe('restoring a session', () => {
    it('starts authenticated when a token is already stored', () => {
      localStorage.setItem('lasttime.token', 'jwt-old');
      setup();
      expect(auth.isAuthenticated()).toBe(true);
    });
  });

  describe('interceptor', () => {
    beforeEach(setup);

    it('adds the Bearer token to API requests', () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'jwt-1' });

      TestBed.inject(HttpClient).get('/api/items').subscribe();
      const req = http.expectOne('/api/items');
      expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-1');
      req.flush([]);
    });

    it('does not add the header when logged out', () => {
      TestBed.inject(HttpClient).get('/api/items').subscribe();
      const req = http.expectOne('/api/items');
      expect(req.request.headers.has('Authorization')).toBe(false);
      req.flush([]);
    });

    it('does not send the token to other hosts', () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'jwt-1' });

      TestBed.inject(HttpClient).get('https://example.com/data').subscribe();
      const req = http.expectOne('https://example.com/data');
      expect(req.request.headers.has('Authorization')).toBe(false);
      req.flush({});
    });

    it('401 from the API clears authentication and redirects to login', async () => {
      auth.login('a@example.com', 'Password123!').subscribe();
      http.expectOne('/api/auth/login').flush({ token: 'jwt-1' });
      await router.navigateByUrl('/');

      TestBed.inject(HttpClient).get('/api/items').subscribe({ error: () => {} });
      http.expectOne('/api/items').flush(null, { status: 401, statusText: 'Unauthorized' });
      await new Promise((r) => setTimeout(r));

      expect(auth.isAuthenticated()).toBe(false);
      expect(localStorage.getItem('lasttime.token')).toBeNull();
      expect(router.url).toBe('/login');
    });

    it('401 while on the login page clears auth but does not navigate again (no loop)', async () => {
      await router.navigateByUrl('/login');
      const navigate = vi.spyOn(router, 'navigate');

      TestBed.inject(HttpClient).get('/api/items').subscribe({ error: () => {} });
      http.expectOne('/api/items').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(navigate).not.toHaveBeenCalled();
      expect(router.url).toBe('/login');
    });

    it('a 401 from login itself (bad credentials) is not treated as an expired session', async () => {
      await router.navigateByUrl('/register');
      const navigate = vi.spyOn(router, 'navigate');

      auth.login('a@example.com', 'wrong').subscribe({ error: () => {} });
      http.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

      expect(navigate).not.toHaveBeenCalled();
    });
  });
});
