import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../core/api.service';
import { ToastService } from '../../core/toast.service';
import { ItemSelectorComponent } from './item-selector';
import {
  ApplyToPriceType, DiscountType, DistributionRule, Offer, OfferProduct, OfferProductRow,
  OfferTemplate, PriceRestrictionOperator, SaveConditionRequest, SaveOfferItemRequest,
  SaveOfferRequest
} from '../../core/models';

interface ConditionDraft extends SaveConditionRequest {
  items: SaveOfferItemRequest[];
}

type Step = 'offer' | 'conditions' | 'rewards';

/** One server-side validation failure, resolved to the stop and field the user sees. */
interface OfferProblem {
  step: Step | null;
  field: string;
  message: string;
}

/**
 * The validator reports failures against the request shape - "Reward.DiscountValue",
 * "Conditions[0].BuyQuantity". Those names mean nothing to someone looking at the
 * wizard, so each one is mapped to the stop it lives on and the label printed above
 * the input, which is what makes "the offer does not match its template" actionable.
 */
const FIELD_LABELS: Record<string, { step: Step | null; label: string }> = {
  Description: { step: 'offer', label: 'Offer description' },
  TemplateCode: { step: 'offer', label: 'Template' },
  StartDate: { step: 'offer', label: 'Start date' },
  EndDate: { step: 'offer', label: 'End date' },
  IsEmergency: { step: 'offer', label: 'Emergency offer' },
  DistributionRule: { step: 'offer', label: 'Distribution rule' },
  Conditions: { step: 'conditions', label: 'Conditions' },
  Reward: { step: 'rewards', label: 'Reward' },
  RewardItems: { step: 'rewards', label: 'Discounted items' },
  'Reward.DiscountType': { step: 'rewards', label: 'Discount type' },
  'Reward.DiscountValue': { step: 'rewards', label: 'Discount' },
  'Reward.CurrencyCode': { step: 'rewards', label: 'Currency' },
  'Reward.GiftItemId': { step: 'rewards', label: 'Gift item' },
  'Reward.SingleItemId': { step: 'rewards', label: 'Item' },
  'Reward.ApplyDiscountUpTo': { step: 'rewards', label: 'Apply discount up to' }
};

const CONDITION_LABELS: Record<string, string> = {
  BuyQuantity: 'Buy quantity',
  UnitOfMeasure: 'Unit of measure',
  SpendAmount: 'Spend amount',
  CurrencyCode: 'Currency',
  PriceRestriction: 'Price restriction',
  PriceRestrictionFrom: 'Price restriction from',
  PriceRestrictionTo: 'Price restriction to',
  Items: 'Qualifying items'
};

/**
 * The three stop wizard: Offer, Conditions, Rewards. The selected template decides
 * which stops exist and which fields each one shows, so one component covers all
 * thirteen templates instead of thirteen bespoke forms.
 */
@Component({
  selector: 'app-offer-wizard',
  imports: [FormsModule, ItemSelectorComponent],
  templateUrl: './offer-wizard.html',
  styleUrl: './offer-wizard.scss'
})
export class OfferWizardComponent {
  private readonly api = inject(ApiService);
  private readonly toasts = inject(ToastService);

  readonly promotionId = input.required<string>();
  readonly templates = input.required<OfferTemplate[]>();
  /** Present when editing; absent when adding. */
  readonly existing = input<Offer | null>(null);

  readonly saved = output<Offer>();
  readonly cancelled = output<void>();

  protected readonly step = signal<Step>('offer');
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly problems = signal<OfferProblem[]>([]);

  // ---- offer stop -------------------------------------------------------
  protected readonly description = signal('');
  protected readonly templateCode = signal('');
  protected readonly startDate = signal(defaultStart());
  protected readonly startTime = signal('');
  protected readonly endDate = signal('');
  protected readonly endTime = signal('');
  protected readonly comments = signal('');
  protected readonly couponCode = signal('');
  protected readonly couponRequired = signal(false);
  protected readonly distributionRule = signal<DistributionRule>('BothBuyAndGetItems');
  protected readonly exclusiveDiscount = signal(false);
  protected readonly customerDescription = signal('');
  protected readonly isEmergency = signal(false);

  // ---- conditions stop --------------------------------------------------
  protected readonly conditions = signal<ConditionDraft[]>([]);

