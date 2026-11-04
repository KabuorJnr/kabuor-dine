// Kabuor Dine's own frame for the team: the floor, the kitchen and bar screens, the menu, tables and QR codes, the
// day's numbers and settings. Warm and dark, so nobody mistakes it for the ERP; the back office (stock, books, staff
// logins, plan and billing) is one tap away in the ERP screens, on the same data.
import { useEffect, useState } from 'react';
import { NavLink, Navigate, Outlet, Route, Routes, useLocation } from 'react-router';
import { BarChart3, ChefHat, LayoutGrid, LogOut, MoreHorizontal, QrCode, Settings2, SquareMenu, Store } from 'lucide-react';
import { can, setSession, useSession, type Permission } from '../lib/api';
import { cn } from '../lib/format';
import { TrialBanner } from '../components/Plan';
import ErrorBoundary from '../components/ErrorBoundary';
import { Sheet } from '../components/ui';
import Floor from './Floor';
import Kitchen from './Kitchen';
import MenuSetup from './MenuSetup';
import Tables from './Tables';
import DineToday from './DineToday';
import DineSettingsPage from './DineSettings';

const NAV: { to: string; label: string; icon: typeof LayoutGrid; need: Permission }[] = [
  { to: '/dine', label: 'Floor', icon: LayoutGrid, need: 'dine' },
  { to: '/dine/kitchen', label: 'Kitchen', icon: ChefHat, need: 'dine' },
  { to: '/dine/menu', label: 'Menu', icon: SquareMenu, need: 'dine' },
  { to: '/dine/tables', label: 'Tables & QR', icon: QrCode, need: 'dine.manage' },
  { to: '/dine/today', label: 'Today', icon: BarChart3, need: 'dine.manage' },
  { to: '/dine/settings', label: 'Settings', icon: Settings2, need: 'dine.manage' },
];

export function DineRoutes() {
  const s = useSession();
  const manage = can(s, 'dine.manage');
  return (
    <Routes>
      <Route element={<DineShell />}>
        <Route index element={<Floor />} />
        <Route path="kitchen" element={<Kitchen />} />
        <Route path="menu" element={<MenuSetup />} />
        <Route path="tables" element={manage ? <Tables /> : <Navigate to="/dine" replace />} />
        <Route path="today" element={manage ? <DineToday /> : <Navigate to="/dine" replace />} />
        <Route path="settings" element={manage ? <DineSettingsPage /> : <Navigate to="/dine" replace />} />
        <Route path="*" element={<Navigate to="/dine" replace />} />
      </Route>
    </Routes>
  );
}

