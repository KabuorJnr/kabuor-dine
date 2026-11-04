// PostgreSQL over the network (Neon) sometimes drops a connection for a moment; a plain read or save is simply tried
// again. But a sale at the till, or an order from the shop API, runs in one transaction of its own: retrying one step
// of it on a fresh connection would leave half a sale (or sign it with KRA twice). Npgsql's retrying strategy refuses
// such transactions outright, so every till sale failed. This strategy retries outside a transaction and, inside one,
// lets the failure through so the whole transaction rolls back and the person tries again.
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql.EntityFrameworkCore.PostgreSQL;

namespace Kabuor.Erp.Data;

public sealed class RetryOutsideTransactions(ExecutionStrategyDependencies dependencies)
    : NpgsqlRetryingExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null)
{
    // Only asked once a statement runs. RetriesOnFailure stays as it is: EF reads it while it is still building the
    // context's services, and asking the context about its transaction from there builds them again, forever.
    bool InTransaction => Dependencies.CurrentContext.Context.Database.CurrentTransaction is not null;

    protected override bool ShouldRetryOn(Exception? exception) => !InTransaction && base.ShouldRetryOn(exception);

    // The base class refuses to start inside a transaction; here that just means "don't retry".
    protected override void OnFirstExecution()
    {
        if (InTransaction) ExceptionsEncountered.Clear();
        else base.OnFirstExecution();
    }
}
