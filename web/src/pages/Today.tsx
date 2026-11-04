// The Role Center: what needs me today, and how is money moving? A one-line headline, the common actions,
// then cue tiles (a count per job, coloured by urgency, each opening the filtered list), then the numbers.
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Bot, CheckCircle2, ChevronDown, Lightbulb, MessageCircle, Plus, ShieldAlert } from 'lucide-react';
import { can, oversees, prefetch, useApi, useSession } from '../lib/api';
import { cn, day, daysBetween, ksh, msisdn, todayIso } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Loading, PageHeader, Stat, PageError } from '../components/ui';
import Proposals from '../components/Proposals';
import KpiBoard, { kpisPath } from '../components/KpiBoard';
import DashboardCharts, { CHARTS_PATH } from '../components/DashboardCharts';
import SetupChecklist, { SETUP_PATH } from '../components/SetupChecklist';

type TodayData = {
  today: string;
  // A section is null when this person's role doesn't cover it (the server leaves it out).
  moneyIn: { last7Days: number; previous7Days: number; todayAmount: number } | null;
  owed: { total: number; invoices: number } | null;
  overdue: { total: number; count: number; top: { id: string; number: string; customer: string; customerPhone: string; balance: number; daysLate: number }[] } | null;
  dueThisWeek: { total: number; count: number } | null;
  toMatch: { total: number; count: number } | null;
  cash: { mpesa: number; bank: number; cash: number } | null;
  vatOwed: number;
  drafts: number;
  etimsProblems: number;
  proposals: number;
  weOwe?: { total: number; count: number } | null;
  billsDue?: null | {
    total: number;
    count: number;
    overdue: number;
    top: { id: string; number: string; supplier: string; balance: number; dueDate: string }[];
  };
  lowStock?: { count: number; top: { id: string; name: string; onHand: number; reorderLevel: number }[] } | null;
  people?: { pendingLeave: number; away: { name: string; type: string; to: string }[]; statutoryDue: { total: number; soonest: string | null }; salariesToPay: number } | null;
  followUps?: { due: number; overdue: number } | null;
  cues?: { quotesOpen: number | null; openLeads: number | null; productionPlanned: number | null; ordersToReceive: number | null; draftPayroll: number | null };
  headline?: string | null;
  agentTasks?: number;
};

function greeting(t: ReturnType<typeof useLang>['t']) {
  const h = new Date().getHours();
  return h < 12 ? t('Good morning') : h < 17 ? t('Good afternoon') : t('Good evening');
}

function Change({ now, before }: { now: number; before: number }) {
  if (!before) return <>{now ? 'Nothing in the 7 days before' : 'No payments yet'}</>;
  const pct = Math.round(((now - before) / before) * 100);
  return <span className={pct >= 0 ? 'text-good' : 'text-bad'}>{pct >= 0 ? '▲' : '▼'} {Math.abs(pct)}% vs the 7 days before</span>;
}

type Cue = { label: string; count: number; to: string; tone?: 'bad' | 'warn'; note?: string };

/** A Role Center tile: how many, of what, how urgent; one tap opens the filtered list. */
function CueTile({ c }: { c: Cue }) {
  const quiet = c.count === 0;
  return (
    <Link
      to={c.to}
      className={cn(
        'group relative flex min-h-[5.5rem] flex-col justify-between overflow-hidden rounded-md bg-white p-3 pl-4 ring-1 ring-line transition hover:ring-brand',
        "before:absolute before:inset-y-0 before:left-0 before:w-1 before:content-['']",
        !quiet && c.tone === 'bad' && 'before:bg-bad',
        !quiet && c.tone === 'warn' && 'before:bg-gold',
        (quiet || !c.tone) && 'before:bg-line',
      )}
    >
      <span className={cn('num font-display text-[28px] leading-none font-semibold', quiet ? 'text-faint' : c.tone === 'bad' ? 'text-bad' : 'text-ink')}>{c.count}</span>
      <span className="mt-2 text-xs leading-tight font-semibold text-ink-2">{c.label}</span>
      {c.note && <span className="num text-xs text-muted">{c.note}</span>}
    </Link>
  );
}

