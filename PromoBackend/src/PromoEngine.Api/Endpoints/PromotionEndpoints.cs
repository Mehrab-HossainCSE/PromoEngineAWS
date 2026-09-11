using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PromoEngine.Api.Contracts;
using PromoEngine.Api.Middleware;
using PromoEngine.Api.Services;
using PromoEngine.Domain.Promotions;

namespace PromoEngine.Api.Endpoints;

public static class PromotionEndpoints
{
    public static void MapPromotionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/promotions")
            .WithTags("Promotions")
            .RequireAuthorization();

        // -------------------------------------------------------------------
        // Promotions
        // -------------------------------------------------------------------

        group.MapPost("/search", async (
            [FromBody] PromotionSearchRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            // The Pricing guide requires at least one criterion before searching.
            var hasCriteria = request.PromotionNumber is not null
                || !string.IsNullOrWhiteSpace(request.Description)
                || !string.IsNullOrWhiteSpace(request.OfferDescription)
                || request.StartDate is not null
                || !string.IsNullOrWhiteSpace(request.ItemId)
                || !string.IsNullOrWhiteSpace(request.Status)
                || !string.IsNullOrWhiteSpace(request.OfferType)
                || !string.IsNullOrWhiteSpace(request.TemplateCode);

            if (!hasCriteria)
            {
                return Results.Problem(
                    title: "Search criteria required",
                    detail: "Enter at least one of promotion, description, offer description, start date or item.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            return Results.Ok(await promotions.SearchAsync(request, ct));
        })
        .WithSummary("Search promotions");

        group.MapGet("", async (
            TenantAccessService access,
            PromotionService promotions,
            int? page,
            int? pageSize,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.SearchAsync(new PromotionSearchRequest
            {
                Page = page is null or <= 0 ? 1 : page.Value,
                PageSize = pageSize is null or <= 0 ? 25 : pageSize.Value
            }, ct);

            return Results.Ok(result);
        })
        .WithSummary("List all promotions");

        group.MapGet("/{id:guid}", async (
            Guid id,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var promotion = await promotions.GetAsync(id, ct);
            return promotion is null ? Results.NotFound() : Results.Ok(promotion);
        })
        .WithSummary("Get a promotion with its offers");

        group.MapPost("", async (
            [FromBody] SavePromotionRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (tenantAccess, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CreatePromotionAsync(
                request, currentUser.Email, tenantAccess!.Plan.MaxPromotions, ct);

            return result.Success
                ? Results.Created($"/api/promotions/{result.Value!.Id}", result.Value)
                : Results.Problem(title: "Could not create promotion", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithSummary("Create a promotion");

        group.MapPut("/{id:guid}", async (
            Guid id,
            [FromBody] SavePromotionRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.UpdatePromotionAsync(id, request, currentUser.Email, ct);

            return result.Success
                ? Results.Ok(result.Value)
                : Results.Problem(title: "Could not update promotion", detail: result.Error, statusCode: StatusCodes.Status404NotFound);
        })
        .WithSummary("Update a promotion");

        group.MapDelete("/{id:guid}", async (
            Guid id,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            return await promotions.DeletePromotionAsync(id, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithSummary("Delete a promotion and its offers");

        // -------------------------------------------------------------------
        // Offers
        // -------------------------------------------------------------------

        group.MapPost("/{promotionId:guid}/offers", async (
            Guid promotionId,
            [FromBody] SaveOfferRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (tenantAccess, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.AddOfferAsync(promotionId, request, tenantAccess!.Plan, currentUser.Email, ct);
            return ToResult(result, created: $"/api/promotions/{promotionId}");
        })
        .WithSummary("Add an offer to a promotion");

        var offers = app.MapGroup("/api/offers").WithTags("Offers").RequireAuthorization();

        offers.MapPut("/{offerId:guid}", async (
            Guid offerId,
            [FromBody] SaveOfferRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (tenantAccess, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.UpdateOfferAsync(offerId, request, tenantAccess!.Plan, currentUser.Email, ct);
            return ToResult(result);
        })
        .WithSummary("Edit an offer");

        offers.MapPost("/{offerId:guid}/copy", async (
            Guid offerId,
            [FromBody] CreateOfferFromExistingRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (tenantAccess, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CreateFromExistingAsync(offerId, request, tenantAccess!.Plan, ct);
            return ToResult(result);
        })
        .WithSummary("Create an offer from an existing one")
        .WithDescription("Used to build tiered offers such as buy 2 get 10% off, buy 3 get 20% off.");

        offers.MapPost("/mass-update", async (
            [FromBody] MassUpdateOffersRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var updated = await promotions.MassUpdateOffersAsync(request, ct);
            return Results.Ok(new { updated });
        })
        .WithSummary("Update dates, coupon, comments and customer description across offers");

        offers.MapPost("/submit", async (
            [FromBody] StatusChangeRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.ChangeStatusAsync(
                request.OfferIds, PromotionStatus.Submitted, currentUser.Email, ct);

            return ToResult(result);
        })
        .WithSummary("Submit offers for approval");

        offers.MapPost("/approve", async (
            [FromBody] StatusChangeRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            if (!currentUser.CanApprove)
            {
                return Results.Problem(
                    title: "Not permitted",
                    detail: "Approving offers requires the Administrator role.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var result = await promotions.ChangeStatusAsync(
                request.OfferIds, PromotionStatus.Approved, currentUser.Email, ct);

            return ToResult(result);
        })
        .WithSummary("Approve offers");

        offers.MapPost("/reopen", async (
            [FromBody] StatusChangeRequest request,
            TenantAccessService access,
            PromotionService promotions,
            ICurrentUser currentUser,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.ChangeStatusAsync(
                request.OfferIds, PromotionStatus.Worksheet, currentUser.Email, ct);

            return ToResult(result);
        })
        .WithSummary("Move offers back to worksheet so they can be edited");

        offers.MapPost("/cancel", async (
            [FromBody] CancelSelectionRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CancelOffersAsync(request.Ids, request.Reason, ct);
            return ToResult(result);
        })
        .WithSummary("Cancel active offers");

        offers.MapPost("/{offerId:guid}/items/cancel", async (
            Guid offerId,
            [FromBody] CancelSelectionRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CancelItemsAsync(offerId, request.Ids, request.Reason, ct);
            return ToResult(result);
        })
        .WithSummary("Cancel items from an active offer");

        offers.MapPost("/delete", async (
            [FromBody] StatusChangeRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            return await promotions.DeleteOffersAsync(request.OfferIds, ct)
                ? Results.NoContent()
                : Results.NotFound();
        })
        .WithSummary("Delete offers");

        // -------------------------------------------------------------------
        // Locations
        // -------------------------------------------------------------------

        offers.MapPost("/{offerId:guid}/locations", async (
            Guid offerId,
            [FromBody] List<SaveOfferLocationRequest> request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.AddLocationsAsync(offerId, request, ct);
            return ToResult(result);
        })
        .WithSummary("Add locations to an offer");

        offers.MapPost("/{offerId:guid}/locations/copy", async (
            Guid offerId,
            [FromBody] CopyLocationsRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CopyLocationsAsync(offerId, request.TargetOfferIds, ct);
            return ToResult(result);
        })
        .WithSummary("Copy this offer's locations onto other offers in the promotion");

        offers.MapPost("/{offerId:guid}/locations/cancel", async (
            Guid offerId,
            [FromBody] CancelSelectionRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CancelLocationsAsync(offerId, request.Ids, request.Reason, ct);
            return ToResult(result);
        })
        .WithSummary("Cancel locations from an active offer");

        offers.MapPost("/{offerId:guid}/locations/delete", async (
            Guid offerId,
            [FromBody] StatusChangeRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            return await promotions.DeleteLocationsAsync(offerId, request.OfferIds, ct)
                ? Results.NoContent()
                : Results.NotFound();
        })
        .WithSummary("Delete locations from an offer");

        // -------------------------------------------------------------------
        // Offer products - the barcode rows the external POS API answers from
        // -------------------------------------------------------------------

        // Parse only. The rows are handed back to the wizard and saved with the offer
        // itself, because until the user presses Save there is no offer to attach
        // them to - and an offer and the sheet it discounts have to arrive together.
        app.MapPost("/api/offer-products/parse", async (
            HttpRequest http,
            TenantAccessService access,
            OfferProductImporter importer,
            IOptions<ItemUploadOptions> uploadOptions,
            CancellationToken ct) =>
        {
            var (tenantAccess, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            if (!tenantAccess!.Plan.AllowsSpreadsheetUpload)
            {
                return Results.Problem(
                    title: "Not included in your plan",
                    detail: $"The {tenantAccess.Plan.Name} plan does not include spreadsheet upload. "
                            + "Upgrade to publish offer data to a point of sale.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var (file, fileProblem) = await ReadUploadAsync(http, uploadOptions.Value, ct);
            if (fileProblem is not null) return fileProblem;

            var parsed = importer.Parse(file!.Content, file.FileName, uploadOptions.Value.MaxRows);

            return parsed.Success
                ? Results.Ok(parsed)
                : Results.Problem(
                    title: "Could not read the product data", detail: parsed.Error,
                    statusCode: StatusCodes.Status400BadRequest);
        })
        .WithTags("Offers")
        .WithSummary("Parse an uploaded offer product sheet")
        .WithDescription(
            "Reads an .xlsx/.csv of barcodes, an optional Include/Exclude column and a GetApplicablePromotions "
            + "flag, and returns the rows to save with the offer so get-applicable-promotions can answer a POS "
            + "from them. The offer's own reward supplies the discount, so the sheet carries no price columns.")
        .DisableAntiforgery()
        .RequireAuthorization();

        // -------------------------------------------------------------------
        // Campaigns
        // -------------------------------------------------------------------

        var campaigns = app.MapGroup("/api/campaigns").WithTags("Campaigns").RequireAuthorization();

        campaigns.MapGet("", async (
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            return Results.Ok(await promotions.GetCampaignsAsync(ct));
        })
        .WithSummary("List campaigns");

        campaigns.MapPost("", async (
            [FromBody] SaveCampaignRequest request,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.CreateCampaignAsync(request, ct);
            return ToResult(result);
        })
        .WithSummary("Create a campaign");

        campaigns.MapDelete("/{id:guid}", async (
            Guid id,
            TenantAccessService access,
            PromotionService promotions,
            CancellationToken ct) =>
        {
            var (_, problem) = await access.ResolveAsync(ct);
            if (problem is not null) return problem;

            var result = await promotions.DeleteCampaignAsync(id, ct);
            return result.Success
                ? Results.NoContent()
                : Results.Problem(title: "Could not delete campaign", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithSummary("Delete a campaign");
    }

    /// <summary>Turns a service result into a 200/201, a 422 with field errors, or a 400.</summary>
    private static IResult ToResult<T>(ServiceResult<T> result, string? created = null)
    {
        if (result.Success)
        {
            return created is null ? Results.Ok(result.Value) : Results.Created(created, result.Value);
        }

        // The per-field errors are the useful part - they name the field and say what is
        // wrong with it. The title stays neutral because these failures are not only
        // template shape: plan entitlements and the price event lead time land here too.
        return result.Errors is not null
            ? Results.ValidationProblem(result.Errors, title: "Some offer details need fixing")
            : Results.Problem(title: "Request failed", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
    }

    /// <summary>A spreadsheet lifted off a multipart request, buffered ready to parse.</summary>
    private sealed record UploadedFile(string FileName, MemoryStream Content);

    /// <summary>
    /// The checks every spreadsheet upload has to make - form encoding, a file being
    /// present, the size cap and the extension - in one place, so the item list and
    /// the offer product sheet cannot drift apart on what they accept.
    /// </summary>
    private static async Task<(UploadedFile? File, IResult? Problem)> ReadUploadAsync(
        HttpRequest http, ItemUploadOptions options, CancellationToken ct)
    {
        if (!http.HasFormContentType)
        {
            return (null, Results.Problem(
                title: "No file", detail: "Send the spreadsheet as multipart/form-data.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        var form = await http.ReadFormAsync(ct);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();

        if (file is null || file.Length == 0)
        {
            return (null, Results.Problem(
                title: "No file", detail: "Choose a spreadsheet to upload.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        if (file.Length > options.MaxFileBytes)
        {
            return (null, Results.Problem(
                title: "File too large",
                detail: $"The file is {file.Length / 1024d / 1024d:N1} MB. "
                        + $"The limit is {options.MaxFileBytes / 1024d / 1024d:N0} MB.",
                statusCode: StatusCodes.Status413PayloadTooLarge));
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".xlsx" or ".xlsm" or ".csv"))
        {
            return (null, Results.Problem(
                title: "Unsupported file type",
                detail: $"'{extension}' is not supported. Save the sheet as .xlsx, .xlsm, or .csv and try again.",
                statusCode: StatusCodes.Status415UnsupportedMediaType));
        }

        // ClosedXML needs a seekable stream, and buffering also keeps the parse off
        // the request socket.
        var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        buffer.Position = 0;

        return (new UploadedFile(file.FileName, buffer), null);
    }
}

