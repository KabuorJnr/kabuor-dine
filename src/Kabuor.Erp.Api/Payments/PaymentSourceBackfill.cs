// Payments recorded before payments carried their source (PaymentSource.Unknown) get one from what the database already
// knows about how each arrived, once, shortly after start-up; new payments are labelled where they're created. First
// match wins: a paid M-Pesa prompt's receipt; an audit entry showing M-Pesa created it (a Safaricom confirmation); the
// public API; a statement line it was recorded from; a till reference; otherwise it was typed in. None of these
// counts as confirmed by Safaricom: no callback address was recorded for them.
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Payments;

public sealed class PaymentSourceBackfill(IServiceScopeFactory scopes, IConfiguration config, ILogger<PaymentSourceBackfill> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (config.GetValue("Backfill:Disabled", false)) return; // tests run it explicitly
        await Task.Delay(TimeSpan.FromSeconds(20), stop);
        try
        {
            using var scope = scopes.CreateScope();
            var labelled = await Run(scope.ServiceProvider.GetRequiredService<ErpDb>());
            if (labelled > 0) log.LogInformation("Labelled the source of {Count} earlier payments", labelled);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "Labelling earlier payments' source failed; it runs again on the next start");
        }
    }

    /// <summary>Labels every payment whose source is still Unknown. Safe to run again: labelled ones are left alone.</summary>
    public static async Task<int> Run(ErpDb db)
    {
        var unknown = await db.Payments.IgnoreQueryFilters().Where(p => p.Source == PaymentSource.Unknown).Select(p => new { p.Id, p.Reference }).ToListAsync();
        if (unknown.Count == 0) return 0;
        var ids = unknown.Select(p => p.Id).ToHashSet();

        var promptReceipts = (await db.MpesaRequests.IgnoreQueryFilters().Where(r => r.Status == MpesaRequestStatus.Paid && r.Receipt != "").Select(r => r.Receipt).ToListAsync())
            .Select(r => r.ToUpperInvariant()).ToHashSet();
        // GUIDs are compared parsed: SQLite and Postgres write them differently.
        var byMpesa = (await db.AuditEntries.IgnoreQueryFilters().Where(a => a.Entity == nameof(Payment) && a.Action == "Added" && a.Actor == "M-Pesa").Select(a => a.EntityId).ToListAsync())
            .Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(ids.Contains).ToHashSet();
        var byApi = (await db.PaymentAllocations.IgnoreQueryFilters().Where(a => a.MatchedBy == "user: API").Select(a => a.PaymentId).ToListAsync()).ToHashSet();
        var fromStatements = (await db.StatementLines.IgnoreQueryFilters().Where(l => l.MatchKind == "Payment" && l.MatchId != null).Select(l => new { Id = l.MatchId!.Value, l.Reference }).ToListAsync())
            .Where(l => ids.Contains(l.Id)).ToDictionary(l => l.Id, l => l.Reference.ToUpperInvariant());

        PaymentSource Guess(Guid id, string reference) =>
            promptReceipts.Contains(reference) ? PaymentSource.MpesaPrompt
            : byMpesa.Contains(id) ? PaymentSource.MpesaConfirmation
            : byApi.Contains(id) ? PaymentSource.ConnectedSystem
            : fromStatements.TryGetValue(id, out var lineRef) && (reference == lineRef || reference.StartsWith("STMT-")) ? PaymentSource.StatementImport
            : reference.EndsWith("-POS") ? PaymentSource.Till
            : PaymentSource.Manual;

        var labelled = 0;
        foreach (var group in unknown.GroupBy(p => Guess(p.Id, p.Reference)))
            foreach (var chunk in group.Select(p => p.Id).Chunk(500))
                labelled += await db.Payments.IgnoreQueryFilters().Where(p => chunk.Contains(p.Id) && p.Source == PaymentSource.Unknown)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Source, group.Key));
        return labelled;
    }
}
