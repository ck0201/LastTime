import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Register } from './register';

@Component({ template: '' })
class Blank {}

describe('Register', () => {
  async function render() {
    await TestBed.configureTestingModule({
      imports: [Register],
      providers: [
        provideRouter([{ path: 'login', component: Blank }]),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(Register);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }

  it('renders the registration form with link to sign in', async () => {
    const { root } = await render();
    expect(root.querySelector('a[href="/login"]')).not.toBeNull();
  });

  it('toggles password visibility when the eye icon is clicked', async () => {
    const { fixture, root } = await render();
    const passwordInput = root.querySelector<HTMLInputElement>('input[formControlName="password"]')!;
    const toggleBtn = root.querySelector<HTMLButtonElement>('.password-toggle')!;

    expect(passwordInput.type).toBe('password');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Show password');

    toggleBtn.click();
    fixture.detectChanges();
    expect(passwordInput.type).toBe('text');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Hide password');

    toggleBtn.click();
    fixture.detectChanges();
    expect(passwordInput.type).toBe('password');
    expect(toggleBtn.getAttribute('aria-label')).toBe('Show password');
  });
});