function DineShell() {
  const s = useSession();
  const { pathname } = useLocation();
  const [more, setMore] = useState(false);
  // The kitchen screen runs full-bleed and dark, on a tablet on the pass.
  const kitchen = pathname.startsWith('/dine/kitchen');
  useEffect(() => setMore(false), [pathname]);
  if (!s) return null;
  const dineOnly = s.company.product === 'dine';
  const nav = NAV.filter((n) => can(s, n.need));
  // Phones: the four places used all service in the tab bar; the rest under More.
  const tabs = nav.slice(0, 4);
  const rest = nav.slice(4);
  const backOffice = (can(s, 'reports') || can(s, 'stock.view') || can(s, 'admin')) && { to: dineOnly ? '/invoices' : '/today', label: dineOnly ? 'Back office' : 'Kabuor ERP' };
  return (
    <div className={cn('min-h-screen', kitchen ? 'bg-dine-night' : 'bg-paper')}>
      <header className="sticky top-0 z-40 bg-dine-night text-white print:hidden">
        <div className="mx-auto flex max-w-[1400px] items-center gap-3 px-4 py-2.5">
          <NavLink to="/dine" className="flex min-w-0 shrink-0 items-center gap-2">
            <span className="grid h-8 w-8 shrink-0 place-items-center rounded-lg bg-dine font-bold">D</span>
            <span className="min-w-0 leading-tight">
              <span className="block text-sm font-bold">Kabuor Dine</span>
              <span className="block max-w-[200px] truncate text-[11px] text-white/60">{s.company.name}</span>
            </span>
          </NavLink>
          <nav className="no-scrollbar hidden min-w-0 flex-1 gap-1 overflow-x-auto md:flex" aria-label="Kabuor Dine">
            {nav.map((n) => (
              <NavLink key={n.to} to={n.to} end={n.to === '/dine'}
                className={({ isActive }) => cn('flex shrink-0 items-center gap-1.5 rounded-md px-3 py-2 text-sm font-semibold transition', isActive ? 'bg-white/12 text-white' : 'text-white/65 hover:bg-white/8 hover:text-white')}>
                <n.icon className="h-4 w-4" /> {n.label}
              </NavLink>
            ))}
          </nav>
          <span className="flex-1 md:hidden" />
          {backOffice && (
            <NavLink to={backOffice.to} title={dineOnly ? 'Bills, money in, stock, reports, staff and settings' : 'Back to Kabuor ERP'} className="hidden shrink-0 items-center gap-1.5 rounded-md px-3 py-2 text-sm font-semibold text-white/70 hover:bg-white/8 hover:text-white md:flex">
              <Store className="h-4 w-4" /> {backOffice.label}
            </NavLink>
          )}
          <button onClick={() => setSession(null)} title={`Sign out ${s.user.name}`} className="hidden h-9 w-9 shrink-0 place-items-center rounded-md text-white/70 hover:bg-white/8 hover:text-white md:grid" aria-label="Sign out">
            <LogOut className="h-4 w-4" />
          </button>
        </div>
      </header>
      <main className={cn(kitchen ? 'pb-20 md:pb-0' : 'mx-auto max-w-[1400px] px-4 pt-5 pb-28 sm:px-6 md:pb-8 lg:px-8')}>
        {!kitchen && can(s, 'admin') && <div className="print:hidden"><TrialBanner /></div>}
        <ErrorBoundary><Outlet /></ErrorBoundary>
      </main>

      {/* Phone tab bar */}
      <nav aria-label="Kabuor Dine" style={{ gridTemplateColumns: `repeat(${tabs.length + 1}, minmax(0, 1fr))` }}
        className={cn('fixed inset-x-0 bottom-0 z-40 grid border-t pb-[env(safe-area-inset-bottom)] md:hidden print:hidden', kitchen ? 'border-white/10 bg-dine-night' : 'border-line bg-white')}>
        {tabs.map((n) => (
          <NavLink key={n.to} to={n.to} end={n.to === '/dine'}
            className={({ isActive }) => cn('flex flex-col items-center gap-0.5 py-2 text-[11px] font-semibold', isActive ? 'text-dine' : kitchen ? 'text-white/55' : 'text-muted')}>
            <n.icon className="h-5 w-5" /> {n.label.replace(' & QR', '')}
          </NavLink>
        ))}
        <button onClick={() => setMore(true)} className={cn('flex flex-col items-center gap-0.5 py-2 text-[11px] font-semibold', kitchen ? 'text-white/55' : 'text-muted')} aria-label="More">
          <MoreHorizontal className="h-5 w-5" /> More
        </button>
      </nav>
      <Sheet open={more} onClose={() => setMore(false)} title={s.company.name} description={`Signed in as ${s.user.name}`} size="sm">
        <div className="space-y-1">
          {rest.map((n) => (
            <NavLink key={n.to} to={n.to} className="flex items-center gap-3 rounded-lg px-3 py-3 text-sm font-semibold hover:bg-paper"><n.icon className="h-5 w-5 text-dine" /> {n.label}</NavLink>
          ))}
          {backOffice && <NavLink to={backOffice.to} className="flex items-center gap-3 rounded-lg px-3 py-3 text-sm font-semibold hover:bg-paper"><Store className="h-5 w-5 text-dine" /> {backOffice.label} <span className="ml-auto text-xs font-normal text-muted">bills, stock, reports</span></NavLink>}
          <button onClick={() => setSession(null)} className="flex w-full items-center gap-3 rounded-lg px-3 py-3 text-sm font-semibold text-bad hover:bg-bad-soft"><LogOut className="h-5 w-5" /> Sign out</button>
        </div>
      </Sheet>
    </div>
  );
}
