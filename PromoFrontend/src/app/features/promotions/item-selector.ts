import { Component, computed, inject, input, model, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiError, ApiService } from '../../core/api.service';
import {
  ItemSelectionLevel, OfferProductRow, OfferProductUploadResponse,
  SaveOfferItemRequest, SelectionAction
} from '../../core/models';

interface LevelOption {
  value: ItemSelectionLevel;
  label: string;
}

/**
 * The include/exclude items grid used on both the conditions and rewards pages.
 * Which fields the dialog shows follows the chosen merchandise hierarchy level,
 * the same way the Include/Exclude Items pop-up behaves in Pricing.
 */
@Component({
  selector: 'app-item-selector',
  imports: [FormsModule],
  templateUrl: './item-selector.html',
  styleUrl: './item-selector.scss'
})
export class ItemSelectorComponent {
  private readonly api = inject(ApiService);

  readonly items = model.required<SaveOfferItemRequest[]>();
  readonly title = input('Qualifying items');
  readonly hint = input<string>('');
  readonly defaultAction = input<SelectionAction>('Include');
  readonly emptyText = input('No items yet. Add an include rule to define which merchandise this applies to.');
  readonly disabled = input(false);

  /**
   * The offer's own uploaded barcode sheet - what an external point of sale is
   * answered from. Shown on the rewards stop only, so one offer has one sheet
   * however many item selectors the template puts on the page.
   */
  readonly products = model<OfferProductRow[]>([]);
  readonly showProducts = input(false);

  /**
   * False hides the hierarchy rules and leaves only the uploaded sheet. Templates
   * whose reward has no item rules of its own still need somewhere to upload their
   * products, and that somewhere is this same card rather than a second one.
   */
  readonly showItems = input(true);

  protected readonly dialogOpen = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly draft = signal<SaveOfferItemRequest>(emptyDraft());

  // ---- offer product sheet (external POS data) ---------------------------
  protected readonly productDialogOpen = signal(false);
  protected readonly productUploading = signal(false);
  protected readonly productUpload = signal<OfferProductUploadResponse | null>(null);
  protected readonly productError = signal<string | null>(null);

  /**
   * Whether a new sheet replaces the offer's rows or is added to them. Replacing is
   * the default: a corrected re-upload must not leave barcodes the retailer has
   * since dropped from the sheet still being discounted.
   */
  protected readonly productMode = signal<'replace' | 'append'>('replace');

  protected readonly levels: LevelOption[] = [
    { value: 'AllDepartments', label: 'All departments' },
    { value: 'Department', label: 'Department' },
    { value: 'Class', label: 'Class' },
    { value: 'Subclass', label: 'Subclass' },
    { value: 'Item', label: 'Item' },
    { value: 'ParentDiff', label: 'Parent / Diff' },
    { value: 'ItemList', label: 'Item list' },
    { value: 'SupplierSiteBrand', label: 'Supplier site / Brand' }
  ];

  protected readonly level = computed(() => this.draft().level);

  // Field visibility, mirroring which inputs the Pricing dialog enables per level.
  protected readonly showDepartment = computed(() =>
    ['Department', 'Class', 'Subclass'].includes(this.level()));
  protected readonly showClass = computed(() => ['Class', 'Subclass'].includes(this.level()));
  protected readonly showSubclass = computed(() => this.level() === 'Subclass');
  protected readonly showItem = computed(() => this.level() === 'Item');
  protected readonly showParentDiff = computed(() => this.level() === 'ParentDiff');
  protected readonly showItemList = computed(() => this.level() === 'ItemList');
  protected readonly showSupplierBrand = computed(() =>
    ['Department', 'Class', 'Subclass', 'SupplierSiteBrand'].includes(this.level()));

  protected readonly includeCount = computed(() => this.items().filter(i => i.action === 'Include').length);
  protected readonly excludeCount = computed(() => this.items().filter(i => i.action === 'Exclude').length);

  protected open() {
    if (this.disabled()) return;
    this.draft.set(emptyDraft(this.defaultAction()));
    this.error.set(null);
    this.dialogOpen.set(true);
  }

