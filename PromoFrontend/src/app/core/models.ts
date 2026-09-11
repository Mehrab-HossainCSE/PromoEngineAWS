/** Contracts mirroring the PromoEngine Web API. */

export type OfferLevel = 'Item' | 'Transaction';
export type OfferKind = 'SimpleDiscount' | 'BuyGet' | 'GiftWithPurchase';
export type ConditionKind = 'None' | 'BuyQuantity' | 'SpendAmount';
export type RewardKind = 'Discount' | 'GiftItem';
export type RewardItemMode = 'None' | 'DiscountedItems' | 'ExcludedItems';
export type SelectionAction = 'Include' | 'Exclude';

export type PromotionStatus =
  | 'Worksheet' | 'Submitted' | 'Approved' | 'Rejected' | 'Active' | 'Completed' | 'Cancelled';

export type ItemSelectionLevel =
  | 'AllDepartments' | 'Department' | 'Class' | 'Subclass' | 'Item'
  | 'ParentDiff' | 'ItemList' | 'UploadList' | 'SupplierSiteBrand';

export type LocationLevel = 'Zone' | 'LocationList' | 'Store';
export type DiscountType = 'PercentOff' | 'AmountOff' | 'FixedPrice' | 'FreeItem';
export type ApplyToPriceType = 'Regular' | 'Clearance' | 'RegularAndClearance';
export type PriceRestrictionOperator = 'None' | 'GreaterThan' | 'LessThan' | 'Between';
export type DistributionRule = 'NotApplicable' | 'BuyItems' | 'GetItems' | 'BothBuyAndGetItems';

// ---------------------------------------------------------------------------
// Auth and tenancy
// ---------------------------------------------------------------------------

export interface AuthResponse {
  token: string;
  expiresAt: string;
  isGuest: boolean;
  user?: UserProfile;
}

export interface UserProfile {
  userId: string;
  email: string;
  displayName: string;
  role: string;
  tenantId: string;
  tenantSlug: string;
  companyName: string;
  provisioningStatus: 'NotRequested' | 'Queued' | 'Provisioning' | 'Ready' | 'Failed';
  databaseName?: string;
  subscription?: Subscription;
}

export interface MeResponse {
  isGuest: boolean;
  user?: UserProfile;
}

