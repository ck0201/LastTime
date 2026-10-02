import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Auth } from '../../core/auth';
import { fakeJwt } from '../../core/fake-jwt';
import { VerifyEmail } from './verify-email';

describe('VerifyEmail', () => {
  let fixture: ComponentFixture<VerifyEmail>;
  let root: HTMLElement;
  let http: HttpTestingController;
  let auth: Auth;

  async function create(claims: Record<string, unknown> = { sub: 'user-1', email: 'user@example.com' }) {
    localStorage.clear();
    localStorage.setItem('lasttime.token', fakeJwt(claims)); // a signed-in user, as the route guard requires
    await TestBed.configureTestingModule({
      imports: [VerifyEmail],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    auth = TestBed.inject(Auth);
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(VerifyEmail);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await fixture.whenStable();
  }

  // Only the countdown's interval is faked; promises, change detection and everything else run normally.
  beforeEach(() => vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }));
  afterEach(() => {
    http.verify();
    vi.useRealTimers();
  });

  const text = () => root.textContent!.replace(/\s+/g, ' ');
  const sendButton = () => root.querySelector<HTMLButtonElement>('.actions button')!;
  const verifyButton = () => root.querySelector<HTMLButtonElement>('button[type="submit"]');
  const codeInput = () => root.querySelector<HTMLInputElement>('input')!;

  async function settle() {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function send(response: { status: number; headers?: Record<string, string> } = { status: 202 }) {
    sendButton().click();
    await settle();
    http.expectOne('/api/auth/resend-verification').flush(null, { ...response, statusText: 'x' });
    await settle();
  }

  async function submitCode(code: string) {
    codeInput().value = code;
    codeInput().dispatchEvent(new Event('input'));
    await settle();
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  async function tick(seconds: number) {
    vi.advanceTimersByTime(seconds * 1000);
    await settle();
  }

  // ================= sending a code =================

  it('offers to send a code first, with no code field yet', async () => {
    await create();

    expect(root.querySelector('h1')!.textContent).toContain('Verify your email');
    expect(sendButton().textContent!.trim()).toBe('Send me a code');
    expect(root.querySelector('input')).toBeNull();
    http.expectNone('/api/auth/resend-verification'); // nothing is sent until the user asks
  });

  it('asks the API for a code, then shows the code field and a short wait before sending again', async () => {
    await create();
    await send();

    expect(text()).toContain("We've emailed you a 6-digit code.");
    expect(codeInput()).not.toBeNull();
    expect(sendButton().disabled).toBe(true);
    expect(sendButton().textContent!.trim()).toBe('Send again in 60s');
  });

  it('disables the button while the request is in flight, so it cannot be double-clicked', async () => {
    await create();
    sendButton().click();
    await settle();

    expect(sendButton().disabled).toBe(true);
    expect(sendButton().textContent!.trim()).toBe('Sending…');
    sendButton().click(); // a second click does nothing
    http.expectOne('/api/auth/resend-verification').flush(null, { status: 202, statusText: 'Accepted' });
    await settle();
    http.expectNone('/api/auth/resend-verification');
  });

  it('counts down and re-enables sending after the wait', async () => {
    await create();
    await send();

    await tick(30);
    expect(sendButton().textContent!.trim()).toBe('Send again in 30s');
    expect(sendButton().disabled).toBe(true);

    await tick(30);
    expect(sendButton().disabled).toBe(false);
    expect(sendButton().textContent!.trim()).toBe('Send a new code');
  });

  // ================= the cooldown (HTTP 429) =================

  it('on 429 shows a useful message and waits for the time the API gave in Retry-After', async () => {
    await create();
    await send({ status: 429, headers: { 'Retry-After': '25' } });

    expect(text()).toContain('A code was sent a moment ago.');
    expect(sendButton().disabled).toBe(true);
    expect(sendButton().textContent!.trim()).toBe('Send again in 25s');
    expect(codeInput()).not.toBeNull(); // the code from earlier can still be entered

    await tick(25);
    expect(sendButton().disabled).toBe(false);
  });

  it('on 429 without a readable Retry-After (the browser may hide it cross-origin) waits a minute', async () => {
    await create();
    await send({ status: 429 });

    expect(sendButton().textContent!.trim()).toBe('Send again in 60s');
    expect(sendButton().disabled).toBe(true);
  });

  it('shows a plain message, without internals, when sending fails', async () => {
    await create();
    sendButton().click();
    await settle();
    http.expectOne('/api/auth/resend-verification').flush('boom', { status: 500, statusText: 'Server Error' });
    await settle();

    expect(text()).toContain("Couldn't send the code. Please try again.");
    expect(text()).not.toMatch(/boom|500|Server Error/);
    expect(sendButton().disabled).toBe(false); // free to try again
  });

  // ================= entering the code =================

  it('does not call the API for a code that is not six digits', async () => {
    await create();
    await send();

    await submitCode('12ab');

    http.expectNone('/api/auth/verify-email');
    expect(text()).toContain('Enter the 6-digit code from the email.');
  });

  it('submits the code, disabling the button meanwhile', async () => {
    await create();
    await send();
    await submitCode(' 123456 ');

    const req = http.expectOne('/api/auth/verify-email');
    expect(req.request.body).toEqual({ code: '123456' });
    expect(verifyButton()!.disabled).toBe(true);
    expect(verifyButton()!.textContent!.trim()).toBe('Verifying…');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  it('on success the screen and the app state switch to verified, with no reload', async () => {
    await create();
    expect(auth.needsVerification()).toBe(true);
    await send();
    await submitCode('123456');
    http.expectOne('/api/auth/verify-email').flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(auth.emailVerified()).toBe(true);
    expect(auth.needsVerification()).toBe(false); // so the "please verify" warning disappears elsewhere
    expect(text()).toContain('Your email address is verified.');
    expect(root.querySelector('form')).toBeNull();
    expect(root.querySelector<HTMLAnchorElement>('a[href="/"]')!.textContent).toContain('Back to your items');
  });

  it('explains a wrong or expired code in friendly words, and stays unverified', async () => {
    await create();
    await send();
    await submitCode('000000');
    http.expectOne('/api/auth/verify-email').flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    expect(text()).toContain("That code isn't right or has expired.");
    expect(text()).not.toMatch(/400|Bad Request/);
    expect(auth.emailVerified()).toBe(false);
    expect(verifyButton()!.disabled).toBe(false); // can try again
  });

  it('shows a generic message, not internals, when verification fails for another reason', async () => {
    await create();
    await send();
    await submitCode('123456');
    http.expectOne('/api/auth/verify-email').flush('db exploded', { status: 500, statusText: 'Server Error' });
    await settle();

    expect(text()).toContain("Couldn't verify your email. Please try again.");
    expect(text()).not.toContain('db exploded');
    expect(auth.emailVerified()).toBe(false);
  });

  // ================= already verified =================

  it('treats a 409 from "send a code" as "already verified" and updates the app state', async () => {
    await create();
    expect(auth.needsVerification()).toBe(true);
    await send({ status: 409 });

    expect(auth.emailVerified()).toBe(true);
    expect(auth.needsVerification()).toBe(false);
    expect(text()).toContain('Your email address is verified.');
  });

  it('shows the verified message straight away for an account already known to be verified', async () => {
    await create({ sub: 'user-1', email_verified: true });

    expect(text()).toContain('Your email address is verified.');
    expect(root.querySelector('input')).toBeNull();
    http.expectNone('/api/auth/resend-verification');
  });

  it('stops its countdown when the screen is closed', async () => {
    await create();
    await send();
    const clear = vi.spyOn(globalThis, 'clearInterval');

    fixture.destroy();

    expect(clear).toHaveBeenCalled();
    clear.mockRestore();
  });
});
