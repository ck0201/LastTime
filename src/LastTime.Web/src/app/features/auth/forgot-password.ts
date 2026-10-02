import { HttpErrorResponse } from '@angular/common/http';
import { Component, inject, signal } from '@angular/core';
import { NonNullableFormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';
import { passwordValidators } from '../../core/password-rules';

type Step = 'email' | 'code' | 'password' | 'done';

/**
 * Forgotten password, in one screen: email, then the emailed 6-digit code, then a new password.
 * The reset token the API hands out after the code is kept only in this component's memory (never in storage),
 * so reloading the page simply starts the flow again.
 */
@Component({
  selector: 'app-forgot-password',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './forgot-password.html',
})
export class ForgotPassword {
  private readonly auth = inject(Auth);
  private readonly fb = inject(NonNullableFormBuilder);

  protected readonly step = signal<Step>('email');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly emailForm = this.fb.group({
    email: ['', [Validators.required, Validators.email, Validators.maxLength(320)]],
  });
  protected readonly codeForm = this.fb.group({
    code: ['', [Validators.required, Validators.pattern(/^\s*\d{6}\s*$/)]],
  });
  protected readonly passwordForm = this.fb.group({
    password: ['', passwordValidators],
  });

  private email = '';
  private resetToken: string | null = null; // in memory only

  protected requestCode(): void {
    if (this.emailForm.invalid) {
      this.emailForm.markAllAsTouched();
      return;
    }

    this.email = this.emailForm.controls.email.value.trim();
    this.busy.set(true);
    this.error.set(null);
    this.auth.forgotPassword(this.email).subscribe({
      // The API answers the same for every address, and so does this screen.
      next: () => this.moveTo('code'),
      error: () => this.fail("Couldn't send the code. Please try again."),
    });
  }

  protected verifyCode(): void {
    if (this.codeForm.invalid) {
      this.codeForm.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.auth.verifyPasswordReset(this.email, this.codeForm.controls.code.value.trim()).subscribe({
      next: (token) => {
        this.resetToken = token;
        this.moveTo('password');
      },
      error: (e: HttpErrorResponse) =>
        this.fail(
          e.status === 400
            ? "That code isn't right or has expired. Check it, or start again to get a new one."
            : "Couldn't check the code. Please try again.",
        ),
    });
  }

  protected resetPassword(): void {
    if (this.passwordForm.invalid || !this.resetToken) {
      this.passwordForm.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);
    this.auth.resetPassword(this.email, this.resetToken, this.passwordForm.controls.password.value).subscribe({
      next: () => {
        this.resetToken = null; // single use; nothing left to keep
        this.passwordForm.reset();
        this.moveTo('done');
      },
      error: (e: HttpErrorResponse) => {
        if (e.status === 400) {
          // Rejected token (expired or already used): it cannot work again, so begin again.
          this.resetToken = null;
          this.startOver("This reset has expired. Please start again to get a new code.");
        } else {
          this.fail("Couldn't change your password. Please try again.");
        }
      },
    });
  }

  /** Back to the first step (for a different email, or a fresh code). */
  protected startOver(message: string | null = null): void {
    this.resetToken = null;
    this.codeForm.reset();
    this.passwordForm.reset();
    this.busy.set(false);
    this.error.set(message);
    this.step.set('email');
  }

  private moveTo(step: Step): void {
    this.busy.set(false);
    this.step.set(step);
  }

  private fail(message: string): void {
    this.busy.set(false);
    this.error.set(message);
  }
}
