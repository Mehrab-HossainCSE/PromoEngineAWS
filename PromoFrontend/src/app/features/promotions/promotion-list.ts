import { Component, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError, ApiService } from '../../core/api.service';
import { ToastService } from '../../core/toast.service';
import { Paged, PromotionSearchRequest, PromotionSummary } from '../../core/models';

@Component({
  selector: 'app-promotion-list',
  imports: [ReactiveFormsModule, RouterLink, DatePipe],
  templateUrl: './promotion-list.html',
  styleUrl: './promotion-list.scss'
})
export class PromotionListComponent {
  private readonly api = inject(ApiService);
  private readonly fb = inject(FormBuilder);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly toasts = inject(ToastService);

  protected readonly result = signal<Paged<PromotionSummary> | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly showFilters = signal(false);

  protected readonly createOpen = signal(false);
  protected readonly creating = signal(false);
  protected readonly createError = signal<string | null>(null);
  protected readonly newDescription = signal('');

  protected readonly statuses = [
    'Worksheet', 'Submitted', 'Approved', 'Rejected', 'Active', 'Completed', 'Cancelled'
  ];

  protected readonly filters = this.fb.group({
    promotionNumber: [null as number | null],
    description: [''],
    offerDescription: [''],
    status: [''],
    itemId: [''],
    startDate: ['']
  });

  constructor() {
    // The sidebar "create promotion" link arrives with ?create=1.
    if (this.route.snapshot.queryParamMap.get('create')) {
      this.createOpen.set(true);
    }

    this.load();
  }

  protected load() {
    this.loading.set(true);
    this.error.set(null);

    this.api.promotions(1, 50).subscribe({
      next: page => {
        this.result.set(page);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });
  }

  protected search() {
    const raw = this.filters.getRawValue();

    const request: PromotionSearchRequest = {
      promotionNumber: raw.promotionNumber || null,
      description: raw.description || null,
      offerDescription: raw.offerDescription || null,
      status: raw.status || null,
      itemId: raw.itemId || null,
      startDate: raw.startDate ? new Date(raw.startDate).toISOString() : null,
      page: 1,
      pageSize: 50
    };

    // The API requires at least one criterion; an empty form means "show everything".
    const hasCriteria = Object.entries(request).some(
      ([key, value]) => !['page', 'pageSize'].includes(key) && value !== null && value !== ''
    );

    if (!hasCriteria) {
      this.load();
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.api.searchPromotions(request).subscribe({
      next: page => {
        this.result.set(page);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });
  }

  protected clearFilters() {
    this.filters.reset({
      promotionNumber: null, description: '', offerDescription: '', status: '', itemId: '', startDate: ''
    });
    this.load();
  }

  protected openCreate() {
    this.newDescription.set('');
    this.createError.set(null);
    this.createOpen.set(true);
  }

  protected closeCreate() {
    this.createOpen.set(false);
    void this.router.navigate([], { queryParams: {}, replaceUrl: true });
  }

  protected create() {
    const description = this.newDescription().trim();
    if (!description) {
      this.createError.set('Enter a promotion description.');
      return;
    }

    this.creating.set(true);
    this.createError.set(null);

    this.api.createPromotion(description).subscribe({
      next: promotion => {
        this.creating.set(false);
        this.createOpen.set(false);
        this.toasts.success('Promotion created', `#${promotion.promotionNumber} ${promotion.description}`);
        void this.router.navigate(['/promotions', promotion.id]);
      },
      error: (err: ApiError) => {
        this.creating.set(false);
        this.createError.set(err.message);
      }
    });
  }

  protected badgeClass(status: string) {
    return `badge badge--${status.toLowerCase()}`;
  }
}