  // ---- rewards stop -----------------------------------------------------
  protected readonly discountType = signal<DiscountType>('PercentOff');
  protected readonly discountValue = signal<number | null>(null);
  protected readonly allCurrencies = signal(true);
  protected readonly currencyCode = signal('USD');
  protected readonly applyTo = signal<ApplyToPriceType>('RegularAndClearance');
  protected readonly applyUpTo = signal<number | null>(null);
  protected readonly getQuantity = signal<number | null>(null);
  protected readonly giftItemId = signal('');
  protected readonly giftItemDescription = signal('');
  protected readonly singleItemId = signal('');
  protected readonly singleItemDescription = signal('');
  protected readonly rewardItems = signal<SaveOfferItemRequest[]>([]);

  /**
   * The offer's own uploaded barcode sheet. It lives on the wizard rather than in
   * the item selector so that it survives a template change, which resets the item
   * rules: the products the offer covers do not stop being those products because
   * the merchandiser picked a different template.
   */
  protected readonly products = signal<OfferProductRow[]>([]);

  protected readonly currencies = ['USD', 'EUR', 'GBP', 'CAD', 'AUD', 'JPY', 'INR', 'BDT'];
  protected readonly unitsOfMeasure = ['EA', 'KG', 'LB', 'L', 'M'];

  /** Templates the caller's plan actually unlocks, grouped for the picker. */
  protected readonly availableTemplates = computed(() =>
    this.templates().filter(t => t.availableOnCurrentPlan));

  protected readonly itemTemplates = computed(() =>
    this.availableTemplates().filter(t => t.level === 'Item'));

  protected readonly transactionTemplates = computed(() =>
    this.availableTemplates().filter(t => t.level === 'Transaction'));

  protected readonly template = computed(() =>
    this.templates().find(t => t.code === this.templateCode()) ?? null);

  protected readonly isEdit = computed(() => !!this.existing());

  /** Conditions is skipped for simple discount templates. */
  protected readonly steps = computed<Step[]>(() => {
    const template = this.template();
    return template?.hasConditions ? ['offer', 'conditions', 'rewards'] : ['offer', 'rewards'];
  });

  protected readonly stepIndex = computed(() => this.steps().indexOf(this.step()));
  protected readonly isLastStep = computed(() => this.stepIndex() === this.steps().length - 1);

  protected readonly discountTypes = computed<{ value: DiscountType; label: string }[]>(() => {
    const template = this.template();
    const options: { value: DiscountType; label: string }[] = [
      { value: 'PercentOff', label: 'Percent off' },
      { value: 'AmountOff', label: 'Amount off' }
    ];

    if (template?.allowsFixedPrice) {
      options.push({ value: 'FixedPrice', label: 'Fixed price' });
    }

    return options;
  });

  constructor() {
    // Prefill when editing an existing offer.
    queueMicrotask(() => {
      const offer = this.existing();
      if (offer) {
        this.hydrate(offer);
      } else if (!this.templateCode() && this.availableTemplates().length) {
        this.templateCode.set(this.availableTemplates()[0].code);
      }
    });
  }

  // -----------------------------------------------------------------------
  // Navigation
  // -----------------------------------------------------------------------

  protected goTo(step: Step) {
    if (this.steps().includes(step)) {
      this.step.set(step);
    }
  }

  protected next() {
    const steps = this.steps();
    const index = this.stepIndex();

    if (this.step() === 'offer') {
      if (!this.description().trim()) {
        this.error.set('Enter an offer description.');
        return;
      }
      if (!this.templateCode()) {
        this.error.set('Choose an offer template.');
        return;
      }

      this.ensureConditionShape();
    }

    this.error.set(null);

    if (index < steps.length - 1) {
      this.step.set(steps[index + 1]);
    }
  }

  protected back() {
    const index = this.stepIndex();
    if (index > 0) {
      this.step.set(this.steps()[index - 1]);
    }
  }

  protected selectTemplate(code: string) {
    if (code === this.templateCode()) return;

    this.templateCode.set(code);
    this.error.set(null);

    const template = this.templates().find(t => t.code === code);
    if (!template) return;

    // A different template means a different shape, so reset the dependent stops.
    this.ensureConditionShape();
    this.rewardItems.set([]);

    if (!template.allowsFixedPrice && this.discountType() === 'FixedPrice') {
      this.discountType.set('PercentOff');
    }

    if (template.rewardKind === 'GiftItem') {
      this.discountType.set('FreeItem');
    } else if (this.discountType() === 'FreeItem') {
      this.discountType.set('PercentOff');
    }

    if (!template.allowsApplyDiscountUpTo) {
      this.applyUpTo.set(null);
    }
  }

