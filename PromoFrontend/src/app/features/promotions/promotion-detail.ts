import { Component, computed, inject, input, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ApiError, ApiService } from '../../core/api.service';
import { ToastService } from '../../core/toast.service';
import { OfferWizardComponent } from './offer-wizard';
import { Offer, OfferTemplate, PromotionDetail, SaveOfferLocationRequest } from '../../core/models';

type Dialog = 'none' | 'location' | 'copyLocations' | 'cloneOffer' | 'cancelOffer' | 'massUpdate';

@Component({
  selector: 'app-promotion-detail',
  imports: [FormsModule, RouterLink, DatePipe, OfferWizardComponent],
  templateUrl: './promotion-detail.html',
  styleUrl: './promotion-detail.scss'
})
export class PromotionDetailComponent {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly toasts = inject(ToastService);

  /** Bound from the :id route parameter via withComponentInputBinding. */
  readonly id = input.required<string>();

  protected readonly promotion = signal<PromotionDetail | null>(null);
  protected readonly templates = signal<OfferTemplate[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly selectedOfferId = signal<string | null>(null);
  protected readonly checkedOfferIds = signal<Set<string>>(new Set());

  protected readonly wizardOpen = signal(false);
  protected readonly editingOffer = signal<Offer | null>(null);

  protected readonly dialog = signal<Dialog>('none');
  protected readonly dialogError = signal<string | null>(null);

  // Location dialog state
  protected readonly locationLevel = signal<'Zone' | 'LocationList' | 'Store'>('Store');
  protected readonly zoneGroup = signal('');
  protected readonly zone = signal('');
  protected readonly locationList = signal('');
  protected readonly store = signal('');
  protected readonly storeName = signal('');

  // Copy locations / clone / cancel state
  protected readonly copyTargets = signal<Set<string>>(new Set());
  protected readonly cloneDescription = signal('');
  protected readonly cloneStartDate = signal('');
  protected readonly cloneCopyLocations = signal(true);
  protected readonly cancelReason = signal('');

  // Mass update state
  protected readonly massEndDate = signal('');
  protected readonly massCoupon = signal('');
  protected readonly massClearEndDate = signal(false);
  protected readonly massClearCoupon = signal(false);

  protected readonly selectedOffer = computed(() => {
    const id = this.selectedOfferId();
    return this.promotion()?.offers.find(o => o.id === id) ?? null;
  });

  protected readonly checkedOffers = computed(() => {
    const ids = this.checkedOfferIds();
    return this.promotion()?.offers.filter(o => ids.has(o.id)) ?? [];
  });

  protected readonly otherOffers = computed(() => {
    const current = this.selectedOfferId();
    return this.promotion()?.offers.filter(o => o.id !== current) ?? [];
  });

  constructor() {
    this.api.templates().subscribe({
      next: templates => this.templates.set(templates),
      error: () => this.templates.set([])
    });

    queueMicrotask(() => this.load());
  }

  protected load() {
    this.loading.set(true);
    this.error.set(null);

    this.api.promotion(this.id()).subscribe({
      next: promotion => {
        this.promotion.set(promotion);
        this.loading.set(false);

        // Keep a selection so the locations panel always has context.
        const current = this.selectedOfferId();
        const stillExists = promotion.offers.some(o => o.id === current);
        if (!stillExists) {
          this.selectedOfferId.set(promotion.offers[0]?.id ?? null);
        }
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });
  }

  // -----------------------------------------------------------------------
  // Offer selection
  // -----------------------------------------------------------------------

  protected select(offer: Offer) {
    this.selectedOfferId.set(offer.id);
  }

  protected toggleChecked(offer: Offer, event: Event) {
    event.stopPropagation();
    this.checkedOfferIds.update(set => {
      const next = new Set(set);
      next.has(offer.id) ? next.delete(offer.id) : next.add(offer.id);
      return next;
    });
  }

  protected isChecked(offer: Offer) {
    return this.checkedOfferIds().has(offer.id);
  }

  private clearChecked() {
    this.checkedOfferIds.set(new Set());
  }

  // -----------------------------------------------------------------------
  // Wizard
  // -----------------------------------------------------------------------

  protected addOffer() {
    this.editingOffer.set(null);
    this.wizardOpen.set(true);
  }

  protected editOffer(offer: Offer) {
    if (offer.status === 'Approved' || offer.status === 'Active') {
      this.toasts.info('Move it back to worksheet first', `Offer ${offer.offerNumber} is ${offer.status}.`);
      return;
    }

    this.editingOffer.set(offer);
    this.wizardOpen.set(true);
  }

  protected onWizardSaved() {
    this.wizardOpen.set(false);
    this.editingOffer.set(null);
    this.load();
  }

  // -----------------------------------------------------------------------
  // Workflow
  // -----------------------------------------------------------------------

  protected submitSelected() {
    this.runBulk(ids => this.api.submitOffers(ids), 'Submitted for approval');
  }

  protected approveSelected() {
    this.runBulk(ids => this.api.approveOffers(ids), 'Offers approved');
  }

  protected reopenSelected() {
    this.runBulk(ids => this.api.reopenOffers(ids), 'Moved back to worksheet');
  }

  protected deleteSelected() {
    const offers = this.checkedOffers();
    if (!offers.length) return;

    if (!confirm(`Delete ${offers.length} offer(s)? This cannot be undone.`)) return;

    this.busy.set(true);
    this.api.deleteOffers(offers.map(o => o.id)).subscribe({
      next: () => {
        this.busy.set(false);
        this.clearChecked();
        this.toasts.success('Offers deleted');
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.toasts.error('Could not delete', err.message);
      }
    });
  }

  private runBulk(action: (ids: string[]) => Observable<unknown>, message: string) {
    const offers = this.checkedOffers();
    if (!offers.length) {
      this.toasts.info('Select at least one offer first');
      return;
    }

    this.busy.set(true);
    action(offers.map(o => o.id)).subscribe({
      next: () => {
        this.busy.set(false);
        this.clearChecked();
        this.toasts.success(message);
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.toasts.error('Action failed', err.message);
      }
    });
  }

  // -----------------------------------------------------------------------
  // Dialogs
  // -----------------------------------------------------------------------

  protected openDialog(dialog: Dialog) {
    this.dialogError.set(null);

    if (dialog === 'cloneOffer') {
      const offer = this.selectedOffer();
      if (!offer) return;
      this.cloneDescription.set(`${offer.description} (tier)`);
      this.cloneStartDate.set(offer.startDate.slice(0, 10));
      this.cloneCopyLocations.set(true);
    }

    if (dialog === 'copyLocations') {
      this.copyTargets.set(new Set());
    }

    if (dialog === 'cancelOffer') {
      this.cancelReason.set('');
    }

    if (dialog === 'location') {
      this.locationLevel.set('Store');
      this.zoneGroup.set('');
      this.zone.set('');
      this.locationList.set('');
      this.store.set('');
      this.storeName.set('');
    }

    if (dialog === 'massUpdate') {
      this.massEndDate.set('');
      this.massCoupon.set('');
      this.massClearEndDate.set(false);
      this.massClearCoupon.set(false);
    }

    this.dialog.set(dialog);
  }

  protected closeDialog() {
    this.dialog.set('none');
    this.dialogError.set(null);
  }

  protected addLocation() {
    const offer = this.selectedOffer();
    if (!offer) return;

    const level = this.locationLevel();
    const body: SaveOfferLocationRequest = {
      action: 'Include',
      level,
      zoneGroup: level === 'Zone' ? this.zoneGroup() || null : null,
      zone: level === 'Zone' ? this.zone() || null : null,
      locationList: level === 'LocationList' ? this.locationList() || null : null,
      store: level === 'Store' ? this.store() || null : null,
      storeName: level === 'Store' ? this.storeName() || null : null
    };

    const missing =
      (level === 'Zone' && !body.zone) ||
      (level === 'LocationList' && !body.locationList) ||
      (level === 'Store' && !body.store);

    if (missing) {
      this.dialogError.set('Fill in the location for the selected level.');
      return;
    }

    this.busy.set(true);
    this.api.addLocations(offer.id, [body]).subscribe({
      next: () => {
        this.busy.set(false);
        this.closeDialog();
        this.toasts.success('Location added');
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.dialogError.set(err.message);
      }
    });
  }

  protected toggleCopyTarget(offerId: string) {
    this.copyTargets.update(set => {
      const next = new Set(set);
      next.has(offerId) ? next.delete(offerId) : next.add(offerId);
      return next;
    });
  }

  protected copyLocations() {
    const offer = this.selectedOffer();
    const targets = [...this.copyTargets()];

    if (!offer || !targets.length) {
      this.dialogError.set('Choose at least one offer to copy to.');
      return;
    }

    this.busy.set(true);
    this.api.copyLocations(offer.id, targets).subscribe({
      next: copied => {
        this.busy.set(false);
        this.closeDialog();
        this.toasts.success('Locations copied', `${copied} location row(s) added.`);
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.dialogError.set(err.message);
      }
    });
  }

  protected deleteLocation(locationId: string) {
    const offer = this.selectedOffer();
    if (!offer) return;

    this.busy.set(true);
    this.api.deleteLocations(offer.id, [locationId]).subscribe({
      next: () => {
        this.busy.set(false);
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.toasts.error('Could not remove location', err.message);
      }
    });
  }

  protected cloneOffer() {
    const offer = this.selectedOffer();
    if (!offer) return;

    if (!this.cloneDescription().trim()) {
      this.dialogError.set('Enter a description for the new offer.');
      return;
    }

    this.busy.set(true);
    this.api.copyOffer(offer.id, {
      description: this.cloneDescription().trim(),
      startDate: new Date(`${this.cloneStartDate()}T00:00:00`).toISOString(),
      copyLocations: this.cloneCopyLocations()
    }).subscribe({
      next: created => {
        this.busy.set(false);
        this.closeDialog();
        this.toasts.success('Offer created from existing', created.description);
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.dialogError.set(err.message);
      }
    });
  }

  protected cancelOffers() {
    const offers = this.checkedOffers().length ? this.checkedOffers() : [this.selectedOffer()!].filter(Boolean);
    if (!offers.length) return;

    if (!this.cancelReason().trim()) {
      this.dialogError.set('A cancellation reason is required.');
      return;
    }

    this.busy.set(true);
    this.api.cancelOffers(offers.map(o => o.id), this.cancelReason().trim()).subscribe({
      next: () => {
        this.busy.set(false);
        this.closeDialog();
        this.clearChecked();
        this.toasts.success('Offers cancelled');
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.dialogError.set(err.message);
      }
    });
  }

  protected massUpdate() {
    const offers = this.checkedOffers();
    if (!offers.length) {
      this.dialogError.set('Select the offers to update.');
      return;
    }

    this.busy.set(true);
    this.api.massUpdateOffers({
      offerIds: offers.map(o => o.id),
      endDate: this.massEndDate() ? new Date(`${this.massEndDate()}T00:00:00`).toISOString() : null,
      couponCode: this.massCoupon() || null,
      clearEndDate: this.massClearEndDate(),
      clearCouponCode: this.massClearCoupon(),
      clearComments: false,
      clearCustomerDescription: false
    }).subscribe({
      next: result => {
        this.busy.set(false);
        this.closeDialog();
        this.clearChecked();
        this.toasts.success('Offers updated', `${result.updated} offer(s) changed.`);
        this.load();
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.dialogError.set(err.message);
      }
    });
  }

  protected deletePromotion() {
    const promotion = this.promotion();
    if (!promotion) return;

    if (!confirm(`Delete promotion #${promotion.promotionNumber} and all of its offers?`)) return;

    this.busy.set(true);
    this.api.deletePromotion(promotion.id).subscribe({
      next: () => {
        this.busy.set(false);
        this.toasts.success('Promotion deleted');
        void this.router.navigateByUrl('/promotions');
      },
      error: (err: ApiError) => {
        this.busy.set(false);
        this.toasts.error('Could not delete', err.message);
      }
    });
  }

  // -----------------------------------------------------------------------
  // Display helpers
  // -----------------------------------------------------------------------

  protected badgeClass(status: string) {
    return `badge badge--${status.toLowerCase()}`;
  }

  protected rewardSummary(offer: Offer): string {
    const reward = offer.reward;
    if (!reward) return '—';

    switch (reward.discountType) {
      case 'PercentOff': return `${reward.discountValue}% off`;
      case 'AmountOff': return `${reward.discountValue} off${reward.currencyCode ? ' ' + reward.currencyCode : ''}`;
      case 'FixedPrice': return `Fixed price ${reward.discountValue}${reward.currencyCode ? ' ' + reward.currencyCode : ''}`;
      case 'FreeItem': return `Free item ${reward.giftItemId ?? ''}`.trim();
      default: return '—';
    }
  }

  protected conditionSummary(offer: Offer): string {
    if (!offer.conditions.length) return 'No conditions';

    return offer.conditions
      .map(condition =>
        condition.buyQuantity != null
          ? `Buy ${condition.buyQuantity}${condition.unitOfMeasure ? ' ' + condition.unitOfMeasure : ''}`
          : condition.spendAmount != null
            ? `Spend ${condition.spendAmount}${condition.currencyCode ? ' ' + condition.currencyCode : ''}`
            : 'Condition')
      .join(' + ');
  }
}
