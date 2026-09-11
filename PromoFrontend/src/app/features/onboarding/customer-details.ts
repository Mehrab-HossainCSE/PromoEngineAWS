import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { Plan } from '../../core/models';

/**
 * Guests reach this screen after picking a plan. Everything here is required before a
 * tenant can exist: the email names the database, and the company and contact details
 * are stored against the tenant record.
 */
@Component({
  selector: 'app-customer-details',
  imports: [ReactiveFormsModule, RouterLink],
  templateUrl: './customer-details.html',
  styleUrl: './customer-details.scss'
})
export class CustomerDetailsComponent {
  private readonly fb = inject(FormBuilder);
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly plan = signal<Plan | null>(null);

  protected readonly form = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    contactName: ['', [Validators.required]],
    companyName: ['', [Validators.required]],
    contactPhone: [''],
    country: ['United States'],
    currencyCode: ['USD', [Validators.required]]
  });

  protected readonly currencies = ['USD', 'EUR', 'GBP', 'CAD', 'AUD', 'JPY', 'INR', 'BDT'];

  constructor() {
    const pending = this.auth.pendingPlanCode();

    if (!pending) {
      void this.router.navigateByUrl('/plans');
      return;
    }

    this.api.plans().subscribe({
      next: plans => this.plan.set(plans.find(p => p.code === pending) ?? null),
      error: () => this.plan.set(null)
    });
  }

  protected submit() {
    const planCode = this.auth.pendingPlanCode();
    if (!planCode) {
      void this.router.navigateByUrl('/plans');
      return;
    }

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.busy.set(true);
    this.error.set(null);

    this.auth.convertGuest({ ...this.form.getRawValue(), planCode }).subscribe({
      next: () => {
        this.busy.set(false);
        this.toasts.success('Workspace requested', 'Creating your dedicated database now.');
        void this.router.navigateByUrl('/workspace');
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.error.set(err.message);
      }
    });
  }
}