  // -----------------------------------------------------------------------
  // Conditions
  // -----------------------------------------------------------------------

  /** Buy X and Y templates need at least two conditions; the rest need exactly one. */
  private ensureConditionShape() {
    const template = this.template();
    if (!template) return;

    if (!template.hasConditions) {
      this.conditions.set([]);
      return;
    }

    const minimum = template.allowsMultipleConditions ? 2 : 1;
    const current = this.conditions();

    if (current.length < minimum) {
      const additions = Array.from(
        { length: minimum - current.length },
        (_, i) => newCondition(current.length + i + 1)
      );
      this.conditions.set([...current, ...additions]);
    } else if (!template.allowsMultipleConditions && current.length > 1) {
      this.conditions.set([current[0]]);
    }
  }

  protected addCondition() {
    this.conditions.update(list => [...list, newCondition(list.length + 1)]);
  }

  protected removeCondition(index: number) {
    const template = this.template();
    const minimum = template?.allowsMultipleConditions ? 2 : 1;

    if (this.conditions().length <= minimum) return;

    this.conditions.update(list =>
      list.filter((_, i) => i !== index).map((condition, i) => ({ ...condition, sequence: i + 1 }))
    );
  }

  protected patchCondition(index: number, patch: Partial<ConditionDraft>) {
    this.conditions.update(list =>
      list.map((condition, i) => (i === index ? { ...condition, ...patch } : condition))
    );
  }

  protected conditionItems(index: number): SaveOfferItemRequest[] {
    return this.conditions()[index]?.items ?? [];
  }

  protected setConditionItems(index: number, items: SaveOfferItemRequest[]) {
    this.patchCondition(index, { items });
  }

  // -----------------------------------------------------------------------
  // Save
  // -----------------------------------------------------------------------

  protected save() {
    const template = this.template();
    if (!template) {
      this.error.set('Choose an offer template.');
      return;
    }

    this.saving.set(true);
    this.error.set(null);
    this.problems.set([]);

    const body = this.build(template);
    const request = this.isEdit()
      ? this.api.updateOffer(this.existing()!.id, body)
      : this.api.addOffer(this.promotionId(), body);

    request.subscribe({
      next: offer => {
        this.saving.set(false);
        this.toasts.success(this.isEdit() ? 'Offer updated' : 'Offer added', offer.description);
        this.saved.emit(offer);
      },
      error: (err: ApiError) => {
        this.saving.set(false);

        const problems = toProblems(err);
        this.problems.set(problems);

        if (problems.length === 0) {
          this.error.set(err.message);
          return;
        }

        this.error.set(problems.length === 1
          ? 'One detail needs fixing before this offer can be saved:'
          : `${problems.length} details need fixing before this offer can be saved:`);

        // Move to the first stop that has a problem, so the field being complained
        // about is actually on screen rather than one click away on another stop.
        const target = problems.find(p => p.step && this.steps().includes(p.step))?.step;
        if (target) {
          this.step.set(target);
        }
      }
    });
  }

