import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { ForgotPassword } from './forgot-password';

describe('ForgotPassword', () => {
  const GENERIC = "If an eligible account exists for this email, we've sent a password reset code.";

  let fixture: ComponentFixture<ForgotPassword>;
  let root: HTMLElement;
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    sessionStorage.clear();
    await TestBed.configureTestingModule({
      imports: [ForgotPassword],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    fixture = TestBed.createComponent(ForgotPassword);
    root = fixture.nativeElement as HTMLElement;
    http = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    await fixture.whenStable();
  });

  afterEach(() => http.verify());

  // ---- helpers

  const text = () => root.textContent!.replace(/\s+/g, ' ');
  const input = (selector = 'input') => root.querySelector<HTMLInputElement>(selector)!;
  const submitButton = () => root.querySelector<HTMLButtonElement>('button[type="submit"]')!;
  const buttonLabelled = (label: string) =>
    Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find((b) => b.textContent!.trim() === label)!;

  async function settle() {
    fixture.detectChanges();
    await fixture.whenStable();
  }

  async function type(value: string) {
    const el = input();
    el.value = value;
    el.dispatchEvent(new Event('input'));
    await settle();
  }

  async function submit() {
    root.querySelector('form')!.dispatchEvent(new Event('submit'));
    await settle();
  }

  async function askForCode(email = 'user@example.com') {
    await type(email);
    await submit();
    http.expectOne('/api/auth/forgot-password').flush(null, { status: 202, statusText: 'Accepted' });
    await settle();
  }

  async function enterCode(code = '123456', token = 'reset-token-xyz') {
    await type(code);
    await submit();
    http.expectOne('/api/auth/verify-password-reset').flush({ resetToken: token });
    await settle();
  }

  // ================= asking for a code =================

  it('starts with an email field, a Send code button, and a way back to sign in', () => {
    expect(root.querySelector('h1')!.textContent).toContain('Forgot your password');
    expect(input().type).toBe('email');
    expect(submitButton().textContent!.trim()).toBe('Send code');
    expect(root.querySelector('a[href="/login"]')!.textContent).toContain('Back to sign in');
  });

  it('does not call the API for a missing or malformed email', async () => {
    await submit();
    expect(text()).toContain('Enter a valid email address.');

    await type('not-an-email');
    await submit();
    http.expectNone('/api/auth/forgot-password');
    expect(text()).toContain('Enter a valid email address.');
  });

  it('sends the trimmed email, and disables the button while it is sending', async () => {
    await type('  user@example.com ');
    await submit();

    const req = http.expectOne('/api/auth/forgot-password');
    expect(req.request.body).toEqual({ email: 'user@example.com' });
    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent!.trim()).toBe('Sending…');
    req.flush(null, { status: 202, statusText: 'Accepted' });
    await settle();
  });

  it('always shows the same generic message once the request is accepted', async () => {
    await askForCode();

    expect(text()).toContain(GENERIC);
    expect(text()).not.toMatch(/not registered|no account|unknown|verified/i);
    expect(input().inputMode).toBe('numeric'); // now asking for the code
  });

  it('shows an error, and stays put, if the request itself fails', async () => {
    await type('user@example.com');
    await submit();
    http.expectOne('/api/auth/forgot-password').flush('boom', { status: 500, statusText: 'Server Error' });
    await settle();

    expect(text()).toContain("Couldn't send the code. Please try again.");
    expect(submitButton().textContent!.trim()).toBe('Send code'); // enabled again, same step
    expect(submitButton().disabled).toBe(false);
    expect(text()).not.toContain('boom');
  });

  // ================= entering the code =================

  it('rejects a code that is not six digits without calling the API', async () => {
    await askForCode();

    await type('12345');
    await submit();

    http.expectNone('/api/auth/verify-password-reset');
    expect(text()).toContain('Enter the 6-digit code from the email.');
  });

  it('sends the email and code, and disables the button while checking', async () => {
    await askForCode('user@example.com');
    await type(' 123456 ');
    await submit();

    const req = http.expectOne('/api/auth/verify-password-reset');
    expect(req.request.body).toEqual({ email: 'user@example.com', code: '123456' });
    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent!.trim()).toBe('Checking…');
    expect(buttonLabelled('Use a different email').disabled).toBe(true);
    req.flush({ resetToken: 'reset-token-xyz' });
    await settle();
  });

  it('moves on to choosing a new password after a correct code', async () => {
    await askForCode();
    await enterCode();

    expect(text()).toContain('Choose a new password');
    expect(input().type).toBe('password');
    expect(submitButton().textContent!.trim()).toBe('Change password');
  });

  it('explains a wrong or expired code in friendly words, and lets the user try again', async () => {
    await askForCode();
    await type('999999');
    await submit();
    http.expectOne('/api/auth/verify-password-reset').flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    expect(text()).toContain("That code isn't right or has expired.");
    expect(submitButton().disabled).toBe(false);
    expect(text()).not.toMatch(/400|Bad Request|token/i);
  });

  it('can go back to the first step to use a different email', async () => {
    await askForCode();
    buttonLabelled('Use a different email').click();
    await settle();

    expect(text()).toContain('Enter your email address');
    expect(submitButton().textContent!.trim()).toBe('Send code');
  });

  // ================= the reset token stays in memory =================

  it('never writes the reset token to localStorage, sessionStorage or anywhere persistent', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem');
    await askForCode();
    await enterCode('123456', 'reset-token-xyz');

    expect(text()).not.toContain('reset-token-xyz'); // not even shown
    expect(setItem).not.toHaveBeenCalled();
    expect(JSON.stringify({ ...localStorage })).not.toContain('reset-token-xyz');
    expect(JSON.stringify({ ...sessionStorage })).not.toContain('reset-token-xyz');
    setItem.mockRestore();
  });

  it('keeps the token in memory only: it is used for the reset, and gone after a restart', async () => {
    await askForCode();
    await enterCode('123456', 'reset-token-xyz');
    await type('A-new-password-1');
    await submit();
    const req = http.expectOne('/api/auth/reset-password');
    expect(req.request.body.resetToken).toBe('reset-token-xyz'); // it came from memory
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    // a freshly created screen (as after a page reload) has no token and starts at the beginning
    const reloaded = TestBed.createComponent(ForgotPassword);
    reloaded.detectChanges();
    await reloaded.whenStable();
    expect((reloaded.nativeElement as HTMLElement).textContent).toContain('Enter your email address');
  });

  // ================= choosing the new password =================

  it('applies the registration password rule to the new password', async () => {
    await askForCode();
    await enterCode();

    for (const bad of ['', '1234567', 'a'.repeat(129)]) {
      await type(bad);
      await submit();
      http.expectNone('/api/auth/reset-password');
      expect(text()).toContain('Use 8 to 128 characters.');
    }
  });

  it('accepts exactly 8 and exactly 128 characters', async () => {
    for (const ok of ['12345678', 'a'.repeat(128)]) {
      await askForCode();
      await enterCode();
      await type(ok);
      await submit();
      expect(http.expectOne('/api/auth/reset-password').request.body.newPassword).toBe(ok);
      http.expectNone('/api/auth/reset-password');
      TestBed.createComponent(ForgotPassword); // fresh screen for the next round
      fixture = TestBed.createComponent(ForgotPassword);
      root = fixture.nativeElement as HTMLElement;
      fixture.detectChanges();
      await fixture.whenStable();
    }
  });

  it('sends email, reset token and new password, and disables the button while saving', async () => {
    await askForCode('user@example.com');
    await enterCode('123456', 'reset-token-xyz');
    await type('A-new-password-1');
    await submit();

    const req = http.expectOne('/api/auth/reset-password');
    expect(req.request.body).toEqual({
      email: 'user@example.com',
      resetToken: 'reset-token-xyz',
      newPassword: 'A-new-password-1',
    });
    expect(submitButton().disabled).toBe(true);
    expect(submitButton().textContent!.trim()).toBe('Saving…');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle();
  });

  // ================= the outcome =================

  it('confirms a successful reset and links back to sign in', async () => {
    await askForCode();
    await enterCode();
    await type('A-new-password-1');
    await submit();
    http.expectOne('/api/auth/reset-password').flush(null, { status: 204, statusText: 'No Content' });
    await settle();

    expect(text()).toContain('Your password has been changed.');
    const link = root.querySelector<HTMLAnchorElement>('a[href="/login"]')!;
    expect(link.textContent).toContain('Go to sign in');
    expect(root.querySelector('input')).toBeNull(); // the password is not left on screen
  });

  it('starts over with a clear message when the reset is rejected (expired or used token)', async () => {
    await askForCode();
    await enterCode();
    await type('A-new-password-1');
    await submit();
    http.expectOne('/api/auth/reset-password').flush(null, { status: 400, statusText: 'Bad Request' });
    await settle();

    expect(text()).toContain('This reset has expired. Please start again');
    expect(text()).toContain('Enter your email address'); // back at the first step
    expect(text()).not.toMatch(/400|Bad Request|token/i);
  });

  it('keeps the new-password step and allows a retry when saving fails for another reason', async () => {
    await askForCode();
    await enterCode();
    await type('A-new-password-1');
    await submit();
    http.expectOne('/api/auth/reset-password').flush('boom', { status: 500, statusText: 'Server Error' });
    await settle();

    expect(text()).toContain("Couldn't change your password. Please try again.");
    expect(submitButton().disabled).toBe(false);
    expect(submitButton().textContent!.trim()).toBe('Change password');

    await submit(); // the token is still held, so a retry can work
    http.expectOne('/api/auth/reset-password').flush(null, { status: 204, statusText: 'No Content' });
    await settle();
    expect(text()).toContain('Your password has been changed.');
  });
});