  protected close() {
    this.dialogOpen.set(false);
  }

  protected patch(field: keyof SaveOfferItemRequest, value: string | number) {
    this.draft.update(current => ({ ...current, [field]: value }));
  }

  // -----------------------------------------------------------------------
  // Offer products - the one Excel Upload, read by get-applicable-promotions
  // -----------------------------------------------------------------------

  protected readonly productIncludeCount = computed(() =>
    this.products().filter(p => p.action !== 'Exclude').length);

  protected readonly productExcludeCount = computed(() =>
    this.products().filter(p => p.action === 'Exclude').length);

  /** The grid is a preview, not a data browser - a big sheet is summarised instead. */
  protected readonly productPreview = computed(() => this.products().slice(0, 25));

  /** Row counts per uploaded file, so one file's rows can be dropped on their own. */
  protected readonly productFiles = computed(() => {
    const counts = new Map<string, number>();

    for (const product of this.products()) {
      const name = product.sourceFileName;
      if (name) counts.set(name, (counts.get(name) ?? 0) + 1);
    }

    return [...counts.entries()].map(([name, count]) => ({ name, count }));
  });

  protected openProductUpload() {
    if (this.disabled()) return;

    this.productUpload.set(null);
    this.productError.set(null);
    this.productUploading.set(false);
    this.productMode.set(this.products().length ? 'append' : 'replace');
    this.productDialogOpen.set(true);
  }

  protected closeProductUpload() {
    this.productDialogOpen.set(false);
    this.productUpload.set(null);
    this.productError.set(null);
  }

  /** Sends the chosen workbook to be parsed. Nothing is stored until the offer is saved. */
  protected onProductFileChosen(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;

    // Let the same file be picked again after a failed parse.
    input.value = '';

    this.productUpload.set(null);
    this.productError.set(null);
    this.productUploading.set(true);

    this.api.parseOfferProducts(file).subscribe({
      next: result => {
        this.productUploading.set(false);
        this.productUpload.set(result);
      },
      error: (err: ApiError) => {
        this.productUploading.set(false);
        this.productError.set(err.message);
      }
    });
  }

  /** Commits the parsed rows to the offer draft. */
  protected addParsedProducts() {
    const parsed = this.productUpload();
    if (!parsed || parsed.rows.length === 0) return;

    this.products.update(current => {
      // Re-uploading the same file always replaces that file's own rows, so
      // uploading twice by mistake cannot double them.
      const kept = this.productMode() === 'replace'
        ? []
        : current.filter(p => p.sourceFileName !== parsed.fileName);

      return [...kept, ...parsed.rows];
    });

    this.closeProductUpload();
  }

  /** Flips one uploaded product between taking the discount and being held back. */
  protected setProductAction(index: number, action: SelectionAction) {
    this.products.update(list => list.map((p, i) => (i === index ? { ...p, action } : p)));
  }

  protected setAllProductActions(action: SelectionAction) {
    this.products.update(list => list.map(p => ({ ...p, action })));
  }

  protected removeProductFile(fileName: string) {
    this.products.update(list => list.filter(p => p.sourceFileName !== fileName));
  }

  protected clearProducts() {
    this.products.set([]);
  }

