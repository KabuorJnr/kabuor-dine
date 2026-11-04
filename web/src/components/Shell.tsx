// The frame: a grouped sidebar on a computer; on a phone, the four things people do most plus "More" (every
// section and Msaidizi). Each person only sees the sections their role can use.
import { Suspense, useEffect, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';
import { Activity, Armchair, CalendarDays, Check, ChevronDown, Factory, FileText, Home, LayoutGrid, LogOut, Menu, Package, Search, ShieldCheck, ShoppingBag, Sparkles, Store, UserRound, Users, Wallet, Wrench } from 'lucide-react';
import { MODULES, type ModuleKey } from '../lib/modules';
import { navGroups, type NavLink as Link, type VisibleGroup } from '../lib/nav';
import { useWorkspace, type Workspace } from '../lib/workspace';
import { useLiveConnection } from '../lib/live';
import { LiveMark, LiveNotices } from './LiveBits';
import { can, oversees, setSession, MOCK_ACCOUNTS, api, type Session } from '../lib/api';
import { cn } from '../lib/format';
import { setLang, useLang } from '../lib/i18n';
import AssistantPanel from './AssistantPanel';
import ErrorBoundary from './ErrorBoundary';
import TellMe from './TellMe';
import { TrialBanner } from './Plan';
import { Sheet } from './ui';

export default function Shell({ session }: { session: Session }) {
  const { t, lang } = useLang();
  const [assistant, setAssistant] = useState(false);
  const [more, setMore] = useState(false);
  const [tellMe, setTellMe] = useState(false);
  const [accountMenuOpen, setAccountMenuOpen] = useState(false);
  const { pathname } = useLocation();
  const [folded, setFolded] = useState<Set<string>>(() => {
    try { return new Set(JSON.parse(localStorage.getItem('kerp-folded') ?? '[]') as string[]); } catch { return new Set(); }
  });
  useEffect(() => setMore(false), [pathname]);
  // Ctrl/Cmd+K, or "/" when not typing, opens Tell me from anywhere.
  useEffect(() => {
    const on = (e: KeyboardEvent) => {
      const typing = e.target instanceof HTMLElement && (['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName) || e.target.isContentEditable);
      if ((e.key.toLowerCase() === 'k' && (e.ctrlKey || e.metaKey)) || (e.key === '/' && !typing)) {
        e.preventDefault();
        setTellMe(true);
      }
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, []);
  const initials = session.user.name.split(' ').map((w) => w[0]).slice(0, 2).join('').toUpperCase();

  const isPlatformAdmin = session.user.isPlatformAdmin === true;
  const isEmployee = session.user.role === 'Employee';
  const all = navGroups(session, t);
  useLiveConnection();
  const { workspace, choose, available } = useWorkspace(session, all);
  // In a module's workspace the sidebar holds only that module: its home, its pages, and the account links.
  const visible: VisibleGroup[] = workspace
    ? all.flatMap((g): VisibleGroup[] => {
        if (g.module === workspace) return [{ title: '', links: [{ to: '/', label: t('Today'), icon: Home, end: true }, { to: `/w/${workspace}`, label: `${MODULES[workspace].short} home`, icon: LayoutGrid, end: true }] }, g];
        if (g.module) return [];
        if (g.links.some((n) => n.to === '/')) return [];
        return [g];
      })
    : all;
  // Phone tabs: Today, then the first three sections this role uses most, in this order of preference.
  const phoneTabs: Link[] = isPlatformAdmin
    ? [
        { to: '/platform', label: 'Console', icon: Activity, end: true },
        { to: '/security', label: 'Security', icon: ShieldCheck },
      ]
    : isEmployee
    ? [{ to: '/portal', label: 'My Portal', icon: UserRound, end: true }]
    : session.company.product === 'dine'
    ? ([
        { to: '/dine', label: 'Dine', icon: Store, end: true },
        can(session, 'sales.view') && { to: '/invoices', label: 'Bills', icon: FileText },
        can(session, 'sales.view') && { to: '/money-in', label: t('Money in'), icon: Wallet },
        can(session, 'stock.view') && { to: '/stock', label: t('Stock'), icon: Package },
      ].filter(Boolean) as Link[])
    : [
        { to: '/', label: t('Today'), icon: Home, end: true },
        ...([
          session?.user?.role === 'Maintenance' && { to: '/maintenance', label: 'Maintenance', icon: Wrench },
          session?.user?.role === 'Maintenance' && { to: '/assets', label: 'Assets', icon: Armchair },
          can(session, 'pos') && { to: '/pos', label: 'POS', icon: Store },
          can(session, 'sales.view') && { to: '/invoices', label: t('Invoices'), icon: FileText },
          can(session, 'sales.view') && { to: '/money-in', label: t('Money in'), icon: Wallet },
          can(session, 'stock.view') && { to: '/stock', label: t('Stock'), icon: Package },
          can(session, 'buy.view') && { to: '/purchases', label: t('Purchases'), icon: ShoppingBag },
          can(session, 'people') && { to: '/people', label: 'Employees', icon: UserRound },
          can(session, 'people') && { to: '/leave', label: 'Leave', icon: CalendarDays },
          can(session, 'make') && { to: '/production', label: 'Production', icon: Factory },
          can(session, 'sales.view') && { to: '/customers', label: t('Customers'), icon: Users },
        ].filter(Boolean) as Link[]).slice(0, 3),
      ];

  // Module groups fold so the sidebar fits on one screen; the group you're working in stays open.
  const isIn = (g: { links: Link[] }) => g.links.some((n) => (n.end ? pathname === n.to.split('?')[0] : pathname.startsWith(n.to.split('?')[0])) && n.to !== '/');
  const toggle = (title: string) => setFolded((f) => {
    const next = new Set(f);
    if (next.has(title)) next.delete(title); else next.add(title);
    try { localStorage.setItem('kerp-folded', JSON.stringify([...next])); } catch {}
    return next;
  });
  const navList = (dense?: boolean) =>
    visible.map((g, i) => {
      const open = !g.module || !!workspace || isIn(g) || !folded.has(g.title);
      const M = g.module ? MODULES[g.module] : null;
      return (
        <div key={g.title || i} className={cn(i > 0 && (dense ? 'mt-2' : 'mt-3'))}>
          {g.title && !workspace && (M ? (
            <button type="button" onClick={() => toggle(g.title)} aria-expanded={open} className="group mb-0.5 flex w-full items-center gap-2 rounded-md px-3 py-1.5 text-left text-2xs font-semibold tracking-wider text-ink-2 uppercase hover:bg-paper">
              <span className="grid h-5 w-5 shrink-0 place-items-center rounded-md text-white" style={{ background: M.color }}><M.icon className="h-3 w-3" /></span>
              <span className="min-w-0 flex-1 truncate">{g.title}</span>
              <ChevronDown className={cn('h-3.5 w-3.5 text-faint transition', !open && '-rotate-90')} />
            </button>
          ) : <p className="mb-1 px-3 text-2xs font-semibold tracking-wider text-faint uppercase">{g.title}</p>)}
          {open && g.links.map((n) => (
            <NavLink key={n.to} to={n.to} end={n.end}
              style={({ isActive }) => (isActive && M ? { boxShadow: `inset 3px 0 0 ${M.color}` } : undefined)}
              className={({ isActive }) => cn('flex items-center gap-3 rounded-md px-3 py-1.5 text-sm font-medium transition', isActive ? 'bg-brand-soft text-brand' : 'text-ink-2 hover:bg-paper')}>
              <n.icon className="h-[18px] w-[18px] shrink-0" />
              <span className="truncate">{n.label}</span>
            </NavLink>
          ))}
        </div>
      );
    });

  return (
    <div className="min-h-svh lg:grid lg:grid-cols-[15rem_1fr] print:block">
      <aside className="sticky top-0 hidden h-svh flex-col border-r border-line bg-white px-3 py-4 lg:flex print:!hidden">
        <div className="min-w-0 px-2 leading-tight">
          {session.company.product === 'dine'
            ? <span className="flex items-center gap-2"><span className="grid h-7 w-7 place-items-center rounded-lg bg-dine text-sm font-bold text-white">D</span><span className="leading-tight"><span className="block text-sm font-bold">Kabuor Dine</span><span className="block text-2xs font-semibold tracking-wider text-faint uppercase">Back office</span></span></span>
            : <img src="/brand/kabuor-erp-logo-blue.svg" alt="Kabuor ERP" className="h-5 w-auto" />}
          <p className="mt-1.5 truncate text-xs text-muted">
            {session.user.isPlatformAdmin ? 'Platform Admin Console' : session.company.name}
          </p>
        </div>
        {available.length > 1 && <WorkspaceSwitcher className="mt-4" workspace={workspace} available={available} onChoose={choose} />}
        <nav className="sidebar-scroll mt-4 -mx-1 min-h-0 flex-1 overflow-y-auto px-1 pb-2" aria-label="Main">
          {navList()}
        </nav>
        <div className="space-y-3 border-t border-line px-2 pt-4 text-sm">
          <div className="inline-flex rounded-full bg-paper p-0.5 ring-1 ring-line" role="radiogroup" aria-label="Language">
            {(['en', 'sw'] as const).map((l) => (
              <button key={l} role="radio" aria-checked={lang === l} onClick={() => setLang(l)} className={cn('rounded-full px-3 py-1 text-xs font-semibold', lang === l ? 'bg-white shadow-sm ring-1 ring-line' : 'text-muted')}>
                {l === 'en' ? 'English' : 'Kiswahili'}
              </button>
            ))}
          </div>
          <div className="flex items-center justify-between gap-2">
            <span className="min-w-0">
              <b className="block truncate">{session.user.name}</b>
              <span className="text-xs text-muted font-medium">
                {session.user.isPlatformAdmin ? '🛡️ Platform Admin' : session.user.role}
              </span>
            </span>
            <SignOutButton label={t('Sign out')} />
          </div>
          <div className="flex items-center justify-center gap-3 pt-1 text-2xs text-muted">
            <NavLink to="/terms" className="hover:text-ink">Terms</NavLink>
            <span className="text-faint">·</span>
            <NavLink to="/privacy" className="hover:text-ink">Privacy</NavLink>
          </div>
        </div>
      </aside>

      {/* Phone top bar */}
      <header className="sticky top-0 z-30 flex items-center justify-between gap-3 border-b border-line bg-white/95 px-4 py-3 backdrop-blur lg:hidden print:hidden">
        <div className="flex min-w-0 items-center gap-2">
          <img src="/brand/kabuor-erp-symbol-blue.svg" alt="" className="h-6 w-auto shrink-0" />
          <span className="truncate font-display font-semibold">
            {session.user.isPlatformAdmin ? 'Kabuor Platform' : session.company.name}
          </span>
        </div>
        <div className="flex items-center gap-1.5">
          <button onClick={() => setTellMe(true)} className="grid h-8 w-8 place-items-center rounded-full text-muted hover:bg-paper" aria-label="Search">
            <Search className="h-4 w-4" />
          </button>
          <button onClick={() => setAssistant(true)} className="grid h-8 w-8 place-items-center rounded-full text-indigo hover:bg-paper" aria-label={t('Ask Msaidizi')}>
            <Sparkles className="h-4 w-4" />
          </button>
          <button onClick={() => setLang(lang === 'en' ? 'sw' : 'en')} className="rounded-full px-2 py-0.5 text-2xs font-semibold text-muted ring-1 ring-line">
            {lang === 'en' ? 'SW' : 'EN'}
          </button>
        </div>
      </header>

      <div className="min-w-0">
        {/* Desktop app bar: Tell me in the middle, the assistant and who's signed in on the right. */}
        <header className="sticky top-0 z-30 hidden h-14 items-center gap-4 border-b border-line bg-white/95 px-8 backdrop-blur lg:flex print:hidden">
          <button onClick={() => setTellMe(true)} className="flex h-9 w-full max-w-md items-center gap-2.5 rounded-md bg-paper px-3 text-sm text-faint ring-1 ring-line hover:ring-faint">
            <Search className="h-4 w-4" /> Tell me what you want to do
            <kbd className="ml-auto rounded border border-line bg-white px-1.5 text-2xs text-muted">Ctrl K</kbd>
          </button>
          <div className="ml-auto flex items-center gap-3">
            <LiveMark />
            <button onClick={() => setAssistant(true)} className="flex h-9 items-center gap-2 rounded-lg px-3 text-sm font-semibold text-indigo hover:bg-paper transition">
              <Sparkles className="h-4 w-4 text-gold" /> Msaidizi
            </button>
            {/* Quick Mock Role Switcher (development builds only: the list is empty in production) */}
            {MOCK_ACCOUNTS.length > 0 && <div className="relative">
              <button
                type="button"
                onClick={() => setAccountMenuOpen(!accountMenuOpen)}
                className="flex items-center gap-1.5 px-2.5 py-1.5 rounded-lg border border-line bg-paper hover:bg-white text-xs font-semibold text-slate-800 transition shadow-2xs"
                title="Switch between mock accounts & roles"
              >
                <span>👥</span>
                <span className="hidden sm:inline font-bold">{session.user.name.split(' ')[0]}</span>
                <span className="text-[10px] text-muted hidden md:inline">({session.user.role})</span>
                <ChevronDown className="h-3 w-3 text-muted ml-0.5" />
              </button>

              {accountMenuOpen && (
                <>
                  <div className="fixed inset-0 z-40" onClick={() => setAccountMenuOpen(false)} />
                  <div className="absolute right-0 mt-2 w-72 rounded-2xl border border-line bg-white p-2 shadow-2xl z-50 animate-in fade-in zoom-in-95 duration-100">
                    <div className="px-3 py-2 border-b border-line/60 mb-1.5">
                      <p className="text-[10px] font-bold text-muted uppercase tracking-wider">Switch Mock Account</p>
                      <p className="text-xs text-ink font-semibold truncate mt-0.5">{session.company.name}</p>
                    </div>
                    <div className="space-y-1 max-h-72 overflow-y-auto">
                      {MOCK_ACCOUNTS.map((acc) => {
                        const isCurrent = session.user.email === acc.email || session.user.id === acc.session.user.id;
                        return (
                          <button
                            key={acc.id}
                            type="button"
                            onClick={async () => {
                              try {
                                const r = await api<Session>('/auth/login', { body: { email: acc.email, password: acc.password } });
                                setSession(r);
                              } catch {
                                setSession(acc.session);
                              }
                              setAccountMenuOpen(false);
                            }}
                            className={cn(
                              'w-full flex items-center justify-between p-2 rounded-xl text-left text-xs transition border',
                              isCurrent ? 'bg-brand/10 border-brand/30 font-bold text-brand' : 'border-transparent hover:bg-paper text-slate-700'
                            )}
                          >
                            <div className="flex items-center gap-2.5 min-w-0">
                              <span className="text-base shrink-0">{acc.emoji}</span>
                              <div className="min-w-0">
                                <p className="leading-tight font-bold truncate">{acc.name}</p>
                                <p className="text-[10px] text-muted truncate">{acc.roleTitle} · {acc.role}</p>
                              </div>
                            </div>
                            {isCurrent && <span className="text-2xs font-bold text-brand ml-1 shrink-0 bg-brand/10 px-1.5 py-0.5 rounded-full">Active</span>}
                          </button>
                        );
                      })}
                    </div>
                  </div>
                </>
              )}
            </div>}

            <span className="grid h-9 w-9 place-items-center rounded-full bg-ink text-xs font-bold text-white shadow-xs" title={`${session.user.name} · ${session.user.role}`}>
              {initials}
            </span>
          </div>
        </header>
        <main className="mx-auto w-full max-w-6xl px-4 pt-5 pb-28 sm:px-6 lg:px-8 lg:pt-6 lg:pb-12">
          {can(session, 'admin') && <TrialBanner />}
          {oversees(session) && <OversightNote />}
          <ErrorBoundary resetKey={pathname}>
            <Suspense fallback={<div className="grid min-h-[40vh] place-items-center"><span className="h-6 w-6 animate-spin rounded-full border-2 border-line border-t-brand" aria-label="Loading" /></div>}><Outlet /></Suspense>
          </ErrorBoundary>
        </main>
      </div>

      {/* Phone tab bar */}
      <nav style={{ gridTemplateColumns: `repeat(${phoneTabs.length + 1}, minmax(0, 1fr))` }} className="fixed inset-x-0 bottom-0 z-30 grid border-t border-line bg-white pb-[env(safe-area-inset-bottom)] lg:hidden print:hidden" aria-label="Main">
        {phoneTabs.map((n) => (
          <NavLink key={n.to} to={n.to} end={n.end} className={({ isActive }) => cn('flex flex-col items-center gap-0.5 py-2 text-2xs font-semibold', isActive ? 'text-brand' : 'text-muted')}>
            <n.icon className="h-5 w-5" />
            {n.label}
          </NavLink>
        ))}
        <button onClick={() => setMore(true)} className="flex flex-col items-center gap-0.5 py-2 text-2xs font-semibold text-muted" aria-label="More sections">
          <Menu className="h-5 w-5" />
          More
        </button>
      </nav>

      <Sheet open={more} onClose={() => setMore(false)} title={session.company.name}>
        <button onClick={() => (setMore(false), setAssistant(true))} className="mb-4 flex w-full items-center gap-3 rounded-md bg-ink px-3 py-3 text-sm font-semibold text-white">
          <Sparkles className="h-[18px] w-[18px] text-gold" /> {t('Ask Msaidizi')}
        </button>
        {available.length > 1 && <WorkspaceSwitcher className="mb-3" workspace={workspace} available={available} onChoose={choose} />}
        {navList(true)}
        <MobileSignOutButton label={t('Sign out')} name={session.user.name} />
      </Sheet>

      <LiveNotices />
      <AssistantPanel open={assistant} onClose={() => setAssistant(false)} />
      <TellMe session={session} open={tellMe} onClose={() => setTellMe(false)} />
    </div>
  );
}

function SignOutButton({ label }: { label: string }) {
  return (
    <button onClick={() => setSession(null)} className="grid h-9 w-9 place-items-center rounded-full text-muted hover:bg-paper" aria-label={label} title={label}>
      <LogOut className="h-4 w-4" />
    </button>
  );
}

function MobileSignOutButton({ label, name }: { label: string; name: string }) {
  return (
    <button onClick={() => setSession(null)} className="mt-6 flex w-full items-center gap-3 rounded-md px-3 py-2 text-sm font-medium text-muted">
      <LogOut className="h-[18px] w-[18px]" /> {label} ({name})
    </button>
  );
}

/** Pick a module to work in, or all of them. Choosing one opens its home; the sidebar then holds only its pages. */
function WorkspaceSwitcher({ workspace, available, onChoose, className }: { workspace: Workspace; available: ModuleKey[]; onChoose: (w: Workspace) => void; className?: string }) {
  const [open, setOpen] = useState(false);
  const navigate = useNavigate();
  const M = workspace ? MODULES[workspace] : null;
  const pick = (w: Workspace) => {
    onChoose(w);
    setOpen(false);
    navigate(w ? `/w/${w}` : '/');
  };
  return (
    <div className={cn('relative', className)}>
      <button type="button" onClick={() => setOpen(!open)} aria-expanded={open} aria-haspopup="listbox"
        className="flex w-full items-center gap-2.5 rounded-lg px-2.5 py-2 text-left ring-1 ring-line transition hover:bg-paper"
        style={M ? { boxShadow: `inset 3px 0 0 ${M.color}` } : undefined}>
        <span className="grid h-7 w-7 shrink-0 place-items-center rounded-md bg-ink text-white" style={M ? { background: M.color } : undefined}>
          {M ? <M.icon className="h-4 w-4" /> : <LayoutGrid className="h-4 w-4" />}
        </span>
        <span className="min-w-0 flex-1 leading-tight">
          <span className="block text-2xs text-muted">Workspace</span>
          <span className="block text-sm leading-tight font-semibold">{M?.name ?? 'All modules'}</span>
        </span>
        <ChevronDown className={cn('h-4 w-4 shrink-0 text-faint transition', open && 'rotate-180')} />
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-40" onClick={() => setOpen(false)} />
          <ul role="listbox" aria-label="Workspaces" className="absolute inset-x-0 z-50 mt-1 rounded-lg bg-white p-1 shadow-xl ring-1 ring-line">
            {[null, ...available].map((w) => {
              const W = w ? MODULES[w] : null;
              const on = w === workspace;
              return (
                <li key={w ?? 'all'}>
                  <button type="button" role="option" aria-selected={on} onClick={() => pick(w)} className={cn('flex w-full items-center gap-2.5 rounded-md px-2 py-1.5 text-left text-sm', on ? 'bg-paper font-semibold' : 'hover:bg-paper')}>
                    <span className="grid h-6 w-6 shrink-0 place-items-center rounded-md bg-ink text-white" style={W ? { background: W.color } : undefined}>
                      {W ? <W.icon className="h-3.5 w-3.5" /> : <LayoutGrid className="h-3.5 w-3.5" />}
                    </span>
                    <span className="min-w-0 flex-1 truncate">{W?.name ?? 'All modules'}</span>
                    {on && <Check className="h-4 w-4 text-brand" />}
                  </button>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </div>
  );
}

/** For an owner who oversees: why the daily-work buttons are gone, once, until dismissed on this device. */
function OversightNote() {
  const [hidden, setHidden] = useState(() => { try { return localStorage.getItem('kerp-oversight-note') === '1'; } catch { return false; } });
  if (hidden) return null;
  return (
    <div className="mb-4 flex items-start gap-3 rounded-lg bg-indigo/5 px-4 py-3 text-sm ring-1 ring-indigo/15 print:hidden">
      <ShieldCheck className="mt-0.5 h-5 w-5 shrink-0 text-indigo" />
      <p className="min-w-0 flex-1 text-ink-2"><b className="text-ink">You’re overseeing.</b> You see every module and you approve and sign off; your team does the selling, buying, stock, jobs and payments, so those buttons are hidden for you. Change it in <NavLink to="/settings" className="font-semibold text-brand hover:underline">Settings → Business</NavLink>.</p>
      <button onClick={() => { setHidden(true); try { localStorage.setItem('kerp-oversight-note', '1'); } catch { /* shown again next time */ } }} className="text-sm font-semibold text-muted hover:text-ink">Got it</button>
    </div>
  );
}
