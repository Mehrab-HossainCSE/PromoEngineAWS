import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';

type Mode = 'signIn' | 'register';

@Component({
  selector: 'app-sign-in',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './sign-in.html',
  styleUrl: './sign-in.scss'
})
export class SignInComponent {
  private readonly fb = inject(FormBuilder);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly mode = signal<Mode>('signIn');
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly signInForm = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]]
  });

  protected readonly registerForm = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    displayName: ['', [Validators.required]],
    companyName: ['', [Validators.required]]
  });

  protected setMode(mode: Mode) {
    this.mode.set(mode);
    this.error.set(null);
  }

  protected submit() {
    this.error.set(null);

    if (this.mode() === 'signIn') {
      if (this.signInForm.invalid) {
        this.signInForm.markAllAsTouched();
        return;
      }

      this.busy.set(true);
      this.auth.login(this.signInForm.getRawValue()).subscribe({
        next: () => {
          this.busy.set(false);
          this.routeAfterAuth();
        },
        error: (err: ApiError) => {
          this.busy.set(false);
          this.error.set(err.message);
        }
      });
      return;
    }

    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.auth.register(this.registerForm.getRawValue()).subscribe({
      next: () => {
        this.busy.set(false);
        this.toasts.success('Account created', 'Choose a subscription to provision your database.');
        // A new account has no subscription yet, so the plan picker is the next step.
        void this.router.navigateByUrl('/plans');
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err.message);
      }
    });
  }

  /** Sends the user to the first screen that is actually useful to them. */
  private routeAfterAuth() {
    if (!this.auth.hasActiveSubscription()) {
      void this.router.navigateByUrl('/plans');
      return;
    }

    void this.router.navigateByUrl(this.auth.workspaceReady() ? '/promotions' : '/workspace');
  }
}