  /** Writes the expected sheet shape out as a CSV the user can fill in and upload. */
  protected downloadProductTemplate() {
    const header = [
      'Barcode', 'ItemId', 'StyleCode', 'ItemDescription', 'Department', 'Class', 'Subclass',
      'Brand', 'VendorName', 'SupplierSite', 'Action', 'GetApplicablePromotions',
      'PromoTypeId', 'SiteCode', 'CustomerTier'
    ].join(',');

    const rows = [
      '6156BA16419876,10001,ST-1001,Men Cotton Polo,10,20,30,Northwind,Acme Supply,SITE-1,Include,Yes,5,G184,Silver',
      '9211AA69409876,10002,ST-1002,Women Summer Dress,10,20,31,Northwind,Acme Supply,SITE-1,Include,Yes,5,G184,Silver',
      '5551234567890,10003,ST-1003,Casual Canvas Shoes,15,25,35,Aero,Global Brands,SITE-2,Exclude,Yes,5,,'
    ];

    const blob = new Blob([[header, ...rows].join('\r\n')], { type: 'text/csv;charset=utf-8;' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');

    link.href = url;
    link.download = 'offer_products_template.csv';
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
    URL.revokeObjectURL(url);
  }

  protected setAction(action: SelectionAction) {
    this.draft.update(current => ({ ...current, action }));
  }

  protected setLevel(level: ItemSelectionLevel) {
    // Reset the level-specific fields so a stale value from another level is not saved.
    this.draft.update(current => ({ ...emptyDraft(this.defaultAction()), action: current.action, level }));
  }

  /** Adds the drafted rule. `keepOpen` backs the "OK and Add Another" behaviour. */
  protected add(keepOpen: boolean) {
    const draft = this.draft();
    const problem = validate(draft);

    if (problem) {
      this.error.set(problem);
      return;
    }

    this.items.update(list => [...list, { ...draft }]);
    this.error.set(null);

    if (keepOpen) {
      this.draft.set({ ...emptyDraft(this.defaultAction()), action: draft.action, level: draft.level });
    } else {
      this.dialogOpen.set(false);
    }
  }

  protected remove(index: number) {
    this.items.update(list => list.filter((_, i) => i !== index));
  }

  protected summarise(item: SaveOfferItemRequest): string {
    switch (item.level) {
      case 'AllDepartments': return 'All departments';
      case 'Department': return `Dept ${item.department}`;
      case 'Class': return `Dept ${item.department} / Class ${item.class}`;
      case 'Subclass': return `Dept ${item.department} / Class ${item.class} / Subclass ${item.subclass}`;
      case 'Item': {
        const head = item.itemId
          ? `Item ${item.itemId}`
          : item.barcode
            ? `Barcode ${item.barcode}`
            : item.styleCode
              ? `Style ${item.styleCode}`
              : 'Item';

        const facts = [
          item.barcode && item.itemId ? `Barcode: ${item.barcode}` : null,
          item.styleCode && (item.itemId || item.barcode) ? `Style: ${item.styleCode}` : null,
          item.vendorName
        ].filter(Boolean);

        const description = item.itemDescription ? ` — ${item.itemDescription}` : '';
        return facts.length ? `${head} (${facts.join(' · ')})${description}` : `${head}${description}`;
      }
      case 'ParentDiff': return `Parent ${item.parentItemId} / ${item.diffType} ${item.diffValue}`;
      case 'ItemList': return `Item list ${item.itemListId}`;
      case 'UploadList': return `Uploaded list ${item.sourceFileName}`;
      case 'SupplierSiteBrand': return `Supplier ${item.supplierSite ?? '—'} / Brand ${item.brand ?? '—'}`;
      default: return item.level;
    }
  }

  protected levelLabel(level: ItemSelectionLevel): string {
    return this.levels.find(l => l.value === level)?.label ?? level;
  }
}

function emptyDraft(defaultAction: SelectionAction = 'Include'): SaveOfferItemRequest {
  return { action: defaultAction, level: 'AllDepartments' };
}

function validate(draft: SaveOfferItemRequest): string | null {
  switch (draft.level) {
    case 'Department':
      return draft.department ? null : 'Enter a department.';
    case 'Class':
      return draft.department && draft.class ? null : 'Enter a department and class.';
    case 'Subclass':
      return draft.department && draft.class && draft.subclass ? null : 'Enter a department, class and subclass.';
    case 'Item':
      return draft.itemId ? null : 'Enter an item number.';
    case 'ParentDiff':
      return draft.parentItemId && draft.diffType && draft.diffValue
        ? null
        : 'Enter a parent item, diff type and diff.';
    case 'ItemList':
      return draft.itemListId ? null : 'Enter an item list.';
    case 'SupplierSiteBrand':
      return draft.supplierSite || draft.brand ? null : 'Enter a supplier site or a brand.';
    default:
      return null;
  }
}