export interface RegisterRequest {
  email: string;
  password: string;
  displayName?: string;
  companyName?: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface GuestConversionRequest {
  email: string;
  password: string;
  contactName: string;
  companyName: string;
  contactPhone?: string;
  country?: string;
  currencyCode?: string;
  planCode: string;
}

export interface Plan {
  id: string;
  code: string;
  name: string;
  tagline: string;
  description: string;
  monthlyPrice: number;
  currencyCode: string;
  maxPromotions: number;
  maxOffersPerPromotion: number;
  maxUsers: number;
  allowsTransactionLevelOffers: boolean;
  allowsGiftWithPurchase: boolean;
  allowsEmergencyOffers: boolean;
  allowsSpreadsheetUpload: boolean;
  isHighlighted: boolean;
  allowedTemplateCodes: string[];
  features: string[];
}

export interface Subscription {
  id: string;
  planCode: string;
  planName: string;
  status: 'Pending' | 'Active' | 'Expired' | 'Cancelled';
  startsAtUtc: string;
  trialEndsAtUtc?: string;
  isActive: boolean;
}

export interface ProvisioningStatus {
  tenantId: string;
  status: 'NotRequested' | 'Queued' | 'Provisioning' | 'Ready' | 'Failed';
  isReady: boolean;
  databaseName?: string;
  provisioner?: string;
  error?: string;
  provisionedAtUtc?: string;
  subscription?: Subscription;
}

// ---------------------------------------------------------------------------
// Offer templates
// ---------------------------------------------------------------------------

export interface OfferTemplate {
  code: string;
  name: string;
  level: OfferLevel;
  type: OfferKind;
  example: string;
  description: string;
  conditionKind: ConditionKind;
  hasConditions: boolean;
  allowsMultipleConditions: boolean;
  hasConditionItems: boolean;
  conditionPriceRestriction: boolean;
  conditionSingleItem: boolean;
  rewardKind: RewardKind;
  rewardItemMode: RewardItemMode;
  rewardPriceRestriction: boolean;
  allowsFixedPrice: boolean;
  allowsApplyTo: boolean;
  allowsApplyDiscountUpTo: boolean;
  allowsDistributionRule: boolean;
  availableOnCurrentPlan: boolean;
}

// ---------------------------------------------------------------------------
// Promotions
// ---------------------------------------------------------------------------

export interface PromotionSummary {
  id: string;
  promotionNumber: number;
  description: string;
  campaignCode?: string;
  status: PromotionStatus;
  offerCount: number;
  startDate?: string;
  endDate?: string;
  createdAtUtc: string;
}

export interface PromotionDetail extends PromotionSummary {
  campaignId?: string;
  offers: Offer[];
}

export interface Offer {
  id: string;
  promotionId: string;
  offerNumber: number;
  description: string;
  level: OfferLevel;
  type: OfferKind;
  templateCode: string;
  templateName: string;
  startDate: string;
  startTime?: string;
  endDate?: string;
  endTime?: string;
  comments?: string;
  couponCode?: string;
  couponCodeRequired: boolean;
  distributionRule: DistributionRule;
  exclusiveDiscount: boolean;
  customerDescription?: string;
  status: PromotionStatus;
  isEmergency: boolean;
  cancelReason?: string;
  approvedBy?: string;
  approvedAtUtc?: string;
  conditions: OfferCondition[];
  reward?: OfferReward;
  rewardItems: OfferItem[];
  locations: OfferLocation[];
  products: OfferProduct[];
}

export interface OfferCondition {
  id: string;
  sequence: number;
  buyQuantity?: number;
  unitOfMeasure?: string;
  spendAmount?: number;
  currencyCode?: string;
  priceRestriction: PriceRestrictionOperator;
  priceRestrictionFrom?: number;
  priceRestrictionTo?: number;
  priceRestrictionCurrency?: string;
  items: OfferItem[];
}

export interface OfferReward {
  discountType: DiscountType;
  discountValue?: number;
  currencyCode?: string;
  appliesToAllCurrencies: boolean;
  applyTo: ApplyToPriceType;
  applyDiscountUpTo?: number;
  getQuantity?: number;
  giftItemId?: string;
  giftItemDescription?: string;
  singleItemId?: string;
  singleItemDescription?: string;
}

export interface OfferItem {
  id: string;
  scope: 'Condition' | 'Reward';
  action: SelectionAction;
  level: ItemSelectionLevel;
  summary: string;
  department?: string;
  class?: string;
  subclass?: string;
  itemId?: string;
  itemDescription?: string;
  barcode?: string;
  styleCode?: string;
  vendorName?: string;
  sourceRowNumber?: number;
  parentItemId?: string;
  diffType?: string;
  diffValue?: string;
  itemListId?: string;
  sourceFileName?: string;
  supplierSite?: string;
  brand?: string;
  cancelReason?: string;
  cancelledAtUtc?: string;
}

export interface OfferLocation {
  id: string;
  action: SelectionAction;
  level: LocationLevel;
  summary: string;
  zoneGroup?: string;
  zone?: string;
  locationList?: string;
  store?: string;
  storeName?: string;
  cancelReason?: string;
  cancelledAtUtc?: string;
}

export interface Campaign {
  id: string;
  code: string;
  description: string;
}

// ---------------------------------------------------------------------------
// Write models
// ---------------------------------------------------------------------------

export interface SaveOfferItemRequest {
  action: SelectionAction;
  level: ItemSelectionLevel;
  department?: string | null;
  class?: string | null;
  subclass?: string | null;
  itemId?: string | null;
  itemDescription?: string | null;
  barcode?: string | null;
  styleCode?: string | null;
  vendorName?: string | null;
  sourceRowNumber?: number | null;
  parentItemId?: string | null;
  diffType?: string | null;
  diffValue?: string | null;
  itemListId?: string | null;
  sourceFileName?: string | null;
  supplierSite?: string | null;
  brand?: string | null;
}

export interface SaveConditionRequest {
  sequence: number;
  buyQuantity?: number | null;
  unitOfMeasure?: string | null;
  spendAmount?: number | null;
  currencyCode?: string | null;
  priceRestriction?: PriceRestrictionOperator | null;
  priceRestrictionFrom?: number | null;
  priceRestrictionTo?: number | null;
  priceRestrictionCurrency?: string | null;
  items: SaveOfferItemRequest[];
}

export interface SaveRewardRequest {
  discountType: DiscountType;
  discountValue?: number | null;
  currencyCode?: string | null;
  appliesToAllCurrencies: boolean;
  applyTo?: ApplyToPriceType | null;
  applyDiscountUpTo?: number | null;
  getQuantity?: number | null;
  giftItemId?: string | null;
  giftItemDescription?: string | null;
  singleItemId?: string | null;
  singleItemDescription?: string | null;
}

export interface SaveOfferRequest {
  description: string;
  templateCode: string;
  startDate: string;
  startTime?: string | null;
  endDate?: string | null;
  endTime?: string | null;
  comments?: string | null;
  couponCode?: string | null;
  couponCodeRequired: boolean;
  distributionRule?: DistributionRule | null;
  exclusiveDiscount: boolean;
  customerDescription?: string | null;
  isEmergency: boolean;
  conditions: SaveConditionRequest[];
  reward?: SaveRewardRequest;
  rewardItems: SaveOfferItemRequest[];
  /** The offer's own uploaded barcode sheet, saved with the offer. */
  products: OfferProductRow[];
}

export interface SaveOfferLocationRequest {
  action: SelectionAction;
  level: LocationLevel;
  zoneGroup?: string | null;
  zone?: string | null;
  locationList?: string | null;
  store?: string | null;
  storeName?: string | null;
}

export interface PromotionSearchRequest {
  promotionNumber?: number | null;
  description?: string | null;
  offerDescription?: string | null;
  offerType?: string | null;
  templateCode?: string | null;
  startDate?: string | null;
  endDate?: string | null;
  status?: string | null;
  itemId?: string | null;
  page?: number;
  pageSize?: number;
}

export interface Paged<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

export interface MassUpdateRequest {
  offerIds: string[];
  startDate?: string | null;
  startTime?: string | null;
  endDate?: string | null;
  endTime?: string | null;
  couponCode?: string | null;
  comments?: string | null;
  customerDescription?: string | null;
  clearEndDate: boolean;
  clearCouponCode: boolean;
  clearComments: boolean;
  clearCustomerDescription: boolean;
}

export interface CreateOfferFromExistingRequest {
  description: string;
  startDate: string;
  startTime?: string | null;
  endDate?: string | null;
  endTime?: string | null;
  couponCode?: string | null;
  customerDescription?: string | null;
  comments?: string | null;
  copyLocations: boolean;
}

// ---------------------------------------------------------------------------
// Offer product upload - the one Excel Upload, read by get-applicable-promotions
// ---------------------------------------------------------------------------

/**
 * One barcode row read out of a sheet uploaded against an offer. There are no
 * discount fields: the offer's own reward is the discount for every row it owns.
 */
export interface OfferProductRow {
  rowNumber: number;
  barcode: string;
  itemId?: string;
  styleCode?: string;
  itemDescription?: string;
  department?: string;
  class?: string;
  subclass?: string;
  brand?: string;
  vendorName?: string;
  supplierSite?: string;
  /** Include takes the discount, Exclude withholds it. Blank means Include. */
  action: SelectionAction;
  /** The GetApplicablePromotions column: is this row served to the POS? */
  getApplicablePromotions: boolean;
  promoTypeId?: string;
  siteCode?: string;
  customerTier?: string;
  sourceFileName?: string;
}

export interface OfferProductUploadResponse {
  fileName: string;
  success: boolean;
  rowCount: number;
  applicableRowCount: number;
  truncated: boolean;
  recognisedColumns: string[];
  /** False when the sheet had no GetApplicablePromotions column, so every row defaulted to served. */
  hasGetApplicablePromotionsColumn: boolean;
  /** False when the sheet had no Include/Exclude column, so every row defaulted to Include. */
  hasActionColumn: boolean;
  rows: OfferProductRow[];
  warnings: string[];
  error?: string;
}

/** A stored offer product row, as served back with the offer. */
export interface OfferProduct extends Omit<OfferProductRow, 'rowNumber'> {
  id: string;
  sourceRowNumber?: number;
  uploadedBy?: string;
  uploadedAtUtc: string;
}

/** Shape of an ASP.NET Core ProblemDetails / ValidationProblemDetails response. */
export interface ApiProblem {
  title?: string;
  detail?: string;
  status?: number;
  errors?: Record<string, string[]>;
}


