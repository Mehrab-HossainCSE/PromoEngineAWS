using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PromoEngine.Api.Contracts;
using PromoEngine.Api.Services;

namespace PromoEngine.Api.Endpoints;

public static class ExternalPromotionEndpoints
{
    public static void MapExternalPromotionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/External/get-applicable-promotions", async (
            [FromBody] GetApplicablePromotionsRequest request,
            [FromHeader(Name = "HEADER-API-KEY")] string? apiKey,
            IOptions<ExternalPromotionApiOptions> options,
            ApplicablePromotionService promotions,
            CancellationToken ct) =>
        {
            if (!KeysMatch(apiKey, options.Value.ApiKey))
            {
                return Results.Json(new { message = "Authentication failed" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (string.IsNullOrWhiteSpace(request.TenantId))
            {
                return Results.Json(
                    new { message = "TenantId is required" }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (string.IsNullOrWhiteSpace(request.SiteCode)
                || string.IsNullOrWhiteSpace(request.PromotypeID)
                || request.Items.Count == 0)
            {
                return Results.Json(new { message = "Invalid SiteCode, PromotypeID or missing Items" }, statusCode: StatusCodes.Status400BadRequest);
            }

            var (results, error) = await promotions.GetAsync(request, ct);

            // A tenant that cannot be resolved is the caller's mistake, not an empty
            // basket, so it is reported rather than answered with [] - otherwise a
            // typo in TenantId looks exactly like "no promotions apply".
            return error is null
                ? Results.Ok(results)
                : Results.Json(new { message = error }, statusCode: StatusCodes.Status400BadRequest);
        })
        .WithTags("External")
        .WithSummary("Get applicable promotions for a POS basket")
        .WithDescription(
            "Requires the HEADER-API-KEY header and a TenantId naming the customer whose database to read - "
            + "the tenant id, tenant slug or account email all work. Returns the promotion slabs applicable "
            + "to the basket, or an empty array when nothing applies.")
        .AllowAnonymous();
    }

    private static bool KeysMatch(string? supplied, string configured)
    {
        if (string.IsNullOrWhiteSpace(supplied) || string.IsNullOrWhiteSpace(configured)) return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(supplied),
            Encoding.UTF8.GetBytes(configured));
    }
}
