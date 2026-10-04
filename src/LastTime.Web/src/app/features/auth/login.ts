import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';

@Component({
  selector: 'app-login',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './login.html',
  styles: `
    .password-input-wrap {
      position: relative;
      display: flex;
      align-items: center;
      width: 100%;
    }
    .password-input-wrap input {
      padding-right: 40px;
    }
    .password-input-wrap input::-ms-reveal,
    .password-input-wrap input::-ms-clear {
      display: none;
    }
    .password-toggle {
      position: absolute;
      right: 4px;
      top: 50%;
      transform: translateY(-50%);
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px;
      height: 32px;
      padding: 0;
      border: 0;
      border-radius: 6px;
      background: transparent;
      color: var(--muted);
      cursor: pointer;
    }
    .password-toggle:hover {
      color: var(--text);
    }
    .password-toggle:focus-visible {
      outline: 2px solid var(--accent);
      outline-offset: 1px;
    }
  `,
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);

  protected readonly submitting = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly showPassword = signal(false);

  protected readonly form = inject(NonNullableFormBuilder).group({
    email: ['', [Validators.required, Validators.pattern(/\S/)]],
    password: ['', Validators.required],
  });

  protected togglePassword(): void {
    this.showPassword.update((show) => !show);
  }

  protected submit(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    const { email, password } = this.form.getRawValue();
    this.submitting.set(true);
    this.error.set(null);
    this.auth.login(email.trim(), password).subscribe({
      next: () => this.router.navigate(['/']),
      error: (e: HttpErrorResponse) => {
        this.error.set(
          e.status === 401
            ? 'Invalid email or password.'
            : e.status === 400
              ? 'Enter your email and password.'
              : "Couldn't sign in. Please try again.",
        );
        this.submitting.set(false);
      },
    });
  }
}