export default function Today() {
  const session = useSession();
  const { t } = useLang();
  const { data: d, error, reload } = useApi<TodayData>('/today');
  // The panels below appear once this page's own answer is in; their data loads alongside it, not after it.
  useEffect(() => {
    if (can(session, 'admin')) prefetch(SETUP_PATH);
    if (can(session, 'reports')) prefetch(kpisPath('month', 0), CHARTS_PATH);
  }, [session]);
  if (error) return <PageError>{error}</PageError>;
  if (!d) return <Loading />;

  const statutoryLate = Boolean(d.people?.statutoryDue.soonest && d.people.statutoryDue.total > 0 && daysBetween(todayIso(), d.people.statutoryDue.soonest) < 0);
  const c = d.cues;
  const groups: { title: string; cues: (Cue | false | null | undefined)[] }[] = [
    {
      title: 'Sell',
      cues: [
        d.overdue && { label: 'Overdue invoices', count: d.overdue.count, to: '/invoices?status=overdue', tone: 'bad', note: d.overdue.count ? ksh(d.overdue.total) : undefined },
        d.owed && { label: 'Unpaid invoices', count: d.owed.invoices, to: '/invoices?status=unpaid', note: d.owed.invoices ? ksh(d.owed.total) : undefined },
        c?.quotesOpen != null && { label: 'Quotes awaiting a yes', count: c.quotesOpen, to: '/quotes' },
        d.followUps && { label: 'Follow-ups due', count: d.followUps.due, to: '/pipeline?view=follow-ups', tone: d.followUps.overdue > 0 ? 'bad' : 'warn', note: d.followUps.overdue ? `${d.followUps.overdue} late` : undefined },
        c?.openLeads != null && { label: 'Open leads', count: c.openLeads, to: '/pipeline' },
      ],
    },
    {
      title: 'Money',
      cues: [
        d.toMatch && can(session, 'finance') && { label: 'Payments to match', count: d.toMatch.count, to: '/money-in', tone: 'warn', note: d.toMatch.count ? ksh(d.toMatch.total) : undefined },
        can(session, 'sell') && { label: 'Drafts not sent', count: d.drafts, to: '/invoices?status=draft', tone: 'warn' },
        d.etimsProblems > 0 && { label: 'Not signed with eTIMS', count: d.etimsProblems, to: '/invoices?status=unpaid', tone: 'bad' },
        d.billsDue && { label: 'Bills due in 7 days', count: d.billsDue.count, to: '/purchases?kind=Bill&status=unpaid', tone: d.billsDue.overdue ? 'bad' : 'warn', note: d.billsDue.count ? `${ksh(d.billsDue.total)}${d.billsDue.overdue ? ` · ${d.billsDue.overdue} overdue` : ''}` : undefined },
      ],
    },
    {
      title: 'Operations',
      cues: [
        d.lowStock && { label: 'Low on stock', count: d.lowStock.count, to: '/stock?filter=low', tone: 'warn' },
        c?.ordersToReceive != null && { label: 'Orders to receive', count: c.ordersToReceive, to: '/purchases?kind=Order&status=sent' },
        c?.productionPlanned != null && { label: 'Production planned', count: c.productionPlanned, to: '/production' },
      ],
    },
    {
      title: 'People',
      cues: d.people
        ? [
            { label: 'Leave to decide', count: d.people.pendingLeave, to: '/leave', tone: 'warn' },
            { label: 'Away today', count: d.people.away.length, to: '/people?filter=on-leave' },
            { label: statutoryLate ? 'Statutory payments overdue' : 'Statutory payments due', count: d.people.statutoryDue.total > 0 ? 1 : 0, to: '/payroll', tone: statutoryLate ? 'bad' : 'warn', note: d.people.statutoryDue.total ? `${ksh(d.people.statutoryDue.total)}${d.people.statutoryDue.soonest ? ` ${statutoryLate ? 'since' : 'by'} ${day(d.people.statutoryDue.soonest)}` : ''}` : undefined },
            { label: 'Payroll drafts', count: c?.draftPayroll ?? 0, to: '/payroll' },
          ]
        : [],
    },
  ];
  const visible = groups.map((g) => ({ ...g, cues: g.cues.filter(Boolean) as Cue[] })).filter((g) => g.cues.length);
  // Nothing to do anywhere (a new business, or a quiet day): one calm line instead of a wall of zeros.
  const allClear = visible.every((g) => g.cues.every((q) => q.count === 0));
  // No money has moved yet: the money panels and charts would only say zero, so they wait for the first sale.
  const noMoneyYet = !d.moneyIn?.last7Days && !d.moneyIn?.previous7Days && !d.owed?.total && !(d.cash && (d.cash.mpesa || d.cash.bank || d.cash.cash)) && !d.weOwe?.total;
  // An owner who oversees gets the decisions and the overviews; the team gets the daily work.
  const actions = oversees(session) ? [
    ['Approvals register', '/finance/approvals'],
    ['Budgets', '/finance/budgets'],
    ['Requests to decide', '/procurement'],
    ['Business OS', '/os'],
    ['Monthly report', '/reports/monthly'],
    ['Monthly plan', '/os?tab=plan'],
  ] as [string, string, boolean?][] : [
    can(session, 'sell') && ['New invoice', '/invoices/new', true],
    can(session, 'sell') && ['Record payment', '/money-in?new=1'],
    can(session, 'sell') && ['New quote', '/quotes/new'],
    can(session, 'pos') && ['Open the till', '/pos'],
    can(session, 'crm') && ['New lead', '/pipeline?new=1'],
    can(session, 'buy') && ['New purchase order', '/purchases/new?kind=Order'],
    can(session, 'make') && ['Plan production', '/production'],
    can(session, 'finance') && ['New bill', '/purchases/new?kind=Bill'],
    can(session, 'stock') && ['Count stock', '/stock?count=1'],
    can(session, 'people') && ['Leave requests', '/leave'],
    can(session, 'people') && ['Payroll', '/payroll'],
  ].filter(Boolean) as [string, string, boolean?][];

  return (
    <div>
      <PageHeader
        title={`${greeting(t)}, ${session?.user.name.split(' ')[0]}`}
        subtitle={new Date(`${d.today}T00:00:00`).toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' })}
        actions={actions.length > 0 && <>
          {actions.slice(0, 5).map(([label, to, primary]) => (
            <Link key={to} to={to} className={primary ? 'btn btn-primary' : 'btn btn-ghost'}>
              {primary && <Plus className="h-4 w-4" />} {label}
            </Link>
          ))}
          {actions.length > 5 && <MoreActions items={actions.slice(5)} />}
        </>}
      />
      <div className="space-y-5">
        {can(session, 'admin') && <SetupChecklist />}

        {d.headline && (
          <p className="flex items-start gap-3 rounded-md bg-brand-soft px-4 py-3 text-sm text-ink">
            <Lightbulb className="mt-0.5 h-5 w-5 shrink-0 text-brand" /> {d.headline}
          </p>
        )}

        {(session?.user.role === 'Owner' || session?.user.role === 'Accountant') && !session.user.twoFactorEnabled && (
          <Link to="/security" className="flex items-center gap-3 rounded-md bg-warn-soft px-4 py-3 text-warn hover:brightness-95">
            <ShieldAlert className="h-5 w-5 shrink-0" />
            <span className="flex-1 text-sm">
              <b>Protect the books:</b> a stolen password is all it takes to sign in as you. Turn on two-step sign-in (two minutes).
            </span>
            <span className="text-sm font-semibold">Turn on →</span>
          </Link>
        )}

        {(d.agentTasks ?? 0) > 0 && (
          <Link to="/agents" className="flex items-center gap-3 rounded-md bg-indigo px-4 py-3 text-white hover:brightness-110">
            <Bot className="h-5 w-5 shrink-0 text-gold" />
            <span className="flex-1 text-sm">
              Your agents have <b>{d.agentTasks}</b> thing{d.agentTasks === 1 ? '' : 's'} for you to check
            </span>
            <span className="text-sm font-semibold">Review →</span>
          </Link>
        )}

        {d.proposals > 0 && <Proposals onChange={reload} />}

        {allClear && (
          <p className="flex items-center gap-2 rounded-md bg-white px-4 py-3 text-sm text-muted ring-1 ring-line">
            <CheckCircle2 className="h-5 w-5 shrink-0 text-good" /> Nothing waiting for you today: no overdue invoices, payments to match or approvals.
          </p>
        )}

        <div className={cn('grid gap-5 xl:grid-cols-2', allClear && 'hidden')}>
          {visible.map((g) => (
            <section key={g.title} aria-label={g.title}>
              <h2 className="mb-2 text-2xs font-semibold tracking-wider text-faint uppercase">{g.title}</h2>
              <div className="grid grid-cols-[repeat(auto-fit,minmax(8.5rem,1fr))] gap-2">
                {g.cues.map((c) => (
                  <CueTile key={c.label} c={c} />
                ))}
              </div>
            </section>
          ))}
        </div>

        {d.people && d.people.away.length > 0 && (
          <p className="text-sm text-muted">
            Away today: {d.people.away.map((a) => `${a.name} (${a.type.toLowerCase()} leave to ${day(a.to)})`).join(', ')}
          </p>
        )}

        {d.moneyIn && d.owed && d.overdue && d.dueThisWeek && !noMoneyYet && (
          <>
            <h2 className="pt-2 text-2xs font-semibold tracking-wider text-faint uppercase">How money is moving</h2>
            <div className="-mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
              <Stat label={t('Money in, last 7 days')} value={ksh(d.moneyIn.last7Days)}>
                <Change now={d.moneyIn.last7Days} before={d.moneyIn.previous7Days} />
              </Stat>
              <Stat label={t('Customers owe you')} value={ksh(d.owed.total)}>
                {d.owed.invoices} unpaid invoice{d.owed.invoices === 1 ? '' : 's'}
                {d.overdue.total > 0 && <span className="text-bad"> · {ksh(d.overdue.total)} late</span>}
              </Stat>
              {d.cash ? (
                <Stat label="Money you hold" value={ksh(d.cash.mpesa + d.cash.bank + d.cash.cash)} tone={d.cash.mpesa + d.cash.bank + d.cash.cash < 0 ? 'bad' : undefined}>
                  M-Pesa {ksh(d.cash.mpesa)} · Bank {ksh(d.cash.bank)} · Cash {ksh(d.cash.cash)}
                  {(d.cash.mpesa < 0 || d.cash.bank < 0 || d.cash.cash < 0) && <span className="block text-bad">Below zero: more was paid out than recorded coming in.</span>}
                </Stat>
              ) : (
                <Stat label="Paid today" value={ksh(d.moneyIn.todayAmount)} />
              )}
              {d.weOwe ? (
                <Stat label={t('We owe suppliers')} value={ksh(d.weOwe.total)}>
                  {d.weOwe.count} unpaid bill{d.weOwe.count === 1 ? '' : 's'}
                </Stat>
              ) : (
                <Stat label="Due in the next 7 days" value={ksh(d.dueThisWeek.total)}>
                  {d.dueThisWeek.count} invoice{d.dueThisWeek.count === 1 ? '' : 's'}
                </Stat>
              )}
            </div>
          </>
        )}

      {d.overdue && d.overdue.top.length > 0 && (
        <section className="card p-4 sm:p-5">
          <div className="flex items-baseline justify-between gap-3">
            <h2 className="text-sm font-semibold text-muted">Longest overdue</h2>
            <Link to="/invoices?status=overdue" className="text-sm font-semibold text-brand hover:underline">
              See all {d.overdue.count}
            </Link>
          </div>
          <ul className="mt-2 divide-y divide-line">
            {d.overdue.top.map((i) => {
              const wa = msisdn(i.customerPhone);
              const text = `Hello ${i.customer}, a reminder that invoice ${i.number} for ${ksh(i.balance)} was due ${i.daysLate} day${i.daysLate === 1 ? '' : 's'} ago. You can pay by M-Pesa using account number ${i.number}. Thank you.`;
              return (
                <li key={i.id} className="flex items-center gap-3 py-3">
                  <Link to={`/sales/${i.id}`} className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{i.customer}</b>
                    <span className="text-sm text-muted">
                      {i.number} · <span className="text-bad">{i.daysLate} day{i.daysLate === 1 ? '' : 's'} late</span>
                    </span>
                  </Link>
                  <span className="num shrink-0 text-right font-semibold">{ksh(i.balance)}</span>
                  {wa && (
                    <a href={`https://wa.me/${wa}?text=${encodeURIComponent(text)}`} target="_blank" rel="noreferrer" className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-mpesa text-white" aria-label={`Remind ${i.customer} on WhatsApp`} title="Remind on WhatsApp">
                      <MessageCircle className="h-4 w-4" />
                    </a>
                  )}
                </li>
              );
            })}
          </ul>
        </section>
      )}

      {d.vatOwed > 0 && (
        <p className="text-sm text-muted">
          VAT collected so far (owed to KRA): <b className="num text-ink">{ksh(d.vatOwed)}</b> ·{' '}
          <Link to="/reports" className="font-semibold text-brand hover:underline">
            VAT report
          </Link>
        </p>
      )}

      {/* How the business is doing against its targets, for whoever reads the reports (owner, admin, accountant). */}
      {can(session, 'reports') && !noMoneyYet && (
        <div className="space-y-6 border-t border-line pt-5">
          <KpiBoard />
          <DashboardCharts />
        </div>
      )}
      </div>
    </div>
  );
}


/** The rest of the day's actions, one tap away, so the action bar stays one tidy row. */
function MoreActions({ items }: { items: [string, string, boolean?][] }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="relative">
      <button type="button" className="btn btn-ghost" onClick={() => setOpen(!open)} aria-expanded={open} aria-haspopup="menu">
        More <ChevronDown className={cn('h-4 w-4 transition', open && 'rotate-180')} />
      </button>
      {open && (
        <>
          <div className="fixed inset-0 z-40" onClick={() => setOpen(false)} />
          <div role="menu" className="fixed inset-x-4 top-36 z-50 rounded-lg bg-white p-1 shadow-xl ring-1 ring-line lg:absolute lg:inset-x-auto lg:top-auto lg:left-0 lg:mt-1 lg:w-56">
            {items.map(([label, to]) => (
              <Link key={to} to={to} role="menuitem" onClick={() => setOpen(false)} className="block rounded-md px-3 py-2 text-sm font-medium text-ink-2 hover:bg-paper hover:text-ink">{label}</Link>
            ))}
          </div>
        </>
      )}
    </div>
  );
}
