// Kabuor Dine (see DineModels.cs): the guest's QR menu, the kitchen and bar screens, the floor, the menu and tables,
// and the bill. A bill is an ordinary ERP invoice (SalesService), so stock, the ledger, eTIMS and money-in all
// follow without anything special for restaurants.
using System.Security.Cryptography;
using System.Text;
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Payments;
using Kabuor.Erp.Sales;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp.Dine;

public sealed record DineLineInput(Guid ItemId, int Quantity, string? Options, string? Note);
public sealed record DineOrderInput(List<DineLineInput> Lines, string? GuestName, string? Note);
public sealed record DineCallInput(string Kind);
public sealed record DinePayInput(string? Phone);
public sealed record DineRateInput(int Stars, string? Comment);
public sealed record DineStatusInput(string Status);
public sealed record DineStationInput(string Station);
public sealed record DineTillInput(string Method, string? Reference, string? Phone);
public sealed record DineTableInput(string? Name, string? Area, int? Seats, int? Count, string? Prefix);
public sealed record DineMenuInput(string? Name, long? Price, string? Category, string? Station, string? Description, string? Options,
    bool? SoldOut, bool? Hidden, bool? Popular, Guid? SuggestItemId, int? Sort, bool? ClearSuggestion = null);
public sealed record DineSettingsInput(string? PayMode, bool? ApproveFirstOrder, string? Stations, string? Welcome, bool? Open);

/// <summary>A guest's hold on their table's visit: company, session and expiry, signed with the server's key.</summary>
public sealed class DineTokens(TokenIssuer tokens)
{
    byte[] Key => SHA256.HashData([.. tokens.Key.Key, .. "kabuor-erp/dine"u8]);

    string Sign(string body) => Convert.ToBase64String(HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(body))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Issue(Guid company, Guid session) => $"{company:N}.{session:N}.{DateTimeOffset.UtcNow.AddHours(12).ToUnixTimeSeconds()}" is var b ? $"{b}.{Sign(b)}" : "";

    public (Guid Company, Guid Session)? Read(string? token)
    {
        var parts = (token ?? "").Split('.');
        if (parts.Length != 4 || !Guid.TryParseExact(parts[0], "N", out var company) || !Guid.TryParseExact(parts[1], "N", out var session) || !long.TryParse(parts[2], out var exp)) return null;
        var want = Sign(string.Join('.', parts[..3]));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(want), Encoding.UTF8.GetBytes(parts[3]))) return null;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() > exp ? null : (company, session);
    }
}

