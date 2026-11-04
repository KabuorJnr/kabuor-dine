using System.Diagnostics;
using System.Diagnostics.Metrics;
using Kabuor.Erp.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kabuor.Erp.Common;

/// <summary>
/// Telemetry, distributed tracing, and metrics for Kabuor ERP.
/// Compatible with OpenTelemetry, Prometheus, Grafana, Datadog, and Sentry.
/// </summary>
public static class ErpTelemetry
{
    public const string ServiceName = "Kabuor.Erp";
    public const string ServiceVersion = "1.0.0";

    // Activity source for OpenTelemetry distributed tracing
    public static readonly ActivitySource ActivitySource = new(ServiceName, ServiceVersion);

    // Metrics Meter for Prometheus / OpenTelemetry metrics
    public static readonly Meter Meter = new(ServiceName, ServiceVersion);

    // Key business & operational counters
    private static readonly Counter<long> PaymentsProcessedCounter = Meter.CreateCounter<long>(
        "kabuor_payments_processed_total",
        unit: "{payments}",
        description: "Total number of payment transactions processed");

    private static readonly Counter<long> PaymentVolumeCentsCounter = Meter.CreateCounter<long>(
        "kabuor_payment_volume_cents_total",
        unit: "{cents}",
        description: "Total gross payment volume processed in cents");

    private static readonly Counter<long> InvoicesIssuedCounter = Meter.CreateCounter<long>(
        "kabuor_invoices_issued_total",
        unit: "{invoices}",
        description: "Total number of sales invoices created");

    private static readonly Counter<long> SecurityEventsCounter = Meter.CreateCounter<long>(
        "kabuor_security_events_total",
        unit: "{events}",
        description: "Security and authentication events such as failed logins or account locks");

    private static readonly Counter<long> WebhookDeliveriesCounter = Meter.CreateCounter<long>(
        "kabuor_webhook_deliveries_total",
        unit: "{deliveries}",
        description: "Total outbound webhook delivery attempts");

    /// <summary>Starts an activity span for tracing payment processing operations.</summary>
    public static Activity? StartPaymentActivity(string operation, string channel, Guid companyId, string? reference = null)
    {
        var activity = ActivitySource.StartActivity($"Payment.{operation}", ActivityKind.Internal);
        if (activity is not null)
        {
            activity.SetTag("payment.channel", channel);
            activity.SetTag("tenant.company_id", companyId.ToString());
            if (!string.IsNullOrEmpty(reference)) activity.SetTag("payment.reference", reference);
        }
        return activity;
    }

    /// <summary>Records a completed or attempted payment transaction in metrics.</summary>
    public static void RecordPayment(string channel, long amountCents, bool success, Guid companyId)
    {
        var tags = new TagList
        {
            { "channel", channel },
            { "status", success ? "success" : "failed" },
            { "tenant", companyId.ToString() }
        };

        PaymentsProcessedCounter.Add(1, tags);
        if (success && amountCents > 0)
        {
            PaymentVolumeCentsCounter.Add(amountCents, new TagList
            {
                { "channel", channel },
                { "tenant", companyId.ToString() }
            });
        }
    }

    /// <summary>Records the creation of an invoice.</summary>
    public static void RecordInvoice(string kind, long totalCents, Guid companyId)
    {
        InvoicesIssuedCounter.Add(1, new TagList
        {
            { "kind", kind },
            { "tenant", companyId.ToString() }
        });
    }

    /// <summary>Records a security event such as a failed login attempt.</summary>
    public static void RecordSecurityEvent(string eventType, string outcome, string? clientIp = null)
    {
        SecurityEventsCounter.Add(1, new TagList
        {
            { "event_type", eventType },
            { "outcome", outcome },
            { "client_ip", clientIp ?? "unknown" }
        });
    }

    /// <summary>Records outbound webhook delivery status.</summary>
    public static void RecordWebhookDelivery(string eventType, bool success)
    {
        WebhookDeliveriesCounter.Add(1, new TagList
        {
            { "event_type", eventType },
            { "status", success ? "delivered" : "failed" }
        });
    }
}

/// <summary>Health check that tests database responsiveness.</summary>
public sealed class ErpDbHealthCheck(ErpDb db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var canConnect = await db.Database.CanConnectAsync(cancellationToken);
            return canConnect
                ? HealthCheckResult.Healthy("Database is responsive and accepting connections.")
                : HealthCheckResult.Unhealthy("Database connection check failed.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database health check threw an exception.", ex);
        }
    }
}