  private build(template: OfferTemplate): SaveOfferRequest {
    const conditions: SaveConditionRequest[] = template.hasConditions
      ? this.conditions().map((condition, index) => ({
          sequence: index + 1,
          buyQuantity: template.conditionKind === 'BuyQuantity' ? numberOrNull(condition.buyQuantity) : null,
          unitOfMeasure: template.conditionKind === 'BuyQuantity' ? condition.unitOfMeasure || null : null,
          spendAmount: template.conditionKind === 'SpendAmount' ? numberOrNull(condition.spendAmount) : null,
          currencyCode: condition.currencyCode || null,
          priceRestriction: template.conditionPriceRestriction ? condition.priceRestriction ?? 'None' : 'None',
          priceRestrictionFrom: template.conditionPriceRestriction ? numberOrNull(condition.priceRestrictionFrom) : null,
          priceRestrictionTo: template.conditionPriceRestriction ? numberOrNull(condition.priceRestrictionTo) : null,
          priceRestrictionCurrency: template.conditionPriceRestriction ? condition.priceRestrictionCurrency || null : null,
          items: template.hasConditionItems ? condition.items : []
        }))
      : [];

    return {
      description: this.description().trim(),
      templateCode: template.code,
      startDate: toIso(this.startDate()),
      startTime: this.startTime() ? `${this.startTime()}:00` : null,
      endDate: this.endDate() ? toIso(this.endDate()) : null,
      endTime: this.endTime() ? `${this.endTime()}:00` : null,
      comments: this.comments() || null,
      couponCode: this.couponCode() || null,
      couponCodeRequired: this.couponRequired(),
      distributionRule: template.allowsDistributionRule ? this.distributionRule() : null,
      exclusiveDiscount: this.exclusiveDiscount(),
      customerDescription: this.customerDescription() || null,
      isEmergency: this.isEmergency(),
      conditions,
      reward: {
        discountType: template.rewardKind === 'GiftItem' ? 'FreeItem' : this.discountType(),
        discountValue: template.rewardKind === 'GiftItem' ? null : numberOrNull(this.discountValue()),
        currencyCode: this.allCurrencies() ? null : this.currencyCode(),
        appliesToAllCurrencies: this.allCurrencies(),
        applyTo: template.allowsApplyTo ? this.applyTo() : null,
        applyDiscountUpTo: template.allowsApplyDiscountUpTo ? numberOrNull(this.applyUpTo()) : null,
        getQuantity: numberOrNull(this.getQuantity()),
        giftItemId: template.rewardKind === 'GiftItem' ? this.giftItemId() || null : null,
        giftItemDescription: template.rewardKind === 'GiftItem' ? this.giftItemDescription() || null : null,
        singleItemId: template.conditionSingleItem ? this.singleItemId() || null : null,
        singleItemDescription: template.conditionSingleItem ? this.singleItemDescription() || null : null
      },
      rewardItems: template.rewardItemMode === 'None' ? [] : this.rewardItems(),
      products: this.products()
    };
  }

  private hydrate(offer: Offer) {
    this.templateCode.set(offer.templateCode);
    this.description.set(offer.description);
    this.startDate.set(offer.startDate.slice(0, 10));
    this.startTime.set(offer.startTime ? offer.startTime.slice(0, 5) : '');
    this.endDate.set(offer.endDate ? offer.endDate.slice(0, 10) : '');
    this.endTime.set(offer.endTime ? offer.endTime.slice(0, 5) : '');
    this.comments.set(offer.comments ?? '');
    this.couponCode.set(offer.couponCode ?? '');
    this.couponRequired.set(offer.couponCodeRequired);
    this.distributionRule.set(
      offer.distributionRule === 'NotApplicable' ? 'BothBuyAndGetItems' : offer.distributionRule
    );
    this.exclusiveDiscount.set(offer.exclusiveDiscount);
    this.customerDescription.set(offer.customerDescription ?? '');
    this.isEmergency.set(offer.isEmergency);

    this.conditions.set(offer.conditions.map(condition => ({
      sequence: condition.sequence,
      buyQuantity: condition.buyQuantity ?? null,
      unitOfMeasure: condition.unitOfMeasure ?? 'EA',
      spendAmount: condition.spendAmount ?? null,
      currencyCode: condition.currencyCode ?? null,
      priceRestriction: condition.priceRestriction,
      priceRestrictionFrom: condition.priceRestrictionFrom ?? null,
      priceRestrictionTo: condition.priceRestrictionTo ?? null,
      priceRestrictionCurrency: condition.priceRestrictionCurrency ?? null,
      items: condition.items.map(toSaveItem)
    })));

    if (offer.reward) {
      const reward = offer.reward;
      this.discountType.set(reward.discountType);
      this.discountValue.set(reward.discountValue ?? null);
      this.allCurrencies.set(reward.appliesToAllCurrencies);
      this.currencyCode.set(reward.currencyCode ?? 'USD');
      this.applyTo.set(reward.applyTo);
      this.applyUpTo.set(reward.applyDiscountUpTo ?? null);
      this.getQuantity.set(reward.getQuantity ?? null);
      this.giftItemId.set(reward.giftItemId ?? '');
      this.giftItemDescription.set(reward.giftItemDescription ?? '');
      this.singleItemId.set(reward.singleItemId ?? '');
      this.singleItemDescription.set(reward.singleItemDescription ?? '');
    }

    this.rewardItems.set(offer.rewardItems.map(toSaveItem));
    this.products.set(offer.products.map(toProductRow));
  }

  protected priceRestrictionOptions: { value: PriceRestrictionOperator; label: string }[] = [
    { value: 'None', label: 'No restriction' },
    { value: 'GreaterThan', label: 'Greater than' },
    { value: 'LessThan', label: 'Less than' },
    { value: 'Between', label: 'Between' }
  ];

