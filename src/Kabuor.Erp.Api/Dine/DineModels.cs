// Kabuor Dine: QR ordering for restaurants, cafés and hotels, on the same books as Kabuor ERP.
// A guest scans the QR on their table, orders from the menu (with their preferences), the order lands on the
// kitchen and bar screens, the waiter serves it, and the bill is paid by M-Pesa prompt or at the table. Every bill
// is an ordinary invoice: stock goes down, the sale is in the ledger and signed with eTIMS, and the payment lands in
// money-in like any other.
namespace Kabuor.Erp.Domain;

public enum DinePayMode
{
    /// <summary>Sit-down: orders go straight to the kitchen and add to the table's bill; guests pay when they're done.</summary>
    Tab,
    /// <summary>Quick service: an order goes to the kitchen once it's paid.</summary>
    PayFirst,
}

/// <summary>One per business: how its tables order and pay.</summary>
public sealed class DineSettings : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public DinePayMode PayMode { get; set; } = DinePayMode.Tab;
    /// <summary>A new table's first order waits for a waiter to confirm someone is really sitting there (no orders from a QR photo at home).</summary>
    public bool ApproveFirstOrder { get; set; } = true;
    /// <summary>The kitchen screens, comma separated (Kitchen, Bar, Grill…). Each dish goes to one.</summary>
    public string Stations { get; set; } = "Kitchen,Bar";
    /// <summary>Shown at the top of the guest menu.</summary>
    public string Welcome { get; set; } = "";
    /// <summary>Taking orders from tables right now. Off: the menu still shows, with a note to ask a waiter.</summary>
    public bool Open { get; set; } = true;
}

public sealed class DineTable : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public required string Name { get; set; }
    /// <summary>Inside, Terrace, VIP… for grouping on the floor screen.</summary>
    public string Area { get; set; } = "";
    public int Seats { get; set; } = 4;
    /// <summary>The secret in the table's QR code. A new code makes old printed QRs (and photos of them) stop working.</summary>
    public string Token { get; set; } = "";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>How an item appears on the menu: its section, the screen that makes it, a description and the choices guests make.</summary>
public sealed class DineMenuEntry : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid ItemId { get; set; }
    public string Category { get; set; } = "Mains";
    public string Station { get; set; } = "Kitchen";
    public string Description { get; set; } = "";
    /// <summary>One choice per line, "Name: option | option | option", e.g. "Spice: Mild | Medium | Hot".</summary>
    public string Options { get; set; } = "";
    /// <summary>Taken off the menu for now (run out, out of season), whatever the stock says.</summary>
    public bool SoldOut { get; set; }
    /// <summary>Hidden from guests: not on the QR menu at all (staff-only items, delivery packaging…).</summary>
    public bool Hidden { get; set; }
    public bool Popular { get; set; }
    /// <summary>"Goes well with": an item suggested when this one is added.</summary>
    public Guid? SuggestItemId { get; set; }
    public int Sort { get; set; }
}

public enum DineSessionStatus
{
    Open,
    Paid,
    Closed,
}

/// <summary>A table's visit, from the first order to the table being cleared. Everyone at the table orders into it.</summary>
public sealed class DineSession : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid TableId { get; set; }
    public string Number { get; set; } = "";
    public DineSessionStatus Status { get; set; } = DineSessionStatus.Open;
    /// <summary>A waiter has confirmed the table (or the business doesn't ask for that).</summary>
    public bool Approved { get; set; }
    public DateTimeOffset OpenedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ClosedAt { get; set; }
    public DateTimeOffset? CallWaiterAt { get; set; }
    public DateTimeOffset? BillRequestedAt { get; set; }
    /// <summary>The guest's phone, once they give it to pay: their receipt and the customer record go to it.</summary>
    public string Phone { get; set; } = "";
    public int Rating { get; set; }
    public string Feedback { get; set; } = "";
}

public enum DineOrderStatus
{
    /// <summary>The table's first order, waiting for a waiter to confirm the table.</summary>
    AwaitingApproval,
    /// <summary>Pay-first: billed, waiting for the money before the kitchen sees it.</summary>
    AwaitingPayment,
    New,
    Preparing,
    Ready,
    Served,
    Cancelled,
}

public sealed class DineOrder : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid SessionId { get; set; }
    public Guid TableId { get; set; }
    public string Number { get; set; } = "";
    public DineOrderStatus Status { get; set; } = DineOrderStatus.New;
    public string GuestName { get; set; } = "";
    public string Note { get; set; } = "";
    /// <summary>Who placed it: "guest" from the QR menu, or the waiter's name.</summary>
    public string PlacedBy { get; set; } = "guest";
    public DateTimeOffset PlacedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>When the kitchen got it (after approval, or payment in pay-first).</summary>
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReadyAt { get; set; }
    public DateTimeOffset? ServedAt { get; set; }
    /// <summary>The invoice this order was billed on (null until billed).</summary>
    public Guid? InvoiceId { get; set; }
    public long Total { get; set; }
    public List<DineOrderLine> Lines { get; set; } = [];
}

public sealed class DineOrderLine : ICompanyOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CompanyId { get; set; }
    public Guid OrderId { get; set; }
    public Guid ItemId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>VAT-inclusive price of one, in cents, as on the menu when ordered.</summary>
    public long UnitPrice { get; set; }
    /// <summary>The guest's choices, e.g. "Spice: Hot · Side: Ugali".</summary>
    public string Options { get; set; } = "";
    public string Note { get; set; } = "";
    public string Station { get; set; } = "Kitchen";
    /// <summary>Its station has marked it ready.</summary>
    public bool Ready { get; set; }
}
