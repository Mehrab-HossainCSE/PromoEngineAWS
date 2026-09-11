using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using PromoEngine.Api.Contracts;

namespace PromoEngine.Api.Services;

/// <summary>Limits on the spreadsheet a user may upload against an offer.</summary>
public sealed class ItemUploadOptions
{
    public const string SectionName = "ItemUpload";

    /// <summary>Largest file accepted, in bytes. Default 5 MB.</summary>
    public int MaxFileBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Most rows read from one sheet; the rest are reported as truncated.</summary>
    public int MaxRows { get; set; } = 5000;
}

/// <summary>
/// Reads the product sheet uploaded against an offer - the flattened, barcode keyed
/// list of the products that offer covers, which an external point of sale is
/// answered from.
///
/// This is the one spreadsheet upload in the application. Headings are matched
/// loosely, so "Bar Code", "BARCODE" and "upc" all land on the same column, and the
/// <c>GetApplicablePromotions</c> column decides what the external API may see.
///
/// It reads no discount columns. The offer's reward is the discount for every row on
/// its sheet, so there is nothing about price for the sheet to disagree with.
/// </summary>
public sealed class OfferProductImporter(ILogger<OfferProductImporter> logger)
{
    private static readonly string[] BarcodeHeaders =
        ["barcode", "barcodeno", "barcodenumber", "upc", "ean", "gtin", "eancode", "upccode"];

    private static readonly string[] ItemIdHeaders =
        ["item", "itemid", "itemno", "itemnumber", "itemcode", "sku", "skucode", "productcode", "productid"];

    private static readonly string[] StyleHeaders = ["style", "stylecode", "styleno", "stylenumber", "styleid"];

    private static readonly string[] DescriptionHeaders =
        ["description", "itemdescription", "productdescription", "name", "itemname", "productname"];

    private static readonly string[] DepartmentHeaders = ["department", "dept", "deptno", "departmentcode"];
    private static readonly string[] ClassHeaders = ["class", "classno", "classcode"];
    private static readonly string[] SubclassHeaders = ["subclass", "subclassno", "subclasscode"];
    private static readonly string[] BrandHeaders = ["brand", "brandname"];
    private static readonly string[] VendorHeaders = ["vendor", "vendorname", "supplier", "suppliername"];
    private static readonly string[] SupplierSiteHeaders = ["suppliersite", "site", "supplierno", "suppliersitecode"];

    // The column the whole feature turns on.
    private static readonly string[] GetApplicableHeaders =
    [
        "getapplicablepromotions", "getapplicablepromotion", "applicablepromotions",
        "getapplicablepromo", "getapplicable", "applicable"
    ];

    /// <summary>Optional include/exclude column. Absent, every row is included.</summary>
    private static readonly string[] ActionHeaders =
        ["action", "includeexclude", "include", "exclude", "inclusion", "includeorexclude"];

    private static readonly string[] PromoTypeHeaders =
        ["promotypeid", "promotype", "promotiontype", "promotiontypeid", "promotypecode"];

    private static readonly string[] SiteCodeHeaders = ["sitecode", "storecode", "store", "outlet", "outletcode"];
    private static readonly string[] CustomerTierHeaders = ["customertier", "tier", "customergrade", "membershiptier"];

    /// <summary>
    /// Parses the stream into product rows. Bad rows never fail the whole file: they
    /// come back as warnings so the user can fix the sheet instead of guessing.
    /// </summary>
    public OfferProductUploadResponse Parse(Stream stream, string fileName, int maxRows)
    {
        return Path.GetExtension(fileName).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? ParseCsv(stream, fileName, maxRows)
            : ParseWorkbook(stream, fileName, maxRows);
    }

    // -----------------------------------------------------------------------
    // .xlsx / .xlsm
    // -----------------------------------------------------------------------

