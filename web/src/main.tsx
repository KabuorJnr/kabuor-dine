import { lazy, StrictMode, Suspense, type ComponentType } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Link, Navigate, Route, Routes } from 'react-router';
import './index.css';
import { registerShell } from './lib/offline';
import { reportError, startMonitoring } from './lib/monitoring';
import { can, canView, getSession, useSession, type Permission } from './lib/api';
import Shell from './components/Shell';
import ErrorBoundary from './components/ErrorBoundary';
import { ChooseOwnPassword, SignIn, SignUp } from './pages/Auth';

import { DialogHost } from './components/ui';
import { installPrinting } from './components/PrintDoc';

// Every page loads on demand, so the first visit downloads the frame and the page in hand, not the whole app (a
// guest scanning a table's QR gets the menu, not the ERP). After a release, a page from the old build may be gone:
// the app reloads once to pick up the new one (vite:preloadError).
function named<M>(load: () => Promise<M>, key: keyof M) {
  return lazy(async () => ({ default: (await load())[key] as ComponentType<any> }));
}
const Activate = lazy(() => import('./pages/Activate'));
const Today = lazy(() => import('./pages/Today'));
const InvoiceList = named(() => import('./pages/Invoices'), 'InvoiceList');
const InvoiceEditor = lazy(() => import('./pages/InvoiceEditor'));
const InvoiceView = lazy(() => import('./pages/InvoiceView'));
const CustomerList = named(() => import('./pages/Customers'), 'CustomerList');
const CustomerView = named(() => import('./pages/Customers'), 'CustomerView');
const Payments = lazy(() => import('./pages/Payments'));
const Reports = lazy(() => import('./pages/Reports'));
const Settings = lazy(() => import('./pages/Settings'));
const PurchaseList = named(() => import('./pages/Purchases'), 'PurchaseList');
const PurchaseEditor = lazy(() => import('./pages/PurchaseEditor'));
const PurchaseView = lazy(() => import('./pages/PurchaseView'));
const SupplierList = named(() => import('./pages/Suppliers'), 'SupplierList');
const SupplierView = named(() => import('./pages/Suppliers'), 'SupplierView');
const Stock = lazy(() => import('./pages/Stock'));
const Assets = lazy(() => import('./pages/Assets'));
const Fleet = lazy(() => import('./pages/Fleet'));
const DispatchMap = lazy(() => import('./pages/DispatchMap'));
const Drive = lazy(() => import('./pages/Drive'));
const EmployeeList = named(() => import('./pages/Employees'), 'EmployeeList');
const EmployeeView = named(() => import('./pages/Employees'), 'EmployeeView');
const Leave = lazy(() => import('./pages/Leave'));
const PayrollList = named(() => import('./pages/Payroll'), 'PayrollList');
const PayrollRun = named(() => import('./pages/Payroll'), 'PayrollRun');
const Recruitment = lazy(() => import('./pages/Recruitment'));
const LeadView = named(() => import('./pages/Pipeline'), 'LeadView');
const Pipeline = named(() => import('./pages/Pipeline'), 'Pipeline');
const Production = lazy(() => import('./pages/Production'));
const CampaignEditor = named(() => import('./pages/Campaigns'), 'CampaignEditor');
const CampaignList = named(() => import('./pages/Campaigns'), 'CampaignList');
const CampaignView = named(() => import('./pages/Campaigns'), 'CampaignView');
const Pos = lazy(() => import('./pages/Pos'));
const Inquiries = lazy(() => import('./pages/Inquiries'));
const Links = lazy(() => import('./pages/Links'));
const Shop = lazy(() => import('./pages/Shop'));
const Privacy = named(() => import('./pages/Legal'), 'Privacy');
const Terms = named(() => import('./pages/Legal'), 'Terms');
const MyPortal = lazy(() => import('./pages/MyPortal'));
const Workspace = lazy(() => import('./pages/Workspace'));
const Agents = lazy(() => import('./pages/Agents'));
const Security = lazy(() => import('./pages/Security'));
const Reconcile = lazy(() => import('./pages/Reconcile'));
const EtimsInbox = lazy(() => import('./pages/EtimsInbox'));
const Platform = lazy(() => import('./pages/Platform'));
const Finance = lazy(() => import('./pages/Finance'));
const LedgerPage = lazy(() => import('./pages/Ledger'));
const MonthlyReport = lazy(() => import('./pages/MonthlyReport'));
const Dashboards = lazy(() => import('./pages/Dashboards'));
const Verified = lazy(() => import('./pages/Verified'));
const VerifiedView = lazy(() => import('./pages/VerifiedView'));
const Maintenance = lazy(() => import('./pages/Maintenance'));
const Requisitions = lazy(() => import('./pages/Requisitions'));
const Tenders = lazy(() => import('./pages/Tenders'));
const TenderBid = named(() => import('./pages/Tenders'), 'TenderBid');
const TenderBoard = named(() => import('./pages/Tenders'), 'TenderBoard');
const StockTakes = lazy(() => import('./pages/StockTakes'));
const Departments = lazy(() => import('./pages/Departments'));
const OperationsFinance = lazy(() => import('./pages/OperationsFinance'));
const Budgets = lazy(() => import('./pages/Budgets'));
const Approvals = lazy(() => import('./pages/Approvals'));
const BusinessOS = lazy(() => import('./pages/BusinessOS'));
const GuestMenu = lazy(() => import('./dine/GuestMenu'));
const DineLanding = lazy(() => import('./dine/Landing'));
const DineRoutes = named(() => import('./dine/DineShell'), 'DineRoutes');
const Advances = lazy(() => import('./pages/Advances'));

