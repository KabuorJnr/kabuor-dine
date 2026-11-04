// Kabuor Verified's API. The owner and the accountant see the proof; only the owner can share it.
// Every share is explicit, time-limited, purpose-limited, and recorded in the audit trail as consent.
// Lenders receive totals and counts only, never individual customer names, phone numbers, or invoice lines.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Verified;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public sealed record CreateVerifiedShareInput(
    string LenderName,
    string? LenderContact,
    int Months,
    int Days,
    string? Password,
    string? Code
);

public static class VerifiedEndpoints
{
    public static void Map(WebApplication app)
    {
        var verified = app.MapGroup("/api/verified").RequireAuthorization(Policies.VerifiedView);

        // 1. Owner & Accountant: view summary
        verified.MapGet("/summary", (VerifiedService s, int? months) => s.Summary(months ?? 6));

        // 2. Owner & Accountant: view shares list
        verified.MapGet("/shares", async (ErpDb db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var shares = await db.VerifiedShares.AsNoTracking()
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync();

            var shareIds = shares.Select(s => s.Id).ToList();
            var views = await db.VerifiedShareViews.AsNoTracking()
                .Where(v => shareIds.Contains(v.ShareId))
                .GroupBy(v => v.ShareId)
                .Select(g => new { ShareId = g.Key, Count = g.Count(), LastViewedAt = g.Max(v => (DateTimeOffset?)v.At) })
                .ToDictionaryAsync(x => x.ShareId);

            return shares.Select(s =>
            {
                var viewInfo = views.GetValueOrDefault(s.Id);
                var status = s.RevokedAt.HasValue ? "revoked"
                    : s.ExpiresAt <= now ? "expired"
                    : "active";

                return new
                {
                    s.Id,
                    s.LenderName,
                    s.LenderContact,
                    s.Months,
                    s.CreatedAt,
                    s.ExpiresAt,
                    s.RevokedAt,
                    s.RevokedBy,
                    s.CreatedBy,
                    Status = status,
                    ViewsCount = viewInfo?.Count ?? 0,
                    LastViewedAt = viewInfo?.LastViewedAt
                };
            });
        });

        // 3. Owner only: create a new lender share with re-authentication
        verified.MapPost("/shares", async (ErpDb db, RequestContext ctx, IConfiguration config, HttpContext http, CreateVerifiedShareInput input) =>
        {
            if (string.IsNullOrWhiteSpace(input.LenderName)) throw new AppException("Lender name is required.");
            if (input.Months is not (3 or 6 or 12)) throw new AppException("Choose 3, 6 or 12 months.");
            if (input.Days is not (7 or 30 or 90)) throw new AppException("Choose 7, 30 or 90 days.");

            // Owner re-authentication
            var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == ctx.UserId);
            var box = http.RequestServices.GetRequiredService<SecretBox>();
            if (user.TwoFactorEnabled && !string.IsNullOrEmpty(user.TotpSecret))
            {
                var secret = box.Open(user.TotpSecret);
                var step = Totp.Match(secret, input.Code ?? "", user.TotpLastStep);
                if (step is null) throw new AppException("Enter the 6-digit code from your authenticator app.", 401);
                user.TotpLastStep = step.Value;
            }
            else
            {
                var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<User>();
                if (string.IsNullOrEmpty(input.Password) || hasher.VerifyHashedPassword(user, user.PasswordHash, input.Password) == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed)
                    throw new AppException("Incorrect password.", 401);
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToHexString(tokenBytes).ToLowerInvariant();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

            var share = new VerifiedShare
            {
                CompanyId = ctx.CompanyId,
                LenderName = input.LenderName.Trim(),
                LenderContact = input.LenderContact?.Trim() ?? "",
                Months = input.Months,
                TokenHash = tokenHash,
                CreatedByUserId = user.Id,
                CreatedBy = user.Name,
                CreatedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(input.Days)
            };

            db.VerifiedShares.Add(share);
            await db.SaveChangesAsync();

            var host = http.Request.Host.Value;
            var scheme = http.Request.Scheme;
            var baseUrl = config["App:PublicUrl"] ?? $"{scheme}://{host}";
            var url = $"{baseUrl.TrimEnd('/')}/v/{token}";

            return Results.Ok(new
            {
                id = share.Id,
                lenderName = share.LenderName,
                months = share.Months,
                expiresAt = share.ExpiresAt,
                token,
                url
            });
        }).RequireAuthorization(Policies.VerifiedShare);

        // 4. Owner only: revoke share
        verified.MapPost("/shares/{id:guid}/revoke", async (Guid id, ErpDb db, RequestContext ctx) =>
        {
            var share = await db.VerifiedShares.FirstOrDefaultAsync(s => s.Id == id);
            if (share is null) return Results.NotFound();
            if (share.RevokedAt.HasValue) return Results.Ok(new { status = "revoked", revokedAt = share.RevokedAt });

            share.RevokedAt = DateTimeOffset.UtcNow;
            share.RevokedBy = ctx.Actor;
            await db.SaveChangesAsync();
            return Results.Ok(new { status = "revoked", revokedAt = share.RevokedAt });
        }).RequireAuthorization(Policies.VerifiedShare);

        // 5. Owner & Accountant: views audit log
        verified.MapGet("/shares/{id:guid}/views", async (Guid id, ErpDb db) =>
        {
            var views = await db.VerifiedShareViews.AsNoTracking()
                .Where(v => v.ShareId == id)
                .OrderByDescending(v => v.At)
                .Take(100)
                .Select(v => new
                {
                    v.Id,
                    v.At,
                    v.IpHash,
                    v.Agent
                })
                .ToListAsync();
            return Results.Ok(views);
        });

        // 6. Public endpoint: lender view (rate limited, no auth)
        app.MapGet("/api/verified/s/{token}", async (string token, ErpDb db, VerifiedService service, IConfiguration config, HttpContext http) =>
        {
            var cleanToken = (token ?? "").Trim().ToLowerInvariant();
            var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cleanToken))).ToLowerInvariant();

