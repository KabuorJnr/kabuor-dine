namespace Kabuor.Erp.Domain;

/// <summary>
/// Status of self-service onboarding checklist for a company.
/// </summary>
public sealed record OnboardingStatus(
    bool CompanyProfileCompleted,
    bool PaymentConfigured,
    bool FirstCustomerAdded,
    bool FirstItemAdded,
    bool FirstInvoiceCreated,
    int TotalSteps,
    int CompletedSteps,
    int CompletionPercentage,
    bool IsReadyToTransact,
    string NextRecommendedAction,
    List<OnboardingStep>? Steps = null
);

/// <summary>One thing to set up, where to do it, and whether it's done (the app's setup checklist).</summary>
public sealed record OnboardingStep(string Key, string Title, string Detail, bool Done, string Href, string Action);

public sealed record QuickstartCustomerInput(
    string Name,
    string? Phone = null,
    string? Email = null
);

public sealed record QuickstartItemInput(
    string Name,
    long UnitPriceCents,
    string? Unit = null
);

/// <summary>
/// Payload to setup an organization and generate its first invoice in a single flow.
/// </summary>
public sealed record QuickstartSetupInput(
    string? MpesaShortcode = null,
    string? StkEnvironment = "sandbox",
    QuickstartCustomerInput? Customer = null,
    QuickstartItemInput? Item = null,
    int? Quantity = 1,
    bool CreateFirstInvoice = true
);

public sealed record QuickstartSetupResult(
    Guid? CustomerId,
    Guid? ItemId,
    Guid? InvoiceId,
    string? InvoiceNumber,
    long TotalCents,
    string Status,
    string Message
);