const PageLoading = () => <div className="grid min-h-[40vh] place-items-center"><span className="h-6 w-6 animate-spin rounded-full border-2 border-line border-t-brand" aria-label="Loading" /></div>;

/** A page outside this person's role: say so plainly instead of showing a half-loaded screen. */
function Need({ p, view, children }: { p: Permission; view?: boolean; children: React.ReactNode }) {
  // view: an overseeing owner may look at this page of the daily work, without its buttons.
  if (view ? canView(getSession(), p) : can(getSession(), p)) return <>{children}</>;
  return (
    <div className="card mx-auto mt-10 max-w-md px-6 py-10 text-center">
      <p className="font-semibold">This isn’t part of your role</p>
      <p className="mt-1 text-sm text-muted">Ask the owner to change your role in Settings if you need it.</p>
      <Link to="/" className="btn btn-primary mt-4">Back to Today</Link>
    </div>
  );
}

function App() {
  const session = useSession();

  if (!session)
    return (
      <Suspense fallback={<PageLoading />}><Routes>
        <Route path="/" element={<DineLanding />} />
        <Route path="/signin" element={<SignIn />} />
        <Route path="/signup" element={<SignUp />} />
        <Route path="/d/:token" element={<Drive />} />
        <Route path="/v/:token" element={<VerifiedView />} />
        <Route path="/privacy" element={<Privacy />} />
        <Route path="/s/:slug" element={<Shop />} />
        <Route path="/m/:token" element={<GuestMenu />} />
        <Route path="/bid/:token" element={<TenderBid />} />
        <Route path="/tenders/:slug" element={<TenderBoard />} />
        <Route path="/terms" element={<Terms />} />
        <Route path="*" element={<SignIn />} />
      </Routes></Suspense>
    );
  if (session.user.mustChangePassword) return <ChooseOwnPassword session={session} />;
  if (session.company.awaitingFirstPayment)
    return (
      <Suspense fallback={<PageLoading />}><Routes>
        <Route path="privacy" element={<Privacy />} />
        <Route path="terms" element={<Terms />} />
        <Route path="*" element={<Activate session={session} />} />
      </Routes></Suspense>
    );
  return (
    <Suspense fallback={<PageLoading />}><Routes>
      <Route path="privacy" element={<Privacy />} />
      <Route path="s/:slug" element={<Shop />} />
      <Route path="m/:token" element={<GuestMenu />} />
      <Route path="terms" element={<Terms />} />
      <Route path="d/:token" element={<Drive />} />
      {/* Kabuor Dine: its own frame (dine/DineShell.tsx), on the same sign-in and data. */}
      <Route path="dine/*" element={<Need p="dine"><DineRoutes /></Need>} />
      <Route path="v/:token" element={<VerifiedView />} />
      <Route path="bid/:token" element={<TenderBid />} />
      <Route path="tenders/:slug" element={<TenderBoard />} />
      <Route element={<Shell session={session} />}>
        <Route
          index
          element={
            session.user.isPlatformAdmin ? (
              <Navigate to="/platform" replace />
            ) : session.company.product === 'dine' && can(session, 'dine') ? (
              <Navigate to="/dine" replace />
            ) : session.user.role === 'Employee' ? (
              <Navigate to="/portal" replace />
            ) : session.user.role === 'Sales' ? (
              <Navigate to="/pos" replace />
            ) : session.user.role === 'Dispatcher' ? (
              <Navigate to="/fleet/live" replace />
            ) : session.user.role === 'Maintenance' ? (
              <Navigate to="/maintenance" replace />
            ) : (
              <Today />
            )
          }
        />
        <Route path="today" element={<Today />} />
        <Route path="w/:module" element={<Workspace />} />
        <Route path="portal" element={<MyPortal />} />
        <Route path="pos" element={<Need p="pos"><Pos /></Need>} />
        <Route path="invoices" element={<Need p="sales.view"><InvoiceList kind="Invoice" /></Need>} />
        <Route path="quotes" element={<Need p="sales.view"><InvoiceList kind="Quote" /></Need>} />
        <Route path="invoices/new" element={<Need p="sell"><InvoiceEditor kind="Invoice" /></Need>} />
        <Route path="quotes/new" element={<Need p="sell"><InvoiceEditor kind="Quote" /></Need>} />
        <Route path="sales/:id" element={<Need p="sales.view"><InvoiceView /></Need>} />
        <Route path="sales/:id/edit" element={<Need p="sell"><InvoiceEditor kind="Invoice" /></Need>} />
        <Route path="customers" element={<Need p="sales.view"><CustomerList /></Need>} />
        <Route path="customers/:id" element={<Need p="sales.view"><CustomerView /></Need>} />
        <Route path="purchases" element={<Need p="buy.view"><PurchaseList /></Need>} />
        <Route path="purchases/new" element={<Need p="buy.view"><PurchaseEditor /></Need>} />
        <Route path="purchases/:id" element={<Need p="buy.view"><PurchaseView /></Need>} />
        <Route path="purchases/:id/edit" element={<Need p="buy.view"><PurchaseEditor /></Need>} />
        <Route path="suppliers" element={<Need p="buy.view"><SupplierList /></Need>} />
        <Route path="suppliers/:id" element={<Need p="buy.view"><SupplierView /></Need>} />
        <Route path="stock" element={<Need p="stock.view"><Stock /></Need>} />
        <Route path="assets" element={<Need p="stock.view"><Assets /></Need>} />
        <Route path="maintenance" element={<Need p="maintain.view"><Maintenance /></Need>} />
        <Route path="procurement" element={<Need p="read"><Requisitions /></Need>} />
        <Route path="procurement/tenders" element={<Need p="buy.view"><Tenders /></Need>} />
        <Route path="stock/takes" element={<Need p="stock.view"><StockTakes /></Need>} />
        <Route path="departments" element={<Need p={can(session, 'people') ? 'people' : 'finance'}><Departments /></Need>} />
        <Route path="fleet" element={<Need p="fleet.view"><Fleet /></Need>} />
        <Route path="fleet/live" element={<Need p="fleet.view"><DispatchMap /></Need>} />
        <Route path="people" element={<Need p="people"><EmployeeList /></Need>} />
        <Route path="people/:id" element={<Need p="people"><EmployeeView /></Need>} />
        <Route path="recruitment" element={<Need p="people"><Recruitment /></Need>} />
        <Route path="leave" element={<Need p="people"><Leave /></Need>} />
        <Route path="payroll" element={<Need p="people"><PayrollList /></Need>} />
        <Route path="payroll/:id" element={<Need p="people"><PayrollRun /></Need>} />
        <Route path="pipeline" element={<Need p="crm"><Pipeline /></Need>} />
        <Route path="pipeline/:id" element={<Need p="crm"><LeadView /></Need>} />
        <Route path="production" element={<Need p="make" view><Production /></Need>} />
        <Route path="campaigns" element={<Need p="crm"><CampaignList /></Need>} />
        <Route path="intelligence" element={<Navigate to="/os?tab=insights" replace />} />
        <Route path="inquiries" element={<Need p="sales.view"><Inquiries /></Need>} />
        <Route path="links" element={<Need p="crm"><Links /></Need>} />
        <Route path="campaigns/new" element={<Need p="crm"><CampaignEditor /></Need>} />
        <Route path="campaigns/:id" element={<Need p="crm"><CampaignView /></Need>} />
        <Route path="money-in" element={<Need p="sales.view"><Payments /></Need>} />
        <Route path="reports" element={<Need p="reports"><Reports /></Need>} />
        <Route path="verified" element={<Need p="verified.view"><Verified /></Need>} />
        <Route path="reports/monthly" element={<Need p="reports"><MonthlyReport /></Need>} />
        <Route path="dashboards" element={<Need p="reports"><Dashboards /></Need>} />
        <Route path="finance" element={<Need p="reports"><Finance /></Need>} />
        <Route path="finance/ledger" element={<Need p="reports"><LedgerPage /></Need>} />
        <Route path="finance/operations" element={<Need p="reports"><OperationsFinance /></Need>} />
        <Route path="finance/budgets" element={<Budgets />} />
        <Route path="finance/approvals" element={<Need p="reports"><Approvals /></Need>} />
        <Route path="os" element={<Need p="reports"><BusinessOS /></Need>} />
        <Route path="os/plan" element={<Navigate to="/os?tab=plan" replace />} />
        <Route path="os/health" element={<Navigate to="/os?tab=health" replace />} />
        <Route path="procurement/mrp" element={<Navigate to="/production?tab=plan" replace />} />
        <Route path="finance/advances" element={<Need p={can(session, 'people') ? 'people' : can(session, 'reports') ? 'reports' : 'finance'}><Advances /></Need>} />
        <Route path="ledger" element={<Need p="reports"><LedgerPage /></Need>} />
        <Route path="agents" element={<Agents />} />
        <Route path="security" element={<Security />} />
        <Route path="purchases/etims" element={<Need p="finance"><EtimsInbox /></Need>} />
        <Route path="reconcile" element={<Need p="finance"><Reconcile /></Need>} />
        <Route path="settings" element={<Need p="admin"><Settings /></Need>} />
        <Route path="platform" element={session.user.isPlatformAdmin ? <Platform /> : <Navigate to="/" replace />} />
        <Route path="admin" element={session.user.isPlatformAdmin ? <Navigate to="/platform" replace /> : <Navigate to="/" replace />} />
        <Route path="*" element={<Navigate to="/" replace />} />
      </Route>
    </Routes></Suspense>
  );
}

registerShell();
// A page from the build before a release is gone from the server: reload once to get the new build.
window.addEventListener('vite:preloadError', (e) => {
  try {
    if (sessionStorage.getItem('kerp-reloaded') === location.pathname) return;
    sessionStorage.setItem('kerp-reloaded', location.pathname);
  } catch { /* reload anyway */ }
  e.preventDefault();
  location.reload();
});
startMonitoring();
// Printing from anywhere (a Print button, Ctrl+P) gives a document, never the web page.
installPrinting();

createRoot(document.getElementById('root')!, { onUncaughtError: (e) => (reportError(e), console.error(e)), onCaughtError: (e) => reportError(e) }).render(
  <StrictMode>
    <BrowserRouter>
      <ErrorBoundary whole>
        <App />
      </ErrorBoundary>
      <DialogHost />
    </BrowserRouter>
  </StrictMode>,
);
