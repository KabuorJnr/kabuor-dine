// Operations API: departments, maintenance (faults, work orders, plans, schedule, insights), procurement (requests,
// tenders), stock takes and the money side of operations. Any staff login can report a fault or ask for something;
// planning and doing the work needs the maintenance role; approving and buying needs procurement; issuing from the
// store needs the stock role; budgets need finance. Bidders reach a tender through its link without signing in.
using Kabuor.Erp.Auth;
using Kabuor.Erp.Common;
using Kabuor.Erp.Data;
using Kabuor.Erp.Domain;
using Kabuor.Erp.Operations;
using Kabuor.Erp.Platform;
using Microsoft.EntityFrameworkCore;

namespace Kabuor.Erp;

public static class OperationsEndpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Policies.Read);

        // People to choose from (a custodian, a technician, a department head): names only, never pay.
        api.MapGet("/directory/employees", async (ErpDb db) =>
        {
            var today = Clock.Today;
            return await db.Employees.Where(e => e.EndDate == null || e.EndDate >= today).OrderBy(e => e.Name)
                .Select(e => new { e.Id, e.Number, e.Name, e.JobTitle, e.Department }).ToListAsync();
        });
        // Suppliers to choose from (a contractor for a job): names only, never what's owed to them.
        api.MapGet("/directory/suppliers", async (ErpDb db) => await db.Suppliers.OrderBy(s => s.Name).Select(s => new { s.Id, s.Number, s.Name, s.Phone }).ToListAsync());

        var departments = api.MapGroup("/departments");
        departments.MapGet("", async (DepartmentService s) => { await s.SeedFromEmployees(); return await s.List(); });
        departments.MapPost("", async (DepartmentInput input, DepartmentService s) => Results.Created("", await s.Save(null, input))).RequireAuthorization(Policies.Finance);
        departments.MapPut("/{id:guid}", (Guid id, DepartmentInput input, DepartmentService s) => s.Save(id, input)).RequireAuthorization(Policies.Finance);

        // ---------- maintenance ----------
        var m = api.MapGroup("/maintenance");
        m.MapGet("/codes", () => new { modes = MaintenanceService.Modes, causes = MaintenanceService.Causes, remedies = MaintenanceService.Remedies });
        m.MapPost("/faults", async (FaultInput input, MaintenanceService s) => Results.Created("", await s.Report(input)));
        m.MapGet("/faults", (string? status, MaintenanceService s) => s.Faults(status)).RequireAuthorization(Policies.MaintainView);
        m.MapPost("/faults/{id:guid}/accept", (Guid id, WorkOrderInput? input, MaintenanceService s) => s.Accept(id, input)).RequireAuthorization(Policies.Maintain);
        m.MapPost("/faults/{id:guid}/reject", (Guid id, ReasonInput input, MaintenanceService s) => s.Reject(id, input.Reason)).RequireAuthorization(Policies.Maintain);

        var wo = m.MapGroup("/work-orders").RequireAuthorization(Policies.MaintainView);
        wo.MapGet("", (string? view, string? q, Guid? assetId, MaintenanceService s) => s.List(view, q, assetId));
        wo.MapPost("", async (WorkOrderInput input, MaintenanceService s) => Results.Created("", await s.Create(input))).RequireAuthorization(Policies.Maintain);
        wo.MapGet("/{id:guid}", (Guid id, MaintenanceService s) => s.Get(id));
        wo.MapPut("/{id:guid}", (Guid id, WorkOrderInput input, MaintenanceService s) => s.Update(id, input)).RequireAuthorization(Policies.Maintain);
        // Closing financially is also open to finance; everything else needs the maintenance role (checked here).
        wo.MapPost("/{id:guid}/actions", (Guid id, WorkOrderAction input, MaintenanceService s, RequestContext ctx) =>
            !string.Equals(input.Kind, "close", StringComparison.OrdinalIgnoreCase) && !ctx.Can(Policies.Maintain) ? throw new AppException("Only the maintenance team can do that.", 403) : s.Act(id, input));
        wo.MapPost("/{id:guid}/parts", (Guid id, PartInput input, MaintenanceService s) => s.AddPart(id, input)).RequireAuthorization(Policies.Maintain);
        wo.MapDelete("/{id:guid}/parts/{partId:guid}", (Guid id, Guid partId, MaintenanceService s) => s.RemovePart(id, partId)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/parts/{partId:guid}/issue", (Guid id, Guid partId, IssueInput input, MaintenanceService s) => s.Issue(id, partId, input)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/parts/{partId:guid}/return", (Guid id, Guid partId, IssueInput input, MaintenanceService s) => s.ReturnPart(id, partId, input)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/labour", (Guid id, LabourInput input, MaintenanceService s) => s.AddLabour(id, input)).RequireAuthorization(Policies.Maintain);
        wo.MapDelete("/{id:guid}/labour/{labourId:guid}", (Guid id, Guid labourId, MaintenanceService s) => s.RemoveLabour(id, labourId)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/costs", (Guid id, DirectCostInput input, MaintenanceService s) => s.AddCost(id, input));
        wo.MapPost("/{id:guid}/order", (Guid id, OrderPartsInput input, MaintenanceService s) => s.OrderParts(id, input)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/tools", (Guid id, ToolInput input, MaintenanceService s) => s.AddTool(id, input)).RequireAuthorization(Policies.Maintain);
        wo.MapDelete("/{id:guid}/tools/{toolId:guid}", (Guid id, Guid toolId, MaintenanceService s) => s.RemoveTool(id, toolId)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/tools/{toolId:guid}/out", (Guid id, Guid toolId, ToolMoveInput input, MaintenanceService s) => s.CheckOutTool(id, toolId, input)).RequireAuthorization(Policies.Maintain);
        wo.MapPost("/{id:guid}/tools/{toolId:guid}/back", (Guid id, Guid toolId, ToolMoveInput input, MaintenanceService s) => s.ReturnTool(id, toolId, input)).RequireAuthorization(Policies.Maintain);

        m.MapPost("/meters/{assetId:guid}", (Guid assetId, MeterInput input, MaintenanceService s) => s.RecordMeter(assetId, input));
        m.MapGet("/meters/{assetId:guid}", (Guid assetId, MaintenanceService s) => s.MeterHistory(assetId)).RequireAuthorization(Policies.MaintainView);
        m.MapGet("/schedule", (DateOnly? from, int? days, MaintenanceService s) => s.Schedule(from, days ?? 14)).RequireAuthorization(Policies.MaintainView);
        m.MapGet("/plans", (MaintenanceService s) => s.Plans()).RequireAuthorization(Policies.MaintainView);
        m.MapPost("/plans", async (PlanInput input, MaintenanceService s) => Results.Created("", await s.SavePlan(null, input))).RequireAuthorization(Policies.Maintain);
        m.MapPut("/plans/{id:guid}", (Guid id, PlanInput input, MaintenanceService s) => s.SavePlan(id, input)).RequireAuthorization(Policies.Maintain);
        m.MapPost("/plans/generate", (MaintenanceService s) => s.GenerateDue()).RequireAuthorization(Policies.Maintain);
        m.MapGet("/insights", (int? days, MaintenanceService s) => s.Insights(days ?? 90)).RequireAuthorization(Policies.MaintainView);

        // ---------- requests from departments ----------
        var req = api.MapGroup("/requisitions");
        req.MapGet("", (string? view, RequisitionService s) => s.List(view));
        req.MapPost("", async (RequisitionInput input, RequisitionService s) => Results.Created("", await s.Create(input)));
        req.MapGet("/{id:guid}", (Guid id, RequisitionService s) => s.Get(id));
        req.MapPut("/{id:guid}", (Guid id, RequisitionInput input, RequisitionService s) => s.Update(id, input));
        req.MapPost("/{id:guid}/submit", (Guid id, RequisitionService s) => s.Submit(id));
        req.MapPost("/{id:guid}/cancel", (Guid id, RequisitionService s) => s.Cancel(id));
        req.MapPost("/{id:guid}/approve", (Guid id, RequisitionService s) => s.Approve(id)).RequireAuthorization(Policies.Buy);
        req.MapPost("/{id:guid}/sign-off", (Guid id, ReasonInput input, RequisitionService s) => s.SignOff(id, input.Reason)).RequireAuthorization(Policies.Finance);
        req.MapPost("/{id:guid}/reject", (Guid id, ReasonInput input, RequisitionService s, RequestContext ctx) =>
        {
            if (!ctx.Can(Policies.Buy) && !ctx.Can(Policies.Finance)) throw new AppException("Only procurement or finance can turn a request down.", 403);
            return s.Reject(id, input.Reason);
        });

        // ---------- cash advances (imprest) ----------
        var adv = api.MapGroup("/advances");
        adv.MapGet("", (string? view, CashAdvanceService s, RequestContext ctx) =>
        {
            if (!ctx.Can(Policies.Finance) && !ctx.Can(Policies.People) && !ctx.Can(Policies.Reports)) throw new AppException("Only finance and HR see everyone's advances.", 403);
            return s.List(view);
        });
        adv.MapGet("/{id:guid}", (Guid id, CashAdvanceService s, RequestContext ctx) =>
        {
            if (!ctx.Can(Policies.Finance) && !ctx.Can(Policies.People) && !ctx.Can(Policies.Reports)) throw new AppException("Not found.", 404);
            return s.Get(id);
        });
        adv.MapPost("", async (AdvanceInput input, CashAdvanceService s) => Results.Created("", await s.Request(input)));
        adv.MapPost("/{id:guid}/sign-off", (Guid id, ReasonInput input, CashAdvanceService s) => s.SignOff(id, input.Reason)).RequireAuthorization(Policies.Finance);
        adv.MapPost("/{id:guid}/reject", (Guid id, ReasonInput input, CashAdvanceService s) => s.Reject(id, input.Reason)).RequireAuthorization(Policies.Finance);
        adv.MapPost("/{id:guid}/pay", (Guid id, AdvancePayInput input, CashAdvanceService s) => s.Pay(id, input)).RequireAuthorization(Policies.Finance);
        adv.MapPost("/{id:guid}/account", (Guid id, AdvanceAccountInput input, CashAdvanceService s) => s.Account(id, input)).RequireAuthorization(Policies.Finance);
        adv.MapPost("/{id:guid}/cancel", (Guid id, CashAdvanceService s) => s.Cancel(id)).RequireAuthorization(Policies.Finance);

        // ---------- budgets (prepared by departments, approved by finance) and the approvals register ----------
        var budgets = api.MapGroup("/budgets");
        budgets.MapGet("", (int? year, BudgetService s) => s.Year(year ?? Clock.Today.Year));
        budgets.MapGet("/{id:guid}", (Guid id, BudgetService s) => s.Get(id));
        budgets.MapPost("", async (BudgetInput input, BudgetService s) => Results.Created("", await s.Create(input)));
        budgets.MapPut("/{id:guid}", (Guid id, BudgetInput input, BudgetService s) => s.Update(id, input));
        budgets.MapPost("/{id:guid}/submit", (Guid id, BudgetService s) => s.Submit(id));
        budgets.MapPost("/{id:guid}/approve", (Guid id, ReasonInput input, BudgetService s) => s.Decide(id, true, input.Reason)).RequireAuthorization(Policies.Finance);
        budgets.MapPost("/{id:guid}/reject", (Guid id, ReasonInput input, BudgetService s) => s.Decide(id, false, input.Reason)).RequireAuthorization(Policies.Finance);
        var register = api.MapGroup("/approvals").RequireAuthorization(Policies.Reports);
        register.MapGet("", (string? subject, Guid? subjectId, string? q, int? take, ApprovalRegister s) => s.List(subject, subjectId, q, take ?? 200));
        register.MapGet("/verify", (ApprovalRegister s) => s.Verify());
        register.MapPost("/check", (FingerprintInput input, ApprovalRegister s) => s.CheckKept(input.Fingerprint));
        register.MapGet("/statement", async (ApprovalRegister s) => Results.Text(await s.Statement(), "text/plain; charset=utf-8"));
        budgets.MapGet("/transfers", (BudgetService s) => s.Transfers());
        budgets.MapPost("/transfers", (TransferInput input, BudgetService s) => s.RequestTransfer(input));
        budgets.MapPost("/transfers/{id:guid}/approve", (Guid id, ReasonInput input, BudgetService s) => s.DecideTransfer(id, true, input.Reason)).RequireAuthorization(Policies.Finance);
        budgets.MapPost("/transfers/{id:guid}/reject", (Guid id, ReasonInput input, BudgetService s) => s.DecideTransfer(id, false, input.Reason)).RequireAuthorization(Policies.Finance);
        req.MapPost("/{id:guid}/issue", (Guid id, RequisitionIssueInput input, RequisitionService s) => s.Issue(id, input)).RequireAuthorization(Policies.Stock);
        req.MapPost("/{id:guid}/order", (Guid id, RequisitionOrderInput input, RequisitionService s) => s.Order(id, input)).RequireAuthorization(Policies.Buy);

        // ---------- tenders and quotations ----------
        var tenders = api.MapGroup("/tenders").RequireAuthorization(Policies.BuyView);
        tenders.MapGet("", (string? view, TenderService s) => s.List(view));
        tenders.MapPost("", async (TenderInput input, TenderService s) => Results.Created("", new { (await s.Create(input)).Id })).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/from-request", async (FromRequisitionInput input, TenderService s) => Results.Created("", new { (await s.FromRequisition(input)).Id })).RequireAuthorization(Policies.Buy);
        tenders.MapGet("/{id:guid}", (Guid id, TenderService s) => s.Get(id));
        tenders.MapPut("/{id:guid}", async (Guid id, TenderInput input, TenderService s) => { await s.Update(id, input); return await s.Get(id); }).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/{id:guid}/publish", async (Guid id, TenderService s) => { await s.Publish(id); return await s.Get(id); }).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/{id:guid}/open", (Guid id, TenderService s) => s.Open(id)).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/{id:guid}/bids/{bidId:guid}/evaluate", (Guid id, Guid bidId, EvaluateInput input, TenderService s) => s.Evaluate(id, bidId, input)).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/{id:guid}/award", (Guid id, AwardInput input, TenderService s) => s.Award(id, input)).RequireAuthorization(Policies.Buy);
        tenders.MapPost("/{id:guid}/cancel", async (Guid id, ReasonInput input, TenderService s) => { await s.Cancel(id, input.Reason); return await s.Get(id); }).RequireAuthorization(Policies.Buy);

        // ---------- stock takes ----------
        var takes = api.MapGroup("/stock-takes").RequireAuthorization(Policies.StockView);
        takes.MapGet("", (StockTakeService s) => s.List());
        takes.MapPost("", (StockTakeInput input, StockTakeService s) => s.Start(input)).RequireAuthorization(Policies.Stock);
        takes.MapGet("/{id:guid}", (Guid id, bool? review, StockTakeService s) => s.Get(id, review == true));
        takes.MapPost("/{id:guid}/counts", (Guid id, List<CountLineInput> counts, StockTakeService s) => s.Count(id, counts)).RequireAuthorization(Policies.Stock);
        takes.MapPost("/{id:guid}/post", (Guid id, StockTakeService s) => s.Post(id)).RequireAuthorization(Policies.Stock);
        takes.MapPost("/{id:guid}/cancel", (Guid id, StockTakeService s) => s.Cancel(id)).RequireAuthorization(Policies.Stock);

        api.MapGet("/operations/finance", (DateOnly? from, DateOnly? to, OperationsFinance s) =>
            s.Get(from ?? new DateOnly(Clock.Today.Year, Clock.Today.Month, 1).AddMonths(-2), to ?? Clock.Today)).RequireAuthorization(Policies.Reports).CachedPerCompany();

        // ---------- bidding, without a sign-in ----------
        // The tender is found by its link's secret token; the request then runs as that business (read-only apart
        // from the bid itself), like the shop and lender pages.
        var bid = app.MapGroup("/api/bid/{token}").RequireRateLimiting("auth").AddEndpointFilter(async (inv, next) =>
        {
            var http = inv.HttpContext;
            var token = (http.Request.RouteValues["token"] as string ?? "").ToLowerInvariant();
            var db = http.RequestServices.GetRequiredService<ErpDb>();
            var t = token.Length == 24 ? await db.Tenders.IgnoreQueryFilters().Include(x => x.Lines).SingleOrDefaultAsync(x => x.Token == token) : null;
            if (t is null || t.Status == TenderStatus.Draft) return Results.NotFound(new { error = "There's no tender at this link. Check it with the business that sent it." });
            var ctx = http.RequestServices.GetRequiredService<RequestContext>();
            (ctx.CompanyId, ctx.Actor) = (t.CompanyId, "Bidder");
            http.Items["tender"] = t;
            return await next(inv);
        });
        bid.MapGet("", (HttpContext h, TenderService s) => s.PublicView((Tender)h.Items["tender"]!));
        bid.MapPost("", (BidInput input, HttpContext h, TenderService s) => s.SubmitBid((Tender)h.Items["tender"]!, input));

        // A business's open tenders, by its public link name.
        app.MapGet("/api/tender-board/{slug}", async (string slug, ErpDb db, RequestContext ctx, TenderService s) =>
        {
            var c = await db.Companies.Where(x => x.Slug == slug.ToLowerInvariant()).Select(x => (Guid?)x.Id).SingleOrDefaultAsync();
            if (c is null) return Results.NotFound(new { error = "No business has that page." });
            (ctx.CompanyId, ctx.Actor) = (c.Value, "Visitor");
            return Results.Ok(await s.Board(c.Value));
        }).RequireRateLimiting("auth");
    }
}
