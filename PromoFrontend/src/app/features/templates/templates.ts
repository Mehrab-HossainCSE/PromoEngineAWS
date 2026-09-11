import { Component, computed, inject, signal } from '@angular/core';
import { ApiError, ApiService } from '../../core/api.service';
import { AuthService } from '../../core/auth.service';
import { OfferTemplate } from '../../core/models';

@Component({
  selector: 'app-templates',
  templateUrl: './templates.html',
  styleUrl: './templates.scss'
})
export class TemplatesComponent {
  private readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);

  protected readonly templates = signal<OfferTemplate[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  protected readonly onlyAvailable = signal(false);

  protected readonly visible = computed(() =>
    this.onlyAvailable() ? this.templates().filter(t => t.availableOnCurrentPlan) : this.templates());

  protected readonly itemTemplates = computed(() => this.visible().filter(t => t.level === 'Item'));
  protected readonly transactionTemplates = computed(() => this.visible().filter(t => t.level === 'Transaction'));

  constructor() {
    this.api.templates().subscribe({
      next: templates => {
        this.templates.set(templates);
        this.loading.set(false);
      },
      error: (err: ApiError) => {
        this.error.set(err.message);
        this.loading.set(false);
      }
    });
  }

  /** Plain-language summary of what the template asks the user for. */
  protected conditionText(template: OfferTemplate): string {
    switch (template.conditionKind) {
      case 'BuyQuantity':
        return template.allowsMultipleConditions
          ? 'Two or more buy quantities'
          : 'A buy quantity';
      case 'SpendAmount':
        return 'A spend threshold';
      default:
        return 'None';
    }
  }

  protected rewardText(template: OfferTemplate): string {
    if (template.rewardKind === 'GiftItem') return 'A free item';

    const kinds = template.allowsFixedPrice
      ? 'Percent off, amount off or fixed price'
      : 'Percent off or amount off';

    return template.rewardItemMode === 'DiscountedItems'
      ? `${kinds}, on a separate reward item list`
      : template.rewardItemMode === 'ExcludedItems'
        ? `${kinds}, off the whole transaction`
        : `${kinds}, on the qualifying items`;
  }
}
