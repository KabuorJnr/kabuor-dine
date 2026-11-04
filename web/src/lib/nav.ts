// The sections of the app, grouped by business OS module, as each person may see them. The sidebar shows them all, or
// one module's when the person works in that module's workspace; the workspace home lists the same pages.
import {
  Armchair, BarChart3, BookOpen, Bot, Briefcase, Building2, Landmark, LayoutDashboard, Link2, MessageCircleQuestion, CalendarDays, ListChecks, ShieldCheck, Factory, FileText, Handshake, Home, Kanban, Megaphone, Package, Quote, Settings as Cog, ShoppingBag, Store, Truck, MapPinned, UserRound, Users, Wallet, Banknote,
  Wrench, ClipboardList, Gavel, ClipboardCheck, Network, ChartPie, Workflow, PiggyBank, FileLock2, HandCoins, Layers,
} from 'lucide-react';
import { can, canView, type Session } from './api';
import type { ModuleKey } from './modules';
import type { Key } from './i18n';

export type NavLink = { to: string; label: string; icon: typeof Home; end?: boolean };
export type NavGroup = { title: string; module?: ModuleKey; links: (NavLink | false)[] };
export type VisibleGroup = { title: string; module?: ModuleKey; links: NavLink[] };

export function navGroups(session: Session, t: (k: Key) => string): VisibleGroup[] {
  const isPlatformAdmin = session.user.isPlatformAdmin === true;
  const isEmployee = session.user.role === 'Employee';

  // Each section is one of the business OS modules, shown in its colour with its icon so it's known at a glance.
  const groups: NavGroup[] = isEmployee
    ? [
        {
          title: '',
          links: [
            { to: '/portal', label: 'My Portal', icon: UserRound, end: true },
            { to: '/security', label: 'Sign-in security', icon: ShieldCheck },
          ],
        },
      ]
    : [
        ...(isPlatformAdmin
          ? [
              {
                title: 'Founder & Platform',
                links: [
                  { to: '/platform', label: 'Founder Command Center', icon: Building2 },
                ],
              },
            ]
          : []),
        {
          title: '',
          links: [
            { to: '/', label: t('Today'), icon: Home, end: true },
          ],
        },
        {
          title: 'Intelligence',
          module: 'intelligence',
          links: [
            can(session, 'reports') && { to: '/os', label: 'Business OS', icon: Workflow },
            can(session, 'reports') && { to: '/dashboards', label: 'Dashboards', icon: LayoutDashboard },
            (can(session, 'sell') || can(session, 'finance') || can(session, 'buy') || can(session, 'admin')) && { to: '/agents', label: 'Agents', icon: Bot },
          ],
        },
        {
          title: 'Sales & marketing',
          module: 'sales',
          links: [
            can(session, 'pos') && { to: '/pos', label: 'Point of sale', icon: Store },
            can(session, 'sales.view') && { to: '/invoices', label: t('Invoices'), icon: FileText },
            can(session, 'sales.view') && { to: '/quotes', label: t('Quotes'), icon: Quote },
            can(session, 'sales.view') && { to: '/customers', label: t('Customers'), icon: Users },
            can(session, 'sales.view') && { to: '/inquiries', label: 'Inquiries', icon: MessageCircleQuestion },
            can(session, 'crm') && { to: '/pipeline', label: 'Pipeline', icon: Kanban },
            can(session, 'crm') && { to: '/campaigns', label: 'Campaigns', icon: Megaphone },
            can(session, 'crm') && { to: '/links', label: 'Tracking links', icon: Link2 },
          ],
        },
        {
          // The finance department's corner: its overview first, then the daily money work, then the books.
          title: 'Finance',
          module: 'finance',
          links: [
            can(session, 'reports') && { to: '/finance', label: 'Finance overview', icon: Landmark, end: true },
            { to: '/finance/budgets', label: 'Budgets', icon: PiggyBank },
            can(session, 'reports') && { to: '/finance/approvals', label: 'Approvals register', icon: FileLock2 },
            (can(session, 'finance') || can(session, 'people')) && { to: '/finance/advances', label: 'Cash advances', icon: HandCoins },
            can(session, 'sales.view') && { to: '/money-in', label: t('Money in'), icon: Wallet },
            can(session, 'finance') && { to: '/reconcile', label: 'Reconcile', icon: ListChecks },
            can(session, 'reports') && { to: '/finance/ledger', label: 'Accounts & ledger', icon: BookOpen },
            can(session, 'reports') && { to: '/reports', label: t('Reports'), icon: BarChart3 },
            can(session, 'reports') && { to: '/finance/operations', label: 'Operations costs', icon: ChartPie },
            can(session, 'verified.view') && { to: '/verified', label: 'Verified', icon: ShieldCheck },
          ],
        },
        {
          title: 'Operations',
          module: 'operations',
          links: [
            can(session, 'maintain.view') && { to: '/maintenance', label: 'Maintenance', icon: Wrench },
            can(session, 'stock.view') && { to: '/assets', label: 'Assets', icon: Armchair },
            can(session, 'fleet.view') && { to: '/fleet', label: 'Fleet & dispatch', icon: Truck, end: true },
            can(session, 'fleet.view') && { to: '/fleet/live', label: 'Live map', icon: MapPinned },
            canView(session, 'make') && { to: '/production', label: 'Production', icon: Factory },
          ],
        },
        {
          title: 'Procurement & supply',
          module: 'procurement',
          links: [
            { to: '/procurement', label: 'Requests', icon: ClipboardList, end: true },
            can(session, 'buy.view') && { to: '/procurement/tenders', label: 'Tenders & quotations', icon: Gavel },
            canView(session, 'make') && { to: '/production?tab=plan', label: 'What to make & buy', icon: Layers },
            can(session, 'buy.view') && { to: '/purchases', label: 'Orders & bills', icon: ShoppingBag },
            can(session, 'buy.view') && { to: '/suppliers', label: t('Suppliers'), icon: Handshake },
            can(session, 'stock.view') && { to: '/stock', label: t('Stock'), icon: Package, end: true },
            can(session, 'stock.view') && { to: '/stock/takes', label: 'Stock takes', icon: ClipboardCheck },
          ],
        },
        {
          title: 'Human resources',
          module: 'people',
          links: [
            can(session, 'people') && { to: '/people', label: 'Employees', icon: UserRound },
            (can(session, 'people') || can(session, 'finance')) && { to: '/departments', label: 'Departments', icon: Network },
            can(session, 'people') && { to: '/recruitment', label: 'Recruitment', icon: Briefcase },
            can(session, 'people') && { to: '/leave', label: 'Leave', icon: CalendarDays },
            can(session, 'people') && { to: '/payroll', label: 'Payroll', icon: Banknote },
          ],
        },
        { title: '', links: [{ to: '/security', label: 'Sign-in security', icon: ShieldCheck }, can(session, 'admin') && { to: '/settings', label: t('Settings'), icon: Cog }] },
      ];
  return groups.map((g) => ({ ...g, links: g.links.filter(Boolean) as NavLink[] })).filter((g) => g.links.length);
}

/** The module whose section holds this page, if any (the longest matching address wins). */
export function moduleOf(groups: VisibleGroup[], pathname: string, search = ''): ModuleKey | null {
  let best: { m: ModuleKey; len: number } | null = null;
  for (const g of groups) {
    if (!g.module) continue;
    for (const n of g.links) {
      const [p, q] = n.to.split('?');
      if (p === '/') continue;
      const hit = n.end ? pathname === p : pathname === p || pathname.startsWith(p + '/');
      if (!hit) continue;
      const len = p.length + (q && search.includes(q) ? q.length : 0);
      if (!best || len > best.len) best = { m: g.module, len };
    }
  }
  return best?.m ?? null;
}