            var share = await db.VerifiedShares.IgnoreQueryFilters()
                .FirstOrDefaultAsync(s => s.TokenHash == tokenHash);

            const string endedMessage = "This link has ended. Ask the business for a new one.";
            if (share is null || share.RevokedAt.HasValue || share.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Results.NotFound(new { error = endedMessage });
            }

            // Log lender view
            var clientIp = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            var salt = config["Verified:Salt"] ?? "kabuor-verified-salt-2026";
            var ipHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{clientIp}"))).ToLowerInvariant()[..16];
            var userAgent = http.Request.Headers.UserAgent.ToString();
            if (userAgent.Length > 200) userAgent = userAgent[..200];

            db.VerifiedShareViews.Add(new VerifiedShareView
            {
                CompanyId = share.CompanyId,
                ShareId = share.Id,
                At = DateTimeOffset.UtcNow,
                IpHash = ipHash,
                Agent = userAgent
            });
            await db.SaveChangesAsync();

            // Business info for lender identity
            var company = await db.Companies.IgnoreQueryFilters().SingleAsync(c => c.Id == share.CompanyId);
            var summary = await service.Summary(share.Months, share.CompanyId);

            // Canonical JSON for tamper-evident signature
            var canonicalObj = new
            {
                company = new { name = company.Name, kraPin = company.KraPin, county = company.County },
                lenderName = share.LenderName,
                months = share.Months,
                expiresAt = share.ExpiresAt.ToString("O"),
                summary = new
                {
                    averageMonthlyConfirmed = summary.AverageMonthlyConfirmed,
                    distinctPayers = summary.Payers,
                    monthToDateConfirmed = summary.MonthToDateConfirmed,
                    etims = new { signedShare = summary.Etims.SignedShare, signed = summary.Etims.Signed, issued = summary.Etims.Issued, testSigned = summary.Etims.TestSigned },
                    monthsBreakdown = summary.MonthList.Select(m => new { month = m.Month, confirmed = m.Confirmed, allMoneyIn = m.AllMoneyIn, payers = m.Payers, topFiveShare = m.TopFiveShare, partial = m.Partial })
                }
            };
            var canonicalJson = JsonSerializer.Serialize(canonicalObj);
            var signingKey = config["Verified:SigningKey"] ?? config["JWT__KEY"] ?? "kabuor-verified-default-signing-key-32";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
            var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonicalJson))).ToLowerInvariant();
            const string keyId = "kabuor-verified-hmac-v1";

            return Results.Ok(new
            {
                company = new
                {
                    name = company.Name,
                    kraPin = company.KraPin,
                    county = company.County
                },
                lenderName = share.LenderName,
                months = share.Months,
                expiresAt = share.ExpiresAt,
                checkedAt = summary.CheckedAt,
                summary,
                canonical = canonicalJson,
                keyId,
                signature
            });
        }).RequireRateLimiting("verified_share");

        // 7. Public endpoint: signature key info
        app.MapGet("/api/verified/key", () => Results.Ok(new
        {
            algorithm = "HMAC-SHA256",
            keyId = "kabuor-verified-hmac-v1",
            issuer = "Kabuor ERP Verified"
        }));
    }
}
