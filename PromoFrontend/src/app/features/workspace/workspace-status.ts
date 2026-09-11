import { Component, OnDestroy, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { ProvisioningStatus } from '../../core/models';

/**
 * Shown while the tenant database is being created. Provisioning happens on a
 * background worker in the API, so the client polls until the workspace reports ready
 * (seconds for a database on a shared instance, minutes for a dedicated RDS instance).
 */
@Component({
  selector: 'app-workspace-status',
  imports: [RouterLink],
  templateUrl: './workspace-status.html',
  styleUrl: './workspace-status.scss'
})
export class WorkspaceStatusComponent implements OnDestroy {
  private readonly api = inject(ApiService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  private timer?: ReturnType<typeof setTimeout>;
  private attempts = 0;

  protected readonly status = signal<ProvisioningStatus | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly retrying = signal(false);

  protected readonly stages = [
    { key: 'Queued', label: 'Request queued', detail: 'Your subscription was recorded.' },
    { key: 'Provisioning', label: 'Creating database', detail: 'Allocating an isolated database for your tenant.' },
    { key: 'Ready', label: 'Applying schema', detail: 'Promotions, offers, items and locations.' }
  ];

  constructor() {
    this.poll();
  }

  ngOnDestroy() {
    if (this.timer) clearTimeout(this.timer);
  }

  protected stageState(key: string): 'done' | 'active' | 'pending' {
    const current = this.status()?.status ?? 'Queued';
    const order = ['NotRequested', 'Queued', 'Provisioning', 'Ready'];
    const currentIndex = order.indexOf(current);
    const stageIndex = order.indexOf(key);

    if (current === 'Ready') return 'done';
    if (stageIndex < currentIndex) return 'done';
    if (stageIndex === currentIndex) return 'active';
    return 'pending';
  }

  protected retry() {
    this.retrying.set(true);
    this.error.set(null);

    this.api.retryProvisioning().subscribe({
      next: () => {
        this.retrying.set(false);
        this.attempts = 0;
        this.poll();
      },
      error: (err: ApiError) => {
        this.retrying.set(false);
        this.error.set(err.message);
      }
    });
  }

  private poll() {
    this.api.provisioningStatus().subscribe({
      next: status => {
        this.status.set(status);
        this.auth.applyProvisioning(status);

        if (status.isReady) {
          this.toasts.success('Workspace ready', `Database ${status.databaseName} is live.`);
          this.timer = setTimeout(() => void this.router.navigateByUrl('/promotions'), 900);
          return;
        }

        if (status.status === 'Failed') {
          this.error.set(status.error ?? 'Provisioning failed.');
          return;
        }

        // Back off gently: quick at first, then every few seconds for slower
        // dedicated-instance provisioning.
        this.attempts++;
        const delay = this.attempts < 10 ? 1000 : 4000;
        this.timer = setTimeout(() => this.poll(), delay);
      },
      error: (err: ApiError) => this.error.set(err.message)
    });
  }
}