    private OfferProductUploadResponse ParseWorkbook(Stream stream, string fileName, int maxRows)
    {
        try
        {
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null) return Failed(fileName, "The workbook has no worksheets.");

            var used = sheet.RangeUsed();
            if (used is null) return Failed(fileName, "The first worksheet is empty.");

            var map = MapHeaders(used.FirstRow());
            if (!map.ContainsKey(Field.Barcode)) return NoBarcodeColumn(fileName);

            var body = used.Rows().Skip(1).ToList();
            var warnings = new List<string>();
            var truncated = false;

            if (body.Count > maxRows)
            {
                truncated = true;
                warnings.Add($"The file has {body.Count:N0} rows; only the first {maxRows:N0} were read.");
                body = body.Take(maxRows).ToList();
            }

            var rows = new List<OfferProductRow>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in body)
            {
                var number = row.RowNumber();
                string? Cell(Field field) => ReadCell(row, map, field);

                var barcode = Cell(Field.Barcode);
                if (string.IsNullOrWhiteSpace(barcode))
                {
                    if (!IsEntirelyEmpty(row)) warnings.Add($"Row {number}: no barcode - skipped.");
                    continue;
                }

                if (!seen.Add(barcode))
                {
                    warnings.Add($"Row {number}: duplicate barcode {barcode} - skipped.");
                    continue;
                }

                rows.Add(BuildRow(number, barcode, fileName, Cell, warnings));
            }

            logger.LogInformation("Parsed {Count} offer product rows from {File}", rows.Count, fileName);
            return Completed(fileName, rows, warnings, truncated, map);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read uploaded offer product sheet {File}", fileName);
            return Failed(fileName, $"The file could not be read as a spreadsheet: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // .csv
    // -----------------------------------------------------------------------

    private OfferProductUploadResponse ParseCsv(Stream stream, string fileName, int maxRows)
    {
        try
        {
            using var reader = new StreamReader(stream, leaveOpen: true);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine)) return Failed(fileName, "The CSV file is empty.");

            var headers = ParseCsvLine(headerLine);
            var map = new Dictionary<Field, ColumnBinding>();

            for (var i = 0; i < headers.Count; i++)
            {
                var header = headers[i].Trim();
                if (header.Length == 0) continue;
                BindAll(map, Normalise(header), i, header);
            }

            if (!map.ContainsKey(Field.Barcode)) return NoBarcodeColumn(fileName);

            var rows = new List<OfferProductRow>();
            var warnings = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var truncated = false;
            var lineNumber = 1;
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (rows.Count >= maxRows)
                {
                    truncated = true;
                    warnings.Add($"The file has more rows; only the first {maxRows:N0} were read.");
                    break;
                }

                var tokens = ParseCsvLine(line);

                string? Cell(Field field)
                {
                    if (!map.TryGetValue(field, out var binding) || binding.Column >= tokens.Count) return null;
                    var value = tokens[binding.Column].Trim();
                    return value.Length == 0 ? null : value;
                }

                var barcode = Cell(Field.Barcode);
                if (string.IsNullOrWhiteSpace(barcode))
                {
                    warnings.Add($"Row {lineNumber}: no barcode - skipped.");
                    continue;
                }

                if (!seen.Add(barcode))
                {
                    warnings.Add($"Row {lineNumber}: duplicate barcode {barcode} - skipped.");
                    continue;
                }

                rows.Add(BuildRow(lineNumber, barcode, fileName, Cell, warnings));
            }

