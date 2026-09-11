import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { ToastService } from '../../core/toast.service';
import { Campaign, ProvisioningStatus } from '../../core/models';

@Component({
  selector: 'app-account',
  imports: [RouterLink, DatePipe],
  templateUrl: './account.html',
  styleUrl: './account.scss'
})
export class AccountComponent {
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);
  protected readonly auth = inject(AuthService);

  protected readonly status = signal<ProvisioningStatus | null>(null);
  protected readonly campaigns = signal<Campaign[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly newCode = signal('');
  protected readonly newDescription = signal('');
  protected readonly savingCampaign = signal(false);
  protected readonly campaignError = signal<string | null>(null);

  constructor() {
    this.api.provisioningStatus().subscribe({
      next: status => {
        this.status.set(status);
        this.auth.applyProvisioning(status);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });

    this.loadCampaigns();
  }

  private loadCampaigns() {
    this.api.campaigns().subscribe({
      next: campaigns => this.campaigns.set(campaigns),
      error: () => this.campaigns.set([])
    });
  }

  protected addCampaign() {
    const code = this.newCode().trim().toUpperCase();
    const description = this.newDescription().trim();

    if (!code || !description) {
      this.campaignError.set('Enter both a code and a description.');
      return;
    }

    this.savingCampaign.set(true);
    this.campaignError.set(null);

    this.api.createCampaign(code, description).subscribe({
      next: () => {
        this.savingCampaign.set(false);
        this.newCode.set('');
        this.newDescription.set('');
        this.toasts.success('Campaign added', code);
        this.loadCampaigns();
      },
      error: (err: ApiError) => {
        this.savingCampaign.set(false);
        this.campaignError.set(err.message);
      }
    });
  }

  protected deleteCampaign(campaign: Campaign) {
    this.api.deleteCampaign(campaign.id).subscribe({
      next: () => {
        this.toasts.success('Campaign removed', campaign.code);
        this.loadCampaigns();
      },
      error: (err: ApiError) => this.toasts.error('Could not remove campaign', err.message)
    });
  }
}
