import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Login } from './login';

@Component({ template: '' })
class Blank {}

describe('Login', () => {
  async function render() {
    await TestBed.configureTestingModule({
      imports: [Login],
      providers: [
        provideRouter([
          { path: 'forgot-password', component: Blank },
          { path: 'register', component: Blank },
        ]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(Login);
    fixture.detectChanges();
    await fixture.whenStable();
    return fixture.nativeElement as HTMLElement;
  }

  it('has a "Forgot password?" link to the forgot-password screen', async () => {
    const root = await render();

    const link = root.querySelector<HTMLAnchorElement>('a[href="/forgot-password"]')!;
    expect(link.textContent!.trim()).toBe('Forgot password?');
    expect(root.querySelector('form')!.contains(link)).toBe(true);
  });

  it('navigates to the forgot-password screen when the link is used', async () => {
    const root = await render();
    const router = TestBed.inject(Router);

    root.querySelector<HTMLAnchorElement>('a[href="/forgot-password"]')!.click();
    await new Promise((r) => setTimeout(r));

    expect(router.url).toBe('/forgot-password');
  });

  it('still offers creating an account', async () => {
    const root = await render();
    expect(root.querySelector('a[href="/register"]')).not.toBeNull();
  });
});
