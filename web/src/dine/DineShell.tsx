// Kabuor Dine's own frame for the team: the floor, the kitchen and bar screens, the menu, tables and QR codes, the
// day's numbers and settings. Warm and dark, so nobody mistakes it for the ERP; the back office (stock, books, staff
// logins, plan and billing) is one tap away in the ERP screens, on the same data.
import { NavLink, Navigate, Outlet, Route, Routes, useLocation } from 'react-router';
import { BarChart3, ChefHat, LayoutGrid, LogOut, QrCode, Settings2, SquareMenu, Store } from 'lucide-react';
import { can, setSession, useSession, type Permission } from '../lib/api';
import { cn } from '../lib/format';
import { TrialBanner } from '../components/Plan';
import ErrorBoundary from '../components/ErrorBoundary';
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
  // The kitchen screen runs full-bleed and dark, on a tablet on the pass.
  const kitchen = pathname.startsWith('/dine/kitchen');
  if (!s) return null;
  const dineOnly = s.company.product === 'dine';
  return (
    <div className={cn('min-h-screen', kitchen ? 'bg-dine-night' : 'bg-paper')}>
      <header className="sticky top-0 z-40 bg-dine-night text-white print:hidden">
        <div className="mx-auto flex max-w-[1400px] items-center gap-3 px-4 py-2.5">
          <NavLink to="/dine" className="flex shrink-0 items-center gap-2">
            <span className="grid h-8 w-8 place-items-center rounded-lg bg-dine font-bold">D</span>
            <span className="hidden leading-tight sm:block">
              <span className="block text-sm font-bold">Kabuor Dine</span>
              <span className="block max-w-[180px] truncate text-[11px] text-white/60">{s.company.name}</span>
            </span>
          </NavLink>
          <nav className="no-scrollbar flex min-w-0 flex-1 gap-1 overflow-x-auto" aria-label="Kabuor Dine">
            {NAV.filter((n) => can(s, n.need)).map((n) => (
              <NavLink key={n.to} to={n.to} end={n.to === '/dine'}
                className={({ isActive }) => cn('flex shrink-0 items-center gap-1.5 rounded-md px-3 py-2 text-sm font-semibold transition', isActive ? 'bg-white/12 text-white' : 'text-white/65 hover:bg-white/8 hover:text-white')}>
                <n.icon className="h-4 w-4" /> {n.label}
              </NavLink>
            ))}
          </nav>
          {(can(s, 'reports') || can(s, 'stock.view') || can(s, 'admin')) && (
            <NavLink to="/today" title={dineOnly ? 'Stock, books, staff logins, plan and billing' : 'Back to Kabuor ERP'} className="hidden shrink-0 items-center gap-1.5 rounded-md px-3 py-2 text-sm font-semibold text-white/70 hover:bg-white/8 hover:text-white md:flex">
              <Store className="h-4 w-4" /> {dineOnly ? 'Back office' : 'Kabuor ERP'}
            </NavLink>
          )}
          <button onClick={() => setSession(null)} title={`Sign out ${s.user.name}`} className="grid h-9 w-9 shrink-0 place-items-center rounded-md text-white/70 hover:bg-white/8 hover:text-white" aria-label="Sign out">
            <LogOut className="h-4 w-4" />
          </button>
        </div>
      </header>
      <main className={cn(kitchen ? '' : 'mx-auto max-w-[1400px] px-4 py-5 sm:px-6 lg:px-8')}>
        {!kitchen && can(s, 'admin') && <div className="print:hidden"><TrialBanner /></div>}
        <ErrorBoundary><Outlet /></ErrorBoundary>
      </main>
    </div>
  );
}
