import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Auth } from '../../core/auth';
import { fakeJwt } from '../../core/fake-jwt';
import { ItemList } from './item-list';

describe('ItemList: "please verify your email" warning', () => {
  let fixture: ComponentFixture<ItemList>;
  let root: HTMLElement;

  async function render(token: string | null, alsoStored: Record<string, string> = {}) {
    localStorage.clear();
    if (token) localStorage.setItem('lasttime.token', token);
    for (const [key, value] of Object.entries(alsoStored)) localStorage.setItem(key, value);
    await TestBed.configureTestingModule({
      imports: [ItemList],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(ItemList);
    root = fixture.nativeElement as HTMLElement;
    fixture.detectChanges();
    await new Promise((r) => setTimeout(r, 300)); // the search is debounced by 250ms
    TestBed.inject(HttpTestingController).expectOne('/api/items').flush([]);
    await fixture.whenStable();
  }

  const banner = () => root.querySelector('.verify-banner');

  it('shows the warning, with a Verify email action, for a signed-in account that is not verified', async () => {
    await render(fakeJwt({ sub: 'user-1', email: 'user@example.com' }));

    const text = banner()!.textContent!.replace(/\s+/g, ' ');
    expect(text).toContain('Please verify your email address.');
    expect(text).toContain(
      "Your email is used to recover your account if you forget your password. If you don't verify it, we may not be able to help you recover your account later.",
    );
    const action = banner()!.querySelector<HTMLAnchorElement>('a[href="/verify-email"]')!;
    expect(action.textContent!.trim()).toBe('Verify email');
    expect(banner()!.getAttribute('role')).toBe('status'); // announced politely; it does not block anything
  });

  it('does not block the list: the items area and search are still there', async () => {
    await render(fakeJwt({ sub: 'user-1' }));

    expect(banner()).not.toBeNull();
    expect(root.querySelector('input.search')).not.toBeNull();
    expect(root.textContent).toContain('Your items');
  });

  it('is gone as soon as the email becomes verified, without a reload', async () => {
    await render(fakeJwt({ sub: 'user-1' }));
    expect(banner()).not.toBeNull();

    TestBed.inject(Auth).markEmailVerified();
    fixture.detectChanges();
    await fixture.whenStable();

    expect(banner()).toBeNull();
  });

  it('is not shown for an account the token says is verified', async () => {
    await render(fakeJwt({ sub: 'user-1', email_verified: true }));
    expect(banner()).toBeNull();
  });

  it('is not shown for an account this browser already saw verified', async () => {
    await render(fakeJwt({ sub: 'user-1' }), { 'lasttime.verified': 'user-1' });
    expect(banner()).toBeNull();
  });

  it('is still shown for a different user on a browser that saw someone else verified', async () => {
    await render(fakeJwt({ sub: 'user-2' }), { 'lasttime.verified': 'user-1' });
    expect(banner()).not.toBeNull();
  });

  it('is not shown when nobody is signed in', async () => {
    await render(null);
    expect(banner()).toBeNull();
  });
});