  protected stepLabel(step: Step) {
    return step === 'offer' ? 'Offer' : step === 'conditions' ? 'Conditions' : 'Rewards';
  }

  protected numeric(value: string): number | null {
    const parsed = Number(value);
    return value === '' || Number.isNaN(parsed) ? null : parsed;
  }
}

function newCondition(sequence: number): ConditionDraft {
  return {
    sequence,
    buyQuantity: null,
    unitOfMeasure: 'EA',
    spendAmount: null,
    currencyCode: null,
    priceRestriction: 'None',
    priceRestrictionFrom: null,
    priceRestrictionTo: null,
    priceRestrictionCurrency: null,
    items: []
  };
}

/**
 * A stored sheet row back into the shape the wizard edits and saves. The original
 * spreadsheet row number is kept so a re-saved offer still says which line of which
 * file each barcode came from.
 */
function toProductRow(product: OfferProduct): OfferProductRow {
  return {
    rowNumber: product.sourceRowNumber ?? 0,
    barcode: product.barcode,
    itemId: product.itemId,
    styleCode: product.styleCode,
    itemDescription: product.itemDescription,
    department: product.department,
    class: product.class,
    subclass: product.subclass,
    brand: product.brand,
    vendorName: product.vendorName,
    supplierSite: product.supplierSite,
    action: product.action,
    getApplicablePromotions: product.getApplicablePromotions,
    promoTypeId: product.promoTypeId,
    siteCode: product.siteCode,
    customerTier: product.customerTier,
    sourceFileName: product.sourceFileName
  };
}

function toSaveItem(item: {
  action: 'Include' | 'Exclude'; level: SaveOfferItemRequest['level'];
  department?: string; class?: string; subclass?: string; itemId?: string; itemDescription?: string;
  barcode?: string; styleCode?: string; vendorName?: string; sourceRowNumber?: number;
  parentItemId?: string; diffType?: string; diffValue?: string; itemListId?: string;
  sourceFileName?: string; supplierSite?: string; brand?: string;
}): SaveOfferItemRequest {
  return {
    action: item.action,
    level: item.level,
    department: item.department,
    class: item.class,
    subclass: item.subclass,
    itemId: item.itemId,
    itemDescription: item.itemDescription,
    barcode: item.barcode,
    styleCode: item.styleCode,
    vendorName: item.vendorName,
    sourceRowNumber: item.sourceRowNumber,
    parentItemId: item.parentItemId,
    diffType: item.diffType,
    diffValue: item.diffValue,
    itemListId: item.itemListId,
    sourceFileName: item.sourceFileName,
    supplierSite: item.supplierSite,
    brand: item.brand
  };
}

/**
 * Turns an API failure into per-field problems. Every message the validator sent is
 * kept: a lone failure used to be dropped in favour of the generic title, which is
 * how "the offer does not match its template" ended up being all the user was told.
 */
function toProblems(err: ApiError): OfferProblem[] {
  return Object.entries(err.fieldErrors).flatMap(([key, messages]) => {
    const { step, label } = describeField(key);
    return messages.map(message => ({ step, field: label, message }));
  });
}

/** Resolves a validation key such as "Conditions[0].BuyQuantity" to a stop and label. */
function describeField(key: string): { step: Step | null; label: string } {
  const condition = /^Conditions\[(\d+)\]\.(\w+)$/.exec(key);

  if (condition) {
    const [, index, field] = condition;
    return {
      step: 'conditions',
      label: `Condition ${Number(index) + 1} · ${CONDITION_LABELS[field] ?? field}`
    };
  }

  // An unmapped key is shown as-is rather than hidden - a message with an odd label
  // still tells the user more than no message at all.
  return FIELD_LABELS[key] ?? { step: null, label: key };
}

function numberOrNull(value: number | null | undefined): number | null {
  return value === null || value === undefined || Number.isNaN(value) ? null : Number(value);
}

/** Dates come from <input type="date"> as yyyy-MM-dd; the API wants an offset stamp. */
function toIso(date: string): string {
  return new Date(`${date}T00:00:00`).toISOString();
}

/** Default to the far side of the price event lead time so the form starts valid. */
function defaultStart(): string {
  const date = new Date();
  date.setDate(date.getDate() + 7);
  return date.toISOString().slice(0, 10);
}
