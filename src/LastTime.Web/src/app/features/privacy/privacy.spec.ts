import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { App } from '../../app';
import { routes } from '../../app.routes';
import { Privacy } from './privacy';

describe('Privacy & Security', () => {
  async function renderPage() {
    await TestBed.configureTestingModule({ imports: [Privacy], providers: [provideRouter([])] }).compileComponents();
    const fixture = TestBed.createComponent(Privacy);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  const normalize = (el: HTMLElement) => el.textContent!.replace(/\s+/g, ' ');

  it('has a Privacy & Security heading with Privacy, Security and Feedback sections', async () => {
    const root = await renderPage();

    expect(root.querySelector('h1')!.textContent).toContain('Privacy & Security');
    expect(Array.from(root.querySelectorAll('h2')).map((h) => h.textContent!.trim())).toEqual([
      'Privacy',
      'Security',
      'Feedback',
    ]);
  });

  it('explains what is stored and how it is used', async () => {
    const text = normalize(await renderPage());

    expect(text).toContain('stores the email address you use for your account');
    expect(text).toContain('stored securely, as a hash');
    expect(text).toContain('items and their history, including descriptions, dates, costs, providers and notes');
    expect(text).toContain("intended to stay private to your account");
    expect(text).toContain('hosted PostgreSQL database');
    expect(text).toContain('used for email verification and for password recovery');
    expect(text).toContain("We do not sell users' personal information.");
  });

  it('gives the security advice', async () => {
    const text = normalize(await renderPage());

    expect(text).toContain('Do not share your LastTime password with anyone.');
    expect(text).toContain('Do not share verification codes or password-reset codes.');
    expect(text).toContain('will never ask you to give your password or a verification code to another person');
    expect(text).toContain('Only enter information you are comfortable storing in the application.');
    expect(text).toContain('contact the app administrator');
  });

  it('shows the friendly feedback message, without a form or any contact details', async () => {
    const root = await renderPage();
    const feedback = normalize(root.querySelector<HTMLElement>('#feedback')!);

    expect(feedback).toContain(
      "Using LastTime? I'd love your feedback. If something is confusing, missing, doesn't work as expected, or you have an idea that would make the app more useful, let me know.",
    );
    expect(root.querySelector('form, input, textarea, button')).toBeNull(); // nothing to submit
    expect(root.textContent).not.toMatch(/@[\w.-]+\.\w+/); // no email address
    expect(root.textContent).not.toMatch(/\+?\d[\d\s().-]{8,}\d/); // no phone number
    expect(root.querySelector('a[href^="mailto:"], a[href^="tel:"]')).toBeNull();
  });

  it('has a way back', async () => {
    const root = await renderPage();
    expect(root.querySelector<HTMLAnchorElement>('a.back')!.getAttribute('href')).toBe('/');
  });
});

describe('Privacy & Security in the app', () => {
  beforeEach(() => localStorage.clear());

  it('is reachable at /privacy without signing in', async () => {
    await TestBed.configureTestingModule({ providers: [provideRouter(routes)] }).compileComponents();

    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/privacy');

    expect(TestBed.inject(Router).url).toBe('/privacy'); // not bounced to /login
    expect((harness.routeNativeElement as HTMLElement).querySelector('h1')!.textContent).toContain('Privacy & Security');
  });

  it('is linked from a small footer shown on every screen, with a short feedback note', async () => {
    await TestBed.configureTestingModule({ imports: [App], providers: [provideRouter([])] }).compileComponents();
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const footer = (fixture.nativeElement as HTMLElement).querySelector('footer.site-footer')!;
    const link = footer.querySelector<HTMLAnchorElement>('a[href="/privacy"]')!;
    expect(link.textContent!.trim()).toBe('Privacy & Security');
    expect(footer.textContent).toContain('Feedback is always welcome');
    expect(footer.querySelector('button, input, form')).toBeNull(); // unobtrusive: just text and one link
  });

  it('keeps the existing header and routes working', async () => {
    await TestBed.configureTestingModule({ imports: [App], providers: [provideRouter(routes)] }).compileComponents();
    const harness = await RouterTestingHarness.create();

    await harness.navigateByUrl('/login');
    expect(TestBed.inject(Router).url).toBe('/login');
    await harness.navigateByUrl('/forgot-password');
    expect(TestBed.inject(Router).url).toBe('/forgot-password');
  });
});