            logger.LogInformation("Parsed {Count} offer product rows from CSV {File}", rows.Count, fileName);
            return Completed(fileName, rows, warnings, truncated, map);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read uploaded offer product CSV {File}", fileName);
            return Failed(fileName, $"The CSV file could not be read: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------------
    // Row assembly
    // -----------------------------------------------------------------------

    private static OfferProductRow BuildRow(
        int rowNumber, string barcode, string fileName, Func<Field, string?> cell, List<string> warnings)
    {
        // A sheet with no flag column at all is taken at face value: every row on it
        // was uploaded to be served. Only an explicit "no" opts a row out.
        var flag = ParseBool(cell(Field.GetApplicablePromotions)) ?? true;

        var actionCell = cell(Field.Action);
        var action = ParseAction(actionCell);

        if (action is null && !string.IsNullOrWhiteSpace(actionCell))
        {
            warnings.Add($"Row {rowNumber}: '{actionCell}' is not Include or Exclude - treated as Include.");
        }

        return new OfferProductRow
        {
            RowNumber = rowNumber,
            Barcode = barcode,
            ItemId = cell(Field.ItemId),
            StyleCode = cell(Field.StyleCode),
            ItemDescription = cell(Field.Description),
            Department = cell(Field.Department),
            Class = cell(Field.Class),
            Subclass = cell(Field.Subclass),
            Brand = cell(Field.Brand),
            VendorName = cell(Field.Vendor),
            SupplierSite = cell(Field.SupplierSite),
            // Not saying include or exclude means include: an uploaded product is a
            // discounted product until the user carves it back out.
            Action = action ?? "Include",
            GetApplicablePromotions = flag,
            PromoTypeId = cell(Field.PromoTypeId),
            SiteCode = cell(Field.SiteCode),
            CustomerTier = cell(Field.CustomerTier),
            SourceFileName = fileName
        };
    }

    private static OfferProductUploadResponse Completed(
        string fileName,
        List<OfferProductRow> rows,
        List<string> warnings,
        bool truncated,
        Dictionary<Field, ColumnBinding> map) => new()
        {
            FileName = fileName,
            Success = rows.Count > 0,
            RowCount = rows.Count,
            ApplicableRowCount = rows.Count(r => r.GetApplicablePromotions && r.Action != "Exclude"),
            Truncated = truncated,
            RecognisedColumns = map.Values.Select(v => v.Header).Distinct().ToList(),
            HasGetApplicablePromotionsColumn = map.ContainsKey(Field.GetApplicablePromotions),
            HasActionColumn = map.ContainsKey(Field.Action),
            Rows = rows,
            Warnings = warnings,
            Error = rows.Count == 0 ? "No usable rows were found in the file." : null
        };

    private static OfferProductUploadResponse NoBarcodeColumn(string fileName) => Failed(fileName,
        "No Barcode column. The first row must carry a heading of Barcode (or UPC / EAN / GTIN), because a "
        + "barcode is what the point of sale sends to get-applicable-promotions.");

    private static OfferProductUploadResponse Failed(string fileName, string error) => new()
    {
        FileName = fileName,
        Success = false,
        RowCount = 0,
        ApplicableRowCount = 0,
        Truncated = false,
        RecognisedColumns = [],
        HasGetApplicablePromotionsColumn = false,
        HasActionColumn = false,
        Rows = [],
        Warnings = [],
        Error = error
    };

    // -----------------------------------------------------------------------
    // Header binding
    // -----------------------------------------------------------------------

    private enum Field
    {
        Barcode, ItemId, StyleCode, Description, Department, Class, Subclass, Brand, Vendor, SupplierSite,
        Action, GetApplicablePromotions, PromoTypeId, SiteCode, CustomerTier
    }

    private sealed record ColumnBinding(int Column, string Header);

    /// <summary>First matching column wins, so a stray later column cannot displace it.</summary>
    private static void BindAll(Dictionary<Field, ColumnBinding> map, string normalised, int column, string header)
    {
        void Bind(Field field, string[] candidates)
        {
            if (!map.ContainsKey(field) && candidates.Contains(normalised))
            {
                map[field] = new ColumnBinding(column, header);
            }
        }

        // The specific fields are bound before the looser Description alias, so a
        // heading like "Product Description" cannot be swallowed by a general one.
        Bind(Field.Barcode, BarcodeHeaders);
        Bind(Field.GetApplicablePromotions, GetApplicableHeaders);
        Bind(Field.Action, ActionHeaders);
        Bind(Field.PromoTypeId, PromoTypeHeaders);
        Bind(Field.SiteCode, SiteCodeHeaders);
        Bind(Field.CustomerTier, CustomerTierHeaders);
        Bind(Field.ItemId, ItemIdHeaders);
        Bind(Field.StyleCode, StyleHeaders);
        Bind(Field.Description, DescriptionHeaders);
        Bind(Field.Department, DepartmentHeaders);
        Bind(Field.Class, ClassHeaders);
        Bind(Field.Subclass, SubclassHeaders);
        Bind(Field.Brand, BrandHeaders);
        Bind(Field.Vendor, VendorHeaders);
        Bind(Field.SupplierSite, SupplierSiteHeaders);
    }

    private static Dictionary<Field, ColumnBinding> MapHeaders(IXLRangeRow headerRow)
    {
        var map = new Dictionary<Field, ColumnBinding>();

        foreach (var cell in headerRow.Cells())
        {
            var header = cell.GetString().Trim();
            if (header.Length == 0) continue;
            BindAll(map, Normalise(header), cell.Address.ColumnNumber, header);
        }

        return map;
    }

    /// <summary>"Bar Code", "bar_code" and "BARCODE" all normalise to "barcode".</summary>
    private static string Normalise(string header) =>
        new(header.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? ReadCell(IXLRangeRow row, Dictionary<Field, ColumnBinding> map, Field field)
    {
        if (!map.TryGetValue(field, out var binding)) return null;

        var cell = row.Cell(binding.Column - row.RangeAddress.FirstAddress.ColumnNumber + 1);
        if (cell.IsEmpty()) return null;

        // Barcodes are long numbers and Excel hands them back as doubles, which would
        // otherwise render as 1.23456789012E+12.
        var value = cell.DataType switch
        {
            XLDataType.Number => FormatNumber(cell.GetDouble()),
            XLDataType.DateTime => cell.GetDateTime().ToString("O", CultureInfo.InvariantCulture),
            XLDataType.Boolean => cell.GetBoolean() ? "true" : "false",
            _ => cell.GetString()
        };

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// 2^53 is the largest integer a double holds exactly, and Excel stores every number
    /// as a double. Below it a whole number is written out digit for digit, which matters
    /// most for the barcode: it is the key the till is matched on, so a barcode quietly
    /// losing its last digit would show up only as a missing discount. Above it the value
    /// in the file is already approximate and no reading of it can recover the digits.
    /// </summary>
    private const double ExactIntegerLimit = 9007199254740992d;

    private static string FormatNumber(double value) =>
        value == Math.Floor(value) && Math.Abs(value) < ExactIntegerLimit
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.############", CultureInfo.InvariantCulture);

    private static bool IsEntirelyEmpty(IXLRangeRow row) =>
        row.Cells().All(cell => cell.IsEmpty() || string.IsNullOrWhiteSpace(cell.GetString()));

    // -----------------------------------------------------------------------
    // Value parsing
    // -----------------------------------------------------------------------

    /// <summary>Accepts the many ways a spreadsheet says yes: Y, Yes, TRUE, 1, X.</summary>
    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "y" or "yes" or "true" or "t" or "x" or "on" or "enabled" or "active" => true,
            "0" or "n" or "no" or "false" or "f" or "off" or "disabled" or "inactive" => false,
            _ => null
        };
    }

    /// <summary>
    /// "Include"/"Exclude" as the sheet is likely to spell them, or null when the cell
    /// says nothing recognisable - which the caller reads as Include.
    /// </summary>
    private static string? ParseAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return Normalise(value) switch
        {
            "include" or "included" or "i" or "in" or "y" or "yes" or "1" or "true" => "Include",
            "exclude" or "excluded" or "e" or "ex" or "out" or "n" or "no" or "0" or "false" => "Exclude",
            _ => null
        };
    }

    // -----------------------------------------------------------------------
    // CSV
    // -----------------------------------------------------------------------

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        result.Add(current.ToString());
        return result;
    }
}
