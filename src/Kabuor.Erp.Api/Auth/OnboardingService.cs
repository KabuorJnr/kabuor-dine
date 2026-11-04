using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Auth;

/// <summary>
/// Streamlines onboarding and quick-start self-service for newly registered organizations.
/// Allows a business owner to configure payment collection, add first client & item,
/// and issue their first invoice in under 5 minutes.
/// </summary>
public sealed class OnboardingService(
    ErpDb db,
    SalesService sales,
    Numbers numbers)
{
    public async Task<OnboardingStatus> GetStatus()
    {
        // One round trip: the company's settings and every "have they done it yet" check come back together.
        var companyId = db.CompanyId;
        var company = await db.Companies.Where(c => c.Id == companyId).Select(c => new
        {
            c.Name, c.Email, c.Phone, c.KraPin, c.VatRegistered, c.MpesaShortcode, c.StkConsumerKey, c.StkShortcode, c.Industry,
            HasCustomer = db.Customers.Any(),
            HasItem = db.Items.Any(),
            HasInvoice = db.SalesDocs.Any(s => s.Kind == SalesDocKind.Invoice),
            Staff = db.Users.Count(u => u.Role != Role.Employee),
            HasVehicle = db.Assets.Any(a => a.Category == "VEH"),
        }).SingleOrDefaultAsync() ?? throw new AppException("Company not found.", 404);

        var hasCompanyProfile = !string.IsNullOrWhiteSpace(company.Name) &&
            (!string.IsNullOrWhiteSpace(company.Email) || !string.IsNullOrWhiteSpace(company.Phone));
        // Invoices can be signed for KRA once the PIN is in (or the business says it isn't VAT registered).
        var taxReady = company.KraPin.Length > 0 || !company.VatRegistered;

        var hasPaymentConfigured = !string.IsNullOrWhiteSpace(company.MpesaShortcode) ||
                                   !string.IsNullOrWhiteSpace(company.StkConsumerKey) ||
                                   !string.IsNullOrWhiteSpace(company.StkShortcode);

        var (hasCustomer, hasItem, hasInvoice, hasTeam) = (company.HasCustomer, company.HasItem, company.HasInvoice, company.Staff > 1);
        // Businesses that deliver their own goods start with their vehicles.
        var delivers = company.Industry is "transportation" or "distribution";
        var hasVehicle = delivers && company.HasVehicle;

        int total = 4;
        int completed = 0;
        if (hasPaymentConfigured) completed++;
        if (hasCustomer) completed++;
        if (hasItem) completed++;
        if (hasInvoice) completed++;

        var steps = new List<OnboardingStep>
        {
            new("tax", "Add your KRA PIN", "Printed on every invoice and needed to sign them with eTIMS.", taxReady, "/settings?tab=company", "Add PIN"),
            new("mpesa", "Connect your M-Pesa till or paybill", "Payments customers make arrive here and match their invoices by themselves.", hasPaymentConfigured, "/settings?tab=company", "Connect M-Pesa"),
            new("items", "Add what you sell", "Products and services with their prices, or bring your list in from Excel.", hasItem, "/settings?tab=items", "Add products"),
            new("customers", "Add your customers", "Type them in, or import them from Excel, QuickBooks or Sage.", hasCustomer, "/customers", "Add customers"),
            new("invoice", "Send your first invoice", "Signed for KRA and shared on WhatsApp in a minute.", hasInvoice, "/invoices/new", "New invoice"),
            new("team", "Invite your team", "Each person gets their own login and sees only what their role needs.", hasTeam, "/settings?tab=team", "Add a person"),
        };
        if (delivers)
            steps.Insert(4, new("fleet", "Add your vehicles", "Lorries, vans and motorbikes with their number plates, so trips and waybills can go out.", hasVehicle, "/fleet?view=vehicles", "Add vehicles"));
        var done = steps.Count(x => x.Done);

        string nextAction = steps.FirstOrDefault(x => !x.Done)?.Title ?? "You're all set! Ready to transact and grow.";

        return new OnboardingStatus(
            CompanyProfileCompleted: hasCompanyProfile,
            PaymentConfigured: hasPaymentConfigured,
            FirstCustomerAdded: hasCustomer,
            FirstItemAdded: hasItem,
            FirstInvoiceCreated: hasInvoice,
            TotalSteps: total,
            CompletedSteps: completed,
            CompletionPercentage: (int)Math.Round((double)done / steps.Count * 100),
            IsReadyToTransact: hasPaymentConfigured && hasInvoice,
            NextRecommendedAction: nextAction,
            Steps: steps
        );
    }

    public async Task<QuickstartSetupResult> Quickstart(QuickstartSetupInput input)
    {
        var company = await db.Companies.SingleOrDefaultAsync(c => c.Id == db.CompanyId)
            ?? throw new AppException("Company not found.", 404);

        // 1. Configure payments if provided
        if (!string.IsNullOrWhiteSpace(input.MpesaShortcode))
        {
            company.MpesaShortcode = new string(input.MpesaShortcode.Where(char.IsDigit).ToArray());
            if (!string.IsNullOrWhiteSpace(input.StkEnvironment))
                company.StkEnvironment = input.StkEnvironment.Trim();
        }

        // 2. Add Customer if provided
        Customer? customer = null;
        if (input.Customer is not null && !string.IsNullOrWhiteSpace(input.Customer.Name))
        {
            customer = new Customer
            {
                CompanyId = company.Id,
                Number = await numbers.Next("CUST"),
                Name = input.Customer.Name.Trim(),
                Phone = Phones.ToMsisdn(input.Customer.Phone) ?? (input.Customer.Phone ?? "").Trim(),
                Email = (input.Customer.Email ?? "").Trim(),
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }
        else
        {
            customer = await db.Customers.FirstOrDefaultAsync();
        }

        // 3. Add Item if provided
        Item? item = null;
        if (input.Item is not null && !string.IsNullOrWhiteSpace(input.Item.Name))
        {
            item = new Item
            {
                CompanyId = company.Id,
                Name = input.Item.Name.Trim(),
                Price = Math.Max(0, input.Item.UnitPriceCents),
                Unit = string.IsNullOrWhiteSpace(input.Item.Unit) ? "pc" : input.Item.Unit.Trim(),
                Kind = ItemKind.Service,
                TaxType = TaxType.B_Standard16,
            };
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }
        else
        {
            item = await db.Items.FirstOrDefaultAsync();
        }

        // 4. Generate first invoice if requested
        Guid? invoiceId = null;
        string? invoiceNumber = null;
        long totalCents = 0;

        if (input.CreateFirstInvoice && customer is not null)
        {
            var lines = new List<LineInput>();
            decimal qty = Math.Max(1, input.Quantity ?? 1);

            if (item is not null)
            {
                lines.Add(new LineInput(item.Id, item.Name, qty, item.Price, TaxType.B_Standard16));
            }
            else
            {
                lines.Add(new LineInput(null, "Starter Service / Product", qty, 100000, TaxType.B_Standard16));
            }

            var doc = await sales.Create(new SalesDocInput(
                Kind: SalesDocKind.Invoice,
                CustomerId: customer.Id,
                IssueDate: Clock.Today,
                DueInDays: 7,
                Notes: "Thank you for your business!",
                Lines: lines
            ));

            // Issue invoice to post ledger and calculate balances
            doc = await sales.Issue(doc.Id);

            invoiceId = doc.Id;
            invoiceNumber = doc.Number;
            totalCents = doc.Total;

            ErpTelemetry.RecordInvoice("invoice", doc.Total, company.Id);
        }

        await db.SaveChangesAsync();

        return new QuickstartSetupResult(
            CustomerId: customer?.Id,
            ItemId: item?.Id,
            InvoiceId: invoiceId,
            InvoiceNumber: invoiceNumber,
            TotalCents: totalCents,
            Status: "Ready",
            Message: invoiceId is not null
                ? $"Onboarding complete! First invoice {invoiceNumber} issued for {Money.Ksh(totalCents)}."
                : "Settings and catalog updated successfully."
        );
    }
}
