import { Component, computed, inject, signal } from '@angular/core';
import { CurrencyPipe } from '@angular/common';
import { Router } from '@angular/router';
import { ApiError, ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { Plan } from '../../core/models';

@Component({
  selector: 'app-plans',
  imports: [CurrencyPipe],
  templateUrl: './plans.html',
  styleUrl: './plans.scss'
})
export class PlansComponent {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  protected readonly plans = signal<Plan[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly selecting = signal<string | null>(null);

  protected readonly isGuest = this.auth.isGuest;
  protected readonly currentPlanCode = computed(() => this.auth.subscription()?.planCode ?? null);

  constructor() {
    this.api.plans().subscribe({
      next: plans => {
        this.plans.set(plans);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });
  }

  /**
   * A guest is sent to collect their customer details first, because the tenant
   * database is named from the email they have not given us yet. A signed-in user
   * subscribes straight away and their database is queued.
   */
  protected choose(plan: Plan) {
    if (this.auth.isGuest()) {
      this.auth.setPendingPlan(plan.code);
      void this.router.navigateByUrl('/get-started');
      return;
    }

    this.selecting.set(plan.code);
    this.error.set(null);

    this.api.subscribe(plan.code).subscribe({
      next: () => {
        this.auth.refresh().subscribe({
          next: () => {
            this.selecting.set(null);
            this.toasts.success(`${plan.name} selected`, 'Preparing your dedicated database.');
            void this.router.navigateByUrl('/workspace');
          },
          error: () => {
            this.selecting.set(null);
            void this.router.navigateByUrl('/workspace');
          }
        });
      },
      error: (err: ApiError) => {
        this.selecting.set(null);
        this.error.set(err.message);
      }
    });
  }

  protected limitLabel(value: number): string {
    return value >= 2147483647 ? 'Unlimited' : value.toLocaleString();
  }
}