public sealed class DineService(ErpDb db, RequestContext ctx, Numbers numbers, SalesService sales, PaymentService payments, StkService stk, DineTokens tokens)
{
    static readonly DineOrderStatus[] Kitchen = [DineOrderStatus.New, DineOrderStatus.Preparing, DineOrderStatus.Ready];
    static readonly DineOrderStatus[] Billable = [DineOrderStatus.New, DineOrderStatus.Preparing, DineOrderStatus.Ready, DineOrderStatus.Served];
    static string Clean(string? s, int max) => (s ?? "").Trim() is var t && t.Length > max ? t[..max] : t;
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(9)).ToLowerInvariant();

    public async Task<DineSettings> Settings()
    {
        var s = await db.DineSettings.SingleOrDefaultAsync();
        if (s is not null) return s;
        s = new DineSettings();
        db.DineSettings.Add(s);
        await db.SaveChangesAsync();
        return s;
    }

    static List<string> StationsOf(DineSettings s) =>
        s.Stations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).DefaultIfEmpty("Kitchen").ToList();

    /// <summary>A new business's first tables, so there's a QR to print on day one.</summary>
    public async Task Start(int tables = 6)
    {
        await Settings();
        if (await db.DineTables.AnyAsync()) return;
        for (var i = 1; i <= tables; i++) db.DineTables.Add(new DineTable { Name = $"Table {i}", Token = NewToken() });
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------------------------------------ the menu

    /// <summary>The menu as guests see it: sections in order, each dish with its price, choices and whether it can be had now.</summary>
    async Task<List<object>> MenuItems(bool guests)
    {
        var items = await db.Items.Where(i => i.Active && i.Use == ItemUse.Sale).OrderBy(i => i.Name).ToListAsync();
        var entries = await db.DineMenu.ToDictionaryAsync(e => e.ItemId);
        var onHand = await db.StockLevels.GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        var names = items.ToDictionary(i => i.Id, i => i.Name);
        return items
            .Select(i => (i, e: entries.GetValueOrDefault(i.Id)))
            .Where(x => !guests || (x.i.Price > 0 && x.e is { Hidden: false }))
            .OrderBy(x => x.e?.Sort ?? 999).ThenBy(x => x.i.Name)
            .Select(x => (object)new
            {
                x.i.Id, x.i.Name, x.i.Price, x.i.Unit, x.i.TrackStock,
                onHand = x.i.TrackStock ? onHand.GetValueOrDefault(x.i.Id) : (decimal?)null,
                onMenu = x.e is { Hidden: false },
                category = x.e?.Category ?? "", station = x.e?.Station ?? "Kitchen", description = x.e?.Description ?? "",
                options = ParseOptions(x.e?.Options ?? ""), optionsText = x.e?.Options ?? "",
                soldOut = (x.e?.SoldOut ?? false) || (x.i.TrackStock && onHand.GetValueOrDefault(x.i.Id) <= 0),
                markedSoldOut = x.e?.SoldOut ?? false, hidden = x.e?.Hidden ?? false,
                popular = x.e?.Popular ?? false, sort = x.e?.Sort ?? 0,
                suggest = x.e?.SuggestItemId is { } sid && names.TryGetValue(sid, out var sn) ? new { id = sid, name = sn } : null,
            }).ToList();
    }

    /// <summary>"Spice: Mild | Medium | Hot" lines → choice groups.</summary>
    static List<object> ParseOptions(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split(':', 2))
        .Where(p => p.Length == 2 && p[0].Trim().Length > 0)
        .Select(p => (object)new { name = p[0].Trim(), choices = p[1].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) })
        .ToList();

    public async Task<object> StaffMenu()
    {
        var s = await Settings();
        return new { stations = StationsOf(s), items = await MenuItems(false) };
    }

    /// <summary>Puts an item on the menu (or changes how it shows). A new dish is also a new item for sale.</summary>
    public async Task<object> SaveDish(Guid? itemId, DineMenuInput input)
    {
        Item item;
        if (itemId is { } id)
        {
            item = await db.Items.SingleOrDefaultAsync(i => i.Id == id) ?? throw new AppException("Not found.", 404);
            if (item.Use != ItemUse.Sale) throw new AppException("That item is kept in stores, not sold.");
        }
        else
        {
            var name = Clean(input.Name, 120);
            if (name.Length < 2) throw new AppException("Name the dish.");
            if (await db.Items.AnyAsync(i => i.Name == name)) throw new AppException($"There's already an item called {name}. Put that one on the menu instead.", 409);
            item = new Item { Name = name, Unit = "plate", Kind = ItemKind.Product };
            db.Items.Add(item);
        }
        if (input.Name is not null && Clean(input.Name, 120) is { Length: >= 2 } n) item.Name = n;
        if (input.Price is { } price)
        {
            if (price <= 0 || price > 100_000_000) throw new AppException("Give the dish a price.");
            if (price != item.Price && itemId is not null && !ctx.Can(Policies.Finance) && ctx.Role != Role.Owner) throw new AppException("Only the owner or an accountant can change prices.", 403);
            item.Price = price;
        }
        if (item.Price <= 0) throw new AppException("Give the dish a price.");

        var e = await db.DineMenu.SingleOrDefaultAsync(x => x.ItemId == item.Id);
        if (e is null) db.DineMenu.Add(e = new DineMenuEntry { ItemId = item.Id });
        var stations = StationsOf(await Settings());
        if (input.Category is not null) e.Category = Clean(input.Category, 60) is { Length: > 0 } c ? c : "Mains";
        if (input.Station is not null) e.Station = stations.FirstOrDefault(s => s.Equals(input.Station.Trim(), StringComparison.OrdinalIgnoreCase)) ?? throw new AppException($"Choose one of the screens: {string.Join(", ", stations)}.");
        if (input.Description is not null) e.Description = Clean(input.Description, 400);
        if (input.Options is not null) e.Options = Clean(input.Options.Replace("\r", ""), 1000);
        if (input.SoldOut is { } so) e.SoldOut = so;
        if (input.Hidden is { } h) e.Hidden = h;
        if (input.Popular is { } p) e.Popular = p;
        if (input.Sort is { } sort) e.Sort = Math.Clamp(sort, 0, 9999);
        if (input.ClearSuggestion == true) e.SuggestItemId = null;
        else if (input.SuggestItemId is { } sid)
        {
            if (sid == item.Id || !await db.Items.AnyAsync(i => i.Id == sid && i.Active && i.Use == ItemUse.Sale)) throw new AppException("Suggest another dish or drink that's for sale.");
            e.SuggestItemId = sid;
        }
        await db.SaveChangesAsync();
        return new { item.Id, item.Name, item.Price, e.Category, e.Station };
    }

    /// <summary>A starter menu of Kenyan favourites, so a new restaurant sees the whole flow before typing its own.</summary>
    public async Task<object> SampleMenu()
    {
        (string Name, long Price, string Cat, string Station, string Desc, string Options, bool Popular)[] dishes =
        [
            ("Nyama choma (½ kg)", 950_00, "Grill", "Kitchen", "Goat ribs slow-roasted over charcoal, with kachumbari.", "Doneness: Medium | Well done\nSide: Ugali | Chips | Mukimo", true),
            ("Tilapia (whole, fried)", 1_100_00, "Mains", "Kitchen", "Lake Victoria tilapia, fried or wet-fry in tomato and onion.", "Style: Fried | Wet fry\nSide: Ugali | Chips | Rice", true),
            ("Chicken stew", 750_00, "Mains", "Kitchen", "Kienyeji-style chicken in a rich tomato stew.", "Side: Ugali | Rice | Chapati", false),
            ("Beef pilau", 600_00, "Mains", "Kitchen", "Spiced rice with tender beef, served with kachumbari.", "Spice: Mild | Medium | Hot", false),
            ("Githeri special", 450_00, "Mains", "Kitchen", "Maize and beans with potatoes and greens.", "", false),
            ("Chips masala", 400_00, "Snacks", "Kitchen", "Chips tossed in a spicy masala sauce.", "Spice: Mild | Medium | Hot", true),
            ("Samosa (3 pcs)", 250_00, "Snacks", "Kitchen", "Beef or vegetable samosas.", "Filling: Beef | Vegetable", false),
            ("Chapati (2 pcs)", 100_00, "Sides", "Kitchen", "Soft layered chapati.", "", false),
            ("Kachumbari", 150_00, "Sides", "Kitchen", "Fresh tomato, onion and coriander.", "", false),
            ("Fresh juice", 250_00, "Drinks", "Bar", "Pressed today.", "Fruit: Passion | Mango | Watermelon | Mixed\nIce: With ice | No ice", true),
            ("Dawa cocktail (mocktail)", 400_00, "Drinks", "Bar", "Lime, honey and ginger, the Kenyan classic, alcohol-free.", "", false),
            ("Soda (500 ml)", 120_00, "Drinks", "Bar", "", "Flavour: Coke | Fanta | Sprite | Stoney\nTemperature: Cold | Warm", false),
            ("Kenyan tea", 150_00, "Drinks", "Bar", "Chai with milk and spices.", "Sugar: With sugar | No sugar", false),
            ("Mineral water (500 ml)", 100_00, "Drinks", "Bar", "", "Temperature: Cold | Warm", false),
        ];
        var stations = StationsOf(await Settings());
        var added = 0;
        var made = new Dictionary<string, Guid>();
        var sort = 10;
        foreach (var d in dishes)
        {
            var existing = await db.Items.FirstOrDefaultAsync(i => i.Name == d.Name);
            if (existing is not null) { made[d.Name] = existing.Id; continue; }
            var item = new Item { Name = d.Name, Price = d.Price, Unit = "plate", Kind = ItemKind.Product };
            db.Items.Add(item);
            db.DineMenu.Add(new DineMenuEntry
            {
                ItemId = item.Id, Category = d.Cat, Station = stations.Contains(d.Station, StringComparer.OrdinalIgnoreCase) ? d.Station : stations[0],
                Description = d.Desc, Options = d.Options, Popular = d.Popular, Sort = sort += 10,
            });
            made[d.Name] = item.Id;
            added++;
        }
        await db.SaveChangesAsync();
        // "Goes well with": chips with the choma, juice with the fish.
        async Task Pair(string a, string b)
        {
            if (!made.TryGetValue(a, out var ia) || !made.TryGetValue(b, out var ib)) return;
            if (await db.DineMenu.SingleOrDefaultAsync(e => e.ItemId == ia) is { SuggestItemId: null } e) e.SuggestItemId = ib;
        }
        await Pair("Nyama choma (½ kg)", "Kachumbari");
        await Pair("Tilapia (whole, fried)", "Fresh juice");
        await Pair("Chips masala", "Soda (500 ml)");
        await Pair("Beef pilau", "Fresh juice");
        await db.SaveChangesAsync();
        return new { added };
    }

    // ------------------------------------------------------------------------------------------------ tables

    public async Task<object> Tables()
    {
        var tables = await db.DineTables.Where(t => t.Active).ToListAsync();
        return tables.OrderBy(t => t.Area).ThenBy(t => t.Name.Length).ThenBy(t => t.Name).Select(t => new { t.Id, t.Name, t.Area, t.Seats, t.Token });
    }

    public async Task<object> AddTables(DineTableInput input)
    {
        var count = Math.Clamp(input.Count ?? 1, 1, 100);
        var existing = (await db.DineTables.Where(t => t.Active).Select(t => t.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefix = Clean(input.Prefix, 30) is { Length: > 0 } p ? p : "Table";
        var added = new List<DineTable>();
        if (count == 1 && Clean(input.Name, 40) is { Length: > 0 } name)
        {
            if (existing.Contains(name)) throw new AppException($"There's already a table called {name}.", 409);
            added.Add(new DineTable { Name = name });
        }
        else
        {
            var n = 1;
            while (added.Count < count)
            {
                var candidate = $"{prefix} {n++}";
                if (!existing.Contains(candidate)) added.Add(new DineTable { Name = candidate });
            }
        }
        foreach (var t in added)
        {
            (t.Area, t.Seats, t.Token) = (Clean(input.Area, 40), Math.Clamp(input.Seats ?? 4, 1, 100), NewToken());
            db.DineTables.Add(t);
        }
        await db.SaveChangesAsync();
        return added.Select(t => new { t.Id, t.Name, t.Area, t.Seats, t.Token });
    }

    public async Task<object> UpdateTable(Guid id, DineTableInput input)
    {
        var t = await db.DineTables.SingleOrDefaultAsync(x => x.Id == id && x.Active) ?? throw new AppException("Not found.", 404);
        if (Clean(input.Name, 40) is { Length: > 0 } name && name != t.Name)
        {
            if (await db.DineTables.AnyAsync(x => x.Active && x.Id != id && x.Name == name)) throw new AppException($"There's already a table called {name}.", 409);
            t.Name = name;
        }
        if (input.Area is not null) t.Area = Clean(input.Area, 40);
        if (input.Seats is { } seats) t.Seats = Math.Clamp(seats, 1, 100);
        await db.SaveChangesAsync();
        return new { t.Id, t.Name, t.Area, t.Seats, t.Token };
    }

    /// <summary>A new QR secret: the old printed code (and any photo of it) stops working.</summary>
    public async Task<object> NewCode(Guid id)
    {
        var t = await db.DineTables.SingleOrDefaultAsync(x => x.Id == id && x.Active) ?? throw new AppException("Not found.", 404);
        t.Token = NewToken();
        await db.SaveChangesAsync();
        return new { t.Id, t.Name, t.Token };
    }

    public async Task RemoveTable(Guid id)
    {
        var t = await db.DineTables.SingleOrDefaultAsync(x => x.Id == id && x.Active) ?? throw new AppException("Not found.", 404);
        if (await db.DineSessions.AnyAsync(s => s.TableId == id && s.Status != DineSessionStatus.Closed)) throw new AppException("Someone is seated at that table. Clear it first.", 409);
        (t.Active, t.Token) = (false, $"x{NewToken()}"); // its QR stops working
        await db.SaveChangesAsync();
    }

    // ------------------------------------------------------------------------------------------------ the guest

    public async Task<DineSession?> OpenSession(Guid tableId) =>
        (await db.DineSessions.Where(s => s.TableId == tableId && s.Status != DineSessionStatus.Closed).ToListAsync()).OrderByDescending(s => s.OpenedAt).FirstOrDefault();

    /// <summary>The menu and, for a guest already at the table, their visit so far.</summary>
    public async Task<object> GuestMenu(DineTable table, Guid? sessionId)
    {
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        var s = await Settings();
        var session = sessionId is { } sid ? await db.DineSessions.SingleOrDefaultAsync(x => x.Id == sid && x.TableId == table.Id && x.Status != DineSessionStatus.Closed) : null;
        return new
        {
            restaurant = new { c.Name, c.Phone, c.County, paybill = c.MpesaShortcode, welcome = s.Welcome },
            table = new { table.Name, table.Area },
            open = s.Open, payMode = s.PayMode.ToString(),
            mpesaPrompt = c.StkShortcode.Length > 0 && c.StkConsumerKey.Length > 0 && c.StkPasskey.Length > 0,
            menu = await MenuItems(true),
            visit = session is null ? null : await Visit(session),
        };
    }

    /// <summary>The guest's view of their table's visit: what they ordered and where it's got to, and the bill.</summary>
    public async Task<object> Visit(DineSession session)
    {
        await Sync(session);
        var orders = await db.DineOrders.Include(o => o.Lines).Where(o => o.SessionId == session.Id).ToListAsync();
        var invoiceIds = orders.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value).Distinct().ToList();
        var invoices = await db.SalesDocs.Where(d => invoiceIds.Contains(d.Id)).ToListAsync();
        var live = orders.Where(o => o.Status != DineOrderStatus.Cancelled).ToList();
        var total = live.Sum(o => o.Total);
        var paid = invoices.Sum(d => Math.Min(d.Paid, d.Total));
        var pending = await db.MpesaRequests.Where(r => invoiceIds.Contains(r.SalesDocId) && r.Status == MpesaRequestStatus.Pending).Select(r => r.CreatedAt).ToListAsync();
        return new
        {
            session.Id, session.Number, status = session.Status.ToString(), session.Approved,
            calledWaiter = session.CallWaiterAt != null, askedForBill = session.BillRequestedAt != null, session.Rating,
            total, paid, due = Math.Max(0, total - paid),
            paying = pending.Any(t => t > DateTimeOffset.UtcNow.AddMinutes(-2)),
            orders = live.OrderByDescending(o => o.PlacedAt).Select(o => new
            {
                o.Id, o.Number, status = o.Status.ToString(), o.GuestName, o.Note, o.PlacedAt, o.Total,
                lines = o.Lines.Select(l => new { l.Name, l.Quantity, l.UnitPrice, l.Options, l.Note }),
            }),
            receipts = invoices.Select(d => new { d.Id, d.Number, d.Total, d.Paid, d.EtimsInvoiceNo, d.EtimsControlCode, d.EtimsQrUrl, status = d.Status.ToString() }),
        };
    }

    /// <summary>A guest's order from the table's QR. The first one opens the table's visit.</summary>
    public async Task<(DineSession Session, DineOrder Order, string Token)> GuestOrder(DineTable table, Guid? sessionId, DineOrderInput input)
    {
        var s = await Settings();
        if (!s.Open) throw new AppException("We're not taking orders from the table right now. Please ask a waiter.", 409);
        var session = await OpenSession(table.Id);
        if (session is { Status: DineSessionStatus.Paid })
        {
            // The last party paid and left without the table being cleared: start this one afresh.
            (session.Status, session.ClosedAt) = (DineSessionStatus.Closed, DateTimeOffset.UtcNow);
            session = null;
        }
        if (session is not null && sessionId != session.Id)
        {
            // Someone new at a table that's already ordering: they join it (friends at one table share one bill), but
            // only while it's young or a waiter has confirmed it, so an old photo of the QR can't pile onto a bill.
            if (!session.Approved && session.OpenedAt < DateTimeOffset.UtcNow.AddHours(-3)) throw new AppException("Please ask a waiter to open this table for you.", 409);
        }
        session ??= await NewSession(table.Id, approved: !s.ApproveFirstOrder);
        var orders = await db.DineOrders.CountAsync(o => o.SessionId == session.Id);
        if (orders >= 40) throw new AppException("That's a lot of orders for one table. Please ask a waiter.", 429);
        if (!session.Approved && orders >= 3) throw new AppException("A waiter is on the way to confirm your table. You can order more once they have.", 409);
        var order = await Place(session, table, input, "guest");
        return (session, order, tokens.Issue(db.CompanyId, session.Id));
    }

    async Task<DineSession> NewSession(Guid tableId, bool approved)
    {
        var session = new DineSession { TableId = tableId, Number = await numbers.Next("TAB"), Approved = approved };
        db.DineSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    async Task<DineOrder> Place(DineSession session, DineTable table, DineOrderInput input, string placedBy)
    {
        if (input.Lines is not { Count: > 0 } || input.Lines.Count > 40) throw new AppException("Add between 1 and 40 dishes.");
        if (input.Lines.Any(l => l.Quantity is < 1 or > 50)) throw new AppException("Order between 1 and 50 of each.");
        var ids = input.Lines.Select(l => l.ItemId).Distinct().ToList();
        var items = await db.Items.Where(i => ids.Contains(i.Id) && i.Active && i.Use == ItemUse.Sale && i.Price > 0).ToDictionaryAsync(i => i.Id);
        var entries = await db.DineMenu.Where(e => ids.Contains(e.ItemId)).ToDictionaryAsync(e => e.ItemId);
        if (items.Count != ids.Count) throw new AppException("One of those dishes isn't on the menu any more. Refresh the menu.");
        var guest = placedBy == "guest";
        if (guest && ids.Any(id => entries.GetValueOrDefault(id) is not { Hidden: false })) throw new AppException("One of those dishes isn't on the menu any more. Refresh the menu.");
        var tracked = items.Values.Where(i => i.TrackStock).Select(i => i.Id).ToList();
        var onHand = await db.StockLevels.Where(l => tracked.Contains(l.ItemId)).GroupBy(l => l.ItemId).Select(g => new { g.Key, q = g.Sum(x => x.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.q);
        foreach (var l in input.Lines)
        {
            var item = items[l.ItemId];
            if (entries.GetValueOrDefault(l.ItemId)?.SoldOut == true) throw new AppException($"Sorry, {item.Name} is finished for today.", 409);
            if (item.TrackStock && onHand.GetValueOrDefault(item.Id) < input.Lines.Where(x => x.ItemId == item.Id).Sum(x => x.Quantity))
                throw new AppException($"Sorry, there isn't enough {item.Name} left for that.", 409);
        }

        var s = await Settings();
        var order = new DineOrder
        {
            SessionId = session.Id, TableId = table.Id, Number = await numbers.Next("K"),
            GuestName = Clean(input.GuestName, 60), Note = Clean(input.Note, 300), PlacedBy = guest ? "guest" : ctx.Actor,
            Status = !session.Approved ? DineOrderStatus.AwaitingApproval : s.PayMode == DinePayMode.PayFirst && guest ? DineOrderStatus.AwaitingPayment : DineOrderStatus.New,
        };
        foreach (var l in input.Lines)
        {
            var item = items[l.ItemId];
            order.Lines.Add(new DineOrderLine
            {
                ItemId = item.Id, Name = item.Name, Quantity = l.Quantity, UnitPrice = item.Price,
                Options = Clean(l.Options, 200), Note = Clean(l.Note, 200), Station = entries.GetValueOrDefault(item.Id)?.Station ?? StationsOf(s)[0],
            });
        }
        order.Total = order.Lines.Sum(l => l.UnitPrice * l.Quantity);
        if (order.Status == DineOrderStatus.New) order.SentAt = DateTimeOffset.UtcNow;
        db.DineOrders.Add(order);
        await db.SaveChangesAsync();
        // Pay-first: the order's own bill, paid before the kitchen sees it.
        if (order.Status == DineOrderStatus.AwaitingPayment) await Bill(session, [order]);
        return order;
    }

    public async Task<object> Call(DineSession session, string kind)
    {
        if (kind == "bill") session.BillRequestedAt ??= DateTimeOffset.UtcNow;
        else session.CallWaiterAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    /// <summary>The guest pays from their phone: the bill so far, by M-Pesa prompt when the business has it set up.</summary>
    public async Task<object> GuestPay(DineSession session, DinePayInput input, string baseUrl)
    {
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        var phone = Phones.ToMsisdn(input.Phone) ?? throw new AppException("Enter your M-Pesa number (e.g. 0712 345 678).");
        if (session.Phone.Length == 0) session.Phone = phone;
        var invoice = await BillDue(session);
        var prompt = c.StkShortcode.Length > 0 && c.StkConsumerKey.Length > 0 && c.StkPasskey.Length > 0;
        if (!prompt)
        {
            session.BillRequestedAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return new { prompt = false, invoice.Number, due = invoice.Total - invoice.Paid, paybill = c.MpesaShortcode };
        }
        var r = await stk.Request(invoice.Id, new StkRequestInput(phone, null), baseUrl);
        return new { prompt = true, invoice.Number, due = r.Amount };
    }

    public async Task<object> Rate(DineSession session, DineRateInput input)
    {
        if (input.Stars is < 1 or > 5) throw new AppException("Choose 1 to 5 stars.");
        (session.Rating, session.Feedback) = (input.Stars, Clean(input.Comment, 500));
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    // ------------------------------------------------------------------------------------------------ the bill

    async Task<Customer> CustomerFor(DineSession session)
    {
        if (Phones.ToMsisdn(session.Phone) is { } phone && await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone) is { } known) return known;
        if (session.Phone.Length > 0)
        {
            var c = new Customer { Number = await numbers.Next("C"), Name = "Dine-in guest", Phone = session.Phone, AcquisitionChannel = Channel.WalkIn };
            db.Customers.Add(c);
            await db.SaveChangesAsync();
            return c;
        }
        const string walkIn = "Dine-in guests";
        var guests = await db.Customers.FirstOrDefaultAsync(c => c.Name == walkIn);
        if (guests is null)
        {
            guests = new Customer { Number = await numbers.Next("C"), Name = walkIn, AcquisitionChannel = Channel.WalkIn };
            db.Customers.Add(guests);
            await db.SaveChangesAsync();
        }
        return guests;
    }

    /// <summary>Bills the given orders as one invoice: issued, so stock goes down, the ledger has the sale and eTIMS signs it.</summary>
    async Task<SalesDoc> Bill(DineSession session, List<DineOrder> orders)
    {
        var table = await db.DineTables.SingleAsync(t => t.Id == session.TableId);
        var customer = await CustomerFor(session);
        var lines = orders.SelectMany(o => o.Lines).Select(l => new LineInput(l.ItemId, l.Options.Length > 0 ? $"{l.Name} ({l.Options})" : l.Name, l.Quantity, null, null)).ToList();
        var doc = await sales.Create(new SalesDocInput(SalesDocKind.Invoice, customer.Id, Clock.Today, 0,
            $"Kabuor Dine · {table.Name} · {session.Number} · orders {string.Join(", ", orders.Select(o => o.Number))}", lines));
        doc = await sales.Issue(doc.Id, overLimit: true); // paid at the table, so it adds no debt
        foreach (var o in orders) o.InvoiceId = doc.Id;
        await db.SaveChangesAsync();
        return doc;
    }

    /// <summary>What the table still owes, as one invoice: the bill already open if nothing new was ordered, else a new one for what isn't billed yet.</summary>
    async Task<SalesDoc> BillDue(DineSession session)
    {
        await Sync(session);
        if (session.Status != DineSessionStatus.Open) throw new AppException("This table's bill is already paid. Thank you!", 409);
        var orders = await db.DineOrders.Include(o => o.Lines).Where(o => o.SessionId == session.Id).ToListAsync();
        if (orders.Any(o => o.Status == DineOrderStatus.AwaitingApproval)) throw new AppException("A waiter still has to confirm your table before you can pay.", 409);
        var unbilled = orders.Where(o => o.InvoiceId == null && Billable.Contains(o.Status)).ToList();
        if (unbilled.Count > 0)
        {
            // Bills issued earlier but not paid (a pay-first order abandoned, a part bill) are settled first.
            var open = await OpenInvoices(orders);
            if (open.Count > 0) throw new AppException($"Bill {open[0].Number} is still open. Pay that one first, or ask a waiter.", 409);
            return await Bill(session, unbilled);
        }
        var due = await OpenInvoices(orders);
        return due.FirstOrDefault() ?? throw new AppException("There's nothing to pay yet.", 409);
    }

    async Task<List<SalesDoc>> OpenInvoices(List<DineOrder> orders)
    {
        var ids = orders.Where(o => o.InvoiceId != null && o.Status != DineOrderStatus.Cancelled).Select(o => o.InvoiceId!.Value).Distinct().ToList();
        return (await db.SalesDocs.Where(d => ids.Contains(d.Id)).ToListAsync()).Where(d => d.Status != SalesDocStatus.Void && d.Paid < d.Total).OrderBy(d => d.Number).ToList();
    }

    /// <summary>Brings a visit up to date with its bills: a paid pay-first order goes to the kitchen, and a table with
    /// everything paid is marked paid.</summary>
    async Task Sync(DineSession session) => await SyncAll([session]);

    /// <summary><see cref="Sync"/> for many visits at once (the floor): their orders and bills in two queries, one save.
    /// Returns the orders and each bill's amount paid, for the caller to reuse.</summary>
    async Task<(List<DineOrder> Orders, Dictionary<Guid, (long Total, long Paid)> Bills)> SyncAll(List<DineSession> sessions)
    {
        var ids = sessions.Select(x => x.Id).ToList();
        var orders = ids.Count == 0 ? [] : await db.DineOrders.Where(o => ids.Contains(o.SessionId)).ToListAsync();
        var invoiceIds = orders.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value).Distinct().ToList();
        var bills = invoiceIds.Count == 0 ? [] : await db.SalesDocs.Where(d => invoiceIds.Contains(d.Id)).Select(d => new { d.Id, d.Total, d.Paid }).ToDictionaryAsync(d => d.Id, d => (d.Total, d.Paid));
        bool Paid(Guid? id) => id is { } x && bills.TryGetValue(x, out var d) && d.Total > 0 && d.Paid >= d.Total;
        var changed = false;
        foreach (var session in sessions.Where(x => x.Status == DineSessionStatus.Open))
        {
            var mine = orders.Where(o => o.SessionId == session.Id).ToList();
            foreach (var o in mine.Where(o => o.Status == DineOrderStatus.AwaitingPayment && Paid(o.InvoiceId)))
                (o.Status, o.SentAt, changed) = (DineOrderStatus.New, DateTimeOffset.UtcNow, true);
            var live = mine.Where(o => o.Status != DineOrderStatus.Cancelled).ToList();
            if (live.Count > 0 && live.All(o => Paid(o.InvoiceId)))
                (session.Status, session.BillRequestedAt, changed) = (DineSessionStatus.Paid, null, true);
        }
        if (changed) await db.SaveChangesAsync();
        return (orders, bills);
    }

    // ------------------------------------------------------------------------------------------------ the floor

    /// <summary>Every table at a glance: who's waiting to be confirmed, who's calling, what's ready to carry out, what's owed.</summary>
    public async Task<object> Floor()
    {
        var s = await Settings();
        var tables = await db.DineTables.Where(t => t.Active).ToListAsync();
        var sessions = await db.DineSessions.Where(x => x.Status != DineSessionStatus.Closed).ToListAsync();
        var (all, bills) = await SyncAll(sessions);
        var orders = all.Where(o => o.Status != DineOrderStatus.Cancelled).ToList();
        var paid = bills.ToDictionary(b => b.Key, b => Math.Min(b.Value.Paid, b.Value.Total));
        var now = DateTimeOffset.UtcNow;
        return new
        {
            payMode = s.PayMode.ToString(), open = s.Open,
            tables = tables.OrderBy(t => t.Area).ThenBy(t => t.Name.Length).ThenBy(t => t.Name).Select(t =>
            {
                var x = sessions.Where(v => v.TableId == t.Id).OrderByDescending(v => v.OpenedAt).FirstOrDefault();
                var mine = x is null ? [] : orders.Where(o => o.SessionId == x.Id).ToList();
                var total = mine.Sum(o => o.Total);
                var settled = mine.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value).Distinct().Sum(id => paid.GetValueOrDefault(id));
                return new
                {
                    t.Id, t.Name, t.Area, t.Seats,
                    session = x is null ? null : new
                    {
                        x.Id, x.Number, status = x.Status.ToString(), x.Approved, x.OpenedAt, minutes = (int)(now - x.OpenedAt).TotalMinutes,
                        callWaiter = x.CallWaiterAt, askedForBill = x.BillRequestedAt,
                        awaitingApproval = mine.Count(o => o.Status == DineOrderStatus.AwaitingApproval),
                        awaitingPayment = mine.Count(o => o.Status == DineOrderStatus.AwaitingPayment),
                        inKitchen = mine.Count(o => o.Status is DineOrderStatus.New or DineOrderStatus.Preparing),
                        ready = mine.Count(o => o.Status == DineOrderStatus.Ready),
                        total, paid = settled, due = Math.Max(0, total - settled), x.Rating,
                    },
                };
            }),
        };
    }

    public async Task<DineSession> Session(Guid id) => await db.DineSessions.SingleOrDefaultAsync(x => x.Id == id) ?? throw new AppException("Not found.", 404);

    public async Task<object> SessionDetail(Guid id)
    {
        var x = await Session(id);
        var table = await db.DineTables.SingleAsync(t => t.Id == x.TableId);
        return new { table = new { table.Id, table.Name, table.Area }, visit = await Visit(x), x.Phone, x.Feedback, x.OpenedAt, x.ClosedAt };
    }

    /// <summary>A waiter opens a table (seating a party, or for orders they'll take by hand).</summary>
    public async Task<object> Seat(Guid tableId)
    {
        var table = await db.DineTables.SingleOrDefaultAsync(t => t.Id == tableId && t.Active) ?? throw new AppException("Not found.", 404);
        var x = await OpenSession(table.Id);
        if (x is { Status: DineSessionStatus.Paid }) throw new AppException("That table has paid. Clear it first.", 409);
        x ??= await NewSession(table.Id, approved: true);
        if (!x.Approved) await Approve(x.Id);
        return new { x.Id, x.Number };
    }

    /// <summary>The waiter takes an order by hand (a guest without a phone, or one who'd rather tell someone).</summary>
    public async Task<object> StaffOrder(Guid tableId, DineOrderInput input)
    {
        var table = await db.DineTables.SingleOrDefaultAsync(t => t.Id == tableId && t.Active) ?? throw new AppException("Not found.", 404);
        var x = await OpenSession(table.Id);
        if (x is { Status: DineSessionStatus.Paid }) throw new AppException("That table has paid. Clear it first.", 409);
        x ??= await NewSession(table.Id, approved: true);
        if (!x.Approved) await Approve(x.Id);
        var o = await Place(x, table, input, ctx.Actor);
        return new { o.Id, o.Number, sessionId = x.Id };
    }

    /// <summary>A waiter confirms someone is really at the table: its orders go to the kitchen (or, pay-first, to be paid).</summary>
    public async Task<object> Approve(Guid sessionId)
    {
        var x = await Session(sessionId);
        x.Approved = true;
        var s = await Settings();
        var waiting = await db.DineOrders.Include(o => o.Lines).Where(o => o.SessionId == x.Id && o.Status == DineOrderStatus.AwaitingApproval).ToListAsync();
        var payFirst = s.PayMode == DinePayMode.PayFirst;
        foreach (var o in waiting)
        {
            o.Status = payFirst ? DineOrderStatus.AwaitingPayment : DineOrderStatus.New;
            if (!payFirst) o.SentAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
        if (payFirst && waiting.Count > 0) await Bill(x, waiting);
        return new { approved = waiting.Count };
    }

    /// <summary>The waiter has answered the table: the call (and the request for the bill) is cleared.</summary>
    public async Task<object> Answer(Guid sessionId)
    {
        var x = await Session(sessionId);
        (x.CallWaiterAt, x.BillRequestedAt) = (null, null);
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    /// <summary>Paid at the table to the waiter (cash, card, an M-Pesa code read off the guest's phone): the bill is
    /// made if needed and the payment recorded against it, all or nothing.</summary>
    public async Task<object> TakePayment(Guid sessionId, DineTillInput input)
    {
        var x = await Session(sessionId);
        if (!Enum.TryParse<PaymentMethod>(input.Method, true, out var method)) throw new AppException("Choose cash, M-Pesa or card.");
        var reference = Clean(input.Reference, 40).ToUpperInvariant();
        if (method == PaymentMethod.Mpesa && reference.Length < 8) throw new AppException("Enter the M-Pesa code from the guest's message (e.g. SFG3H5KLM2).");
        if (Phones.ToMsisdn(input.Phone) is { } phone && x.Phone.Length == 0) x.Phone = phone;
        await using var tx = await db.Database.BeginTransactionAsync();
        var invoice = await BillDue(x);
        var due = invoice.Total - invoice.Paid;
        // An M-Pesa payment Safaricom already told us about (paybill confirmation): use it rather than record it twice.
        var known = method == PaymentMethod.Mpesa ? await db.Payments.SingleOrDefaultAsync(p => p.Reference == reference) : null;
        if (known is not null)
        {
            if (known.Unallocated < due) throw new AppException($"M-Pesa {known.Reference} has {Money.Ksh(known.Unallocated)} left to use; the bill is {Money.Ksh(due)}.");
            await payments.Allocate(known.Id, [new AllocationInput(invoice.Id, due)], $"Dine: {ctx.Actor}");
        }
        else
        {
            await payments.Record(new PaymentInput(method, due, DateTimeOffset.UtcNow, reference.Length > 0 ? reference : $"{invoice.Number}-DINE",
                x.Phone, "", invoice.Number, invoice.CustomerId, invoice.Id), ctx.Actor, PaymentSource.Till);
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        await Sync(x);
        return new { invoice.Id, invoice.Number, paid = due, status = x.Status.ToString(), more = x.Status == DineSessionStatus.Open };
    }

    /// <summary>The bill as one invoice, for printing at the table before paying.</summary>
    public async Task<object> PrintBill(Guid sessionId)
    {
        var x = await Session(sessionId);
        var invoice = await BillDue(x);
        return new { invoice.Id, invoice.Number, due = invoice.Total - invoice.Paid };
    }

    /// <summary>The party has left: the table is free for the next guests.</summary>
    public async Task<object> Clear(Guid sessionId, bool force)
    {
        var x = await Session(sessionId);
        await Sync(x);
        var orders = await db.DineOrders.Where(o => o.SessionId == x.Id && o.Status != DineOrderStatus.Cancelled).ToListAsync();
        if (x.Status == DineSessionStatus.Open && orders.Count > 0)
        {
            if (!force) throw new AppException("This table hasn't paid everything yet. Take the payment first, or clear it anyway as unpaid.", 409);
            if (!ctx.Can(Policies.Finance) && ctx.Role != Role.Owner) throw new AppException("Only the owner or an accountant can clear a table that hasn't paid.", 403);
        }
        (x.Status, x.ClosedAt, x.CallWaiterAt, x.BillRequestedAt) = (DineSessionStatus.Closed, DateTimeOffset.UtcNow, null, null);
        await db.SaveChangesAsync();
        return new { ok = true };
    }

    // ------------------------------------------------------------------------------------------------ the kitchen

    /// <summary>A station's screen: orders with dishes for it, oldest first, with how long each has waited.</summary>
    public async Task<object> KitchenBoard(string? station)
    {
        var s = await Settings();
        var stations = StationsOf(s);
        var st = stations.FirstOrDefault(x => x.Equals(station ?? "", StringComparison.OrdinalIgnoreCase));
        var since = DateTimeOffset.UtcNow.AddMinutes(-20);
        var orders = (await db.DineOrders.Include(o => o.Lines).Where(o => Kitchen.Contains(o.Status)).ToListAsync())
            .Where(o => o.Status != DineOrderStatus.Ready || o.ReadyAt > since).ToList();
        var tables = await db.DineTables.IgnoreQueryFilters().Where(t => t.CompanyId == db.CompanyId).ToDictionaryAsync(t => t.Id, t => t.Name);
        var now = DateTimeOffset.UtcNow;
        var shown = orders
            .Select(o => (o, lines: o.Lines.Where(l => st is null || l.Station.Equals(st, StringComparison.OrdinalIgnoreCase)).ToList()))
            .Where(x => x.lines.Count > 0)
            .OrderBy(x => x.o.Status == DineOrderStatus.Ready).ThenBy(x => x.o.SentAt ?? x.o.PlacedAt)
            .Select(x => new
            {
                x.o.Id, x.o.Number, table = tables.GetValueOrDefault(x.o.TableId, "?"), status = x.o.Status.ToString(), x.o.GuestName, x.o.Note,
                sentAt = x.o.SentAt ?? x.o.PlacedAt, minutes = (int)(now - (x.o.SentAt ?? x.o.PlacedAt)).TotalMinutes,
                stationReady = x.lines.All(l => l.Ready),
                lines = x.lines.Select(l => new { l.Id, l.Name, l.Quantity, l.Options, l.Note, l.Station, l.Ready }),
                others = x.o.Lines.Where(l => !x.lines.Contains(l)).Select(l => new { l.Station, l.Ready }).GroupBy(l => l.Station).Select(g => new { station = g.Key, ready = g.All(l => l.Ready) }),
            }).ToList();
        return new { stations, station = st, orders = shown, waiting = shown.Count(o => o.status != "Ready") };
    }

    public async Task<object> SetStatus(Guid orderId, string status)
    {
        var o = await db.DineOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == orderId) ?? throw new AppException("Not found.", 404);
        if (!Enum.TryParse<DineOrderStatus>(status, true, out var to) || to is DineOrderStatus.AwaitingApproval or DineOrderStatus.AwaitingPayment)
            throw new AppException("Choose preparing, ready, served or cancelled.");
        if (o.Status is DineOrderStatus.Cancelled) throw new AppException("That order was cancelled.", 409);
        if (o.Status is DineOrderStatus.AwaitingApproval or DineOrderStatus.AwaitingPayment && to != DineOrderStatus.Cancelled)
            throw new AppException(o.Status == DineOrderStatus.AwaitingApproval ? "Confirm the table first." : "That order isn't paid yet.", 409);
        if (to == DineOrderStatus.Cancelled)
        {
            if (o.InvoiceId is { } inv)
            {
                // Billed already: only an unpaid bill for this order alone can go with it (voided, so stock and the books come back).
                var doc = await db.SalesDocs.SingleAsync(d => d.Id == inv);
                var shared = await db.DineOrders.AnyAsync(x => x.InvoiceId == inv && x.Id != o.Id && x.Status != DineOrderStatus.Cancelled);
                if (doc.Paid > 0 || shared) throw new AppException("That order is on a bill that's paid or covers other orders. Refund or credit it from the invoice instead.", 409);
                if (!ctx.Can(Policies.Finance) && ctx.Role != Role.Owner && !ctx.Can(Policies.Sell)) throw new AppException("Ask a supervisor to cancel a billed order.", 403);
                await sales.Void(inv);
            }
        }
        o.Status = to;
        if (to == DineOrderStatus.Ready) { o.ReadyAt ??= DateTimeOffset.UtcNow; foreach (var l in o.Lines) l.Ready = true; }
        if (to == DineOrderStatus.Served) { o.ReadyAt ??= DateTimeOffset.UtcNow; o.ServedAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync();
        return new { o.Id, status = o.Status.ToString() };
    }

    /// <summary>One station's dishes are done (the bar's drinks): the order is ready once every station's are.</summary>
    public async Task<object> StationReady(Guid orderId, string station)
    {
        var o = await db.DineOrders.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == orderId) ?? throw new AppException("Not found.", 404);
        if (o.Status is not (DineOrderStatus.New or DineOrderStatus.Preparing)) throw new AppException("That order isn't being made.", 409);
        foreach (var l in o.Lines.Where(l => l.Station.Equals(station, StringComparison.OrdinalIgnoreCase))) l.Ready = true;
        if (o.Lines.All(l => l.Ready)) (o.Status, o.ReadyAt) = (DineOrderStatus.Ready, DateTimeOffset.UtcNow);
        else o.Status = DineOrderStatus.Preparing;
        await db.SaveChangesAsync();
        return new { o.Id, status = o.Status.ToString() };
    }

    // ------------------------------------------------------------------------------------------------ settings and the day

    public async Task<object> SettingsView()
    {
        var s = await Settings();
        var c = await db.Companies.SingleAsync(x => x.Id == db.CompanyId);
        return new
        {
            payMode = s.PayMode.ToString(), s.ApproveFirstOrder, stations = StationsOf(s), s.Welcome, s.Open,
            mpesaPrompt = c.StkShortcode.Length > 0 && c.StkConsumerKey.Length > 0 && c.StkPasskey.Length > 0, paybill = c.MpesaShortcode,
            product = c.Product,
        };
    }

    public async Task<object> SaveSettings(DineSettingsInput input)
    {
        var s = await Settings();
        if (input.PayMode is not null) s.PayMode = Enum.TryParse<DinePayMode>(input.PayMode, true, out var m) ? m : throw new AppException("Choose tab or pay-first.");
        if (input.ApproveFirstOrder is { } a) s.ApproveFirstOrder = a;
        if (input.Stations is not null)
        {
            var list = input.Stations.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Length > 30 ? x[..30] : x).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            if (list.Count == 0) throw new AppException("Keep at least one kitchen screen.");
            s.Stations = string.Join(",", list);
            // Dishes on a screen that's gone move to the first one.
            foreach (var e in await db.DineMenu.ToListAsync())
                if (!list.Contains(e.Station, StringComparer.OrdinalIgnoreCase)) e.Station = list[0];
        }
        if (input.Welcome is not null) s.Welcome = Clean(input.Welcome, 300);
        if (input.Open is { } open) s.Open = open;
        await db.SaveChangesAsync();
        return await SettingsView();
    }

    /// <summary>The day (or week, or month) in the restaurant: takings, covers, the best sellers, how fast the kitchen was, and what guests said.</summary>
    public async Task<object> Report(int days)
    {
        days = days is 7 or 30 ? days : 1;
        var from = DateTimeOffset.UtcNow.AddHours(3).Date.AddDays(1 - days);
        var since = new DateTimeOffset(from, TimeSpan.FromHours(3));
        var orders = (await db.DineOrders.Include(o => o.Lines).Where(o => o.Status != DineOrderStatus.Cancelled).ToListAsync()).Where(o => o.PlacedAt >= since).ToList();
        var cancelled = (await db.DineOrders.Where(o => o.Status == DineOrderStatus.Cancelled).Select(o => o.PlacedAt).ToListAsync()).Count(t => t >= since);
        var sessions = (await db.DineSessions.ToListAsync()).Where(x => x.OpenedAt >= since).ToList();
        var invoiceIds = orders.Where(o => o.InvoiceId != null).Select(o => o.InvoiceId!.Value).Distinct().ToList();
        var invoices = await db.SalesDocs.Where(d => invoiceIds.Contains(d.Id) && d.Status != SalesDocStatus.Void).ToListAsync();
        var prep = orders.Where(o => o.SentAt != null && o.ReadyAt != null).Select(o => (o.ReadyAt!.Value - o.SentAt!.Value).TotalMinutes).ToList();
        var rated = sessions.Where(x => x.Rating > 0).ToList();
        var tables = await db.DineTables.IgnoreQueryFilters().Where(t => t.CompanyId == db.CompanyId).ToDictionaryAsync(t => t.Id, t => t.Name);
        return new
        {
            days, from = DateOnly.FromDateTime(from),
            sales = orders.Sum(o => o.Total), paid = invoices.Sum(d => Math.Min(d.Paid, d.Total)),
            orders = orders.Count, cancelled, visits = sessions.Count,
            averageBill = sessions.Count == 0 ? 0 : orders.Sum(o => o.Total) / Math.Max(1, sessions.Count(x => orders.Any(o => o.SessionId == x.Id))),
            prepMinutes = prep.Count == 0 ? (double?)null : Math.Round(prep.Average(), 1),
            slowest = orders.Where(o => o.SentAt != null && o.ReadyAt != null).OrderByDescending(o => o.ReadyAt!.Value - o.SentAt!.Value).Take(3)
                .Select(o => new { o.Number, table = tables.GetValueOrDefault(o.TableId, "?"), minutes = Math.Round((o.ReadyAt!.Value - o.SentAt!.Value).TotalMinutes) }),
            fromGuests = orders.Count(o => o.PlacedBy == "guest"),
            rating = rated.Count == 0 ? (double?)null : Math.Round(rated.Average(x => x.Rating), 1), ratings = rated.Count,
            comments = rated.Where(x => x.Feedback.Length > 0).OrderByDescending(x => x.OpenedAt).Take(8).Select(x => new { x.Rating, x.Feedback, table = tables.GetValueOrDefault(x.TableId, "?"), x.OpenedAt }),
            top = orders.SelectMany(o => o.Lines).GroupBy(l => l.Name).Select(g => new { name = g.Key, quantity = g.Sum(l => l.Quantity), sales = g.Sum(l => l.UnitPrice * l.Quantity) })
                .OrderByDescending(x => x.quantity).Take(10),
            byHour = orders.GroupBy(o => o.PlacedAt.ToOffset(TimeSpan.FromHours(3)).Hour).OrderBy(g => g.Key).Select(g => new { hour = g.Key, orders = g.Count(), sales = g.Sum(o => o.Total) }),
            byStation = orders.SelectMany(o => o.Lines).GroupBy(l => l.Station).Select(g => new { station = g.Key, quantity = g.Sum(l => l.Quantity), sales = g.Sum(l => l.UnitPrice * l.Quantity) }),
        };
    }
}
