// The finance department's front page. The accountant is deciding "what needs paying, what needs chasing, and can I
// close the month?", so it leads with cash, what is owed both ways and this month's profit, then what KRA and the
// statutory bodies are due (amount and date), then the jobs standing between them and a clean month-end, each one a
// link to where it gets done.
import { Link } from 'react-router';
import { AlertTriangle, ArrowRight, BookOpen, CalendarRange, CheckCircle2, Landmark, Lock, PenLine } from 'lucide-react';
import { can, useApi, useSession } from '../lib/api';
import { cn, dayYear, daysBetween, ksh, todayIso } from '../lib/format';
import { ErrorNote, Loading, PageHeader, Stat } from '../components/ui';

type Overview = {
  asAt: string;
  cash: { accounts: { id: string; code: string; name: string; balance: number }[]; total: number };
  receivables: { total: number; overdue: number; overdueCount: number; dueThisWeek: number };
  payables: { total: number; overdue: number; overdueCount: number; dueThisWeek: number };
  month: { from: string; to: string; income: number; expenses: number; profit: number; previousProfit: number };
  statutory: { name: string; payTo: string; owed: number }[];
  deadlines: { what: string; payTo: string; due: string; amount: number; soFar: boolean; href: string }[];
  todo: { key: string; title: string; detail: string; amount: number | null; href: string }[];
  lockDate: string | null;
};

function DueIn({ date }: { date: string }) {
  const days = daysBetween(todayIso(), date);
  if (days < 0) return <span className="font-semibold text-bad">{-days} day{days === -1 ? '' : 's'} late</span>;
  if (days === 0) return <span className="font-semibold text-bad">Due today</span>;
  return <span className={cn(days <= 5 ? 'font-semibold text-warn' : 'text-muted')}>in {days} day{days === 1 ? '' : 's'}</span>;
}

export default function Finance() {
  const session = useSession();
  const { data, error } = useApi<Overview>('/finance/overview');
  const finance = can(session, 'finance');
  const monthName = data ? new Date(`${data.month.from}T00:00:00`).toLocaleDateString('en-GB', { month: 'long' }) : '';
  const change = data && data.month.previousProfit ? Math.round(((data.month.profit - data.month.previousProfit) / Math.abs(data.month.previousProfit)) * 100) : null;

  return (
    <>
      <PageHeader print
        title="Finance"
        subtitle="Cash, what’s owed both ways, what KRA is due, and what’s left before month-end."
        actions={
          <>
            {finance && <Link to="/finance/ledger?new=journal" className="btn btn-primary"><PenLine className="h-4 w-4" /> New journal</Link>}
            <Link to="/finance/ledger" className="btn btn-ghost"><BookOpen className="h-4 w-4" /> Accounts &amp; ledger</Link>
            <Link to="/reports/monthly" className="btn btn-ghost"><CalendarRange className="h-4 w-4" /> Monthly report</Link>
          </>
        }
      />
      {error ? <ErrorNote>{error}</ErrorNote> : !data ? <Loading /> : (
        <div className="space-y-5">
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Stat label="Cash and bank" value={ksh(data.cash.total)}>
              {data.cash.accounts.filter((a) => a.balance !== 0).map((a) => `${a.name} ${ksh(a.balance)}`).join(' · ') || 'M-Pesa, bank and cash'}
            </Stat>
            <Stat label="Customers owe you" value={ksh(data.receivables.total)} tone={data.receivables.overdue > 0 ? 'bad' : undefined}>
              {data.receivables.overdue > 0 ? <Link to="/invoices?status=overdue" className="font-semibold text-bad hover:underline">{ksh(data.receivables.overdue)} overdue ({data.receivables.overdueCount})</Link> : 'Nothing overdue'}
              {data.receivables.dueThisWeek > 0 && <> · {ksh(data.receivables.dueThisWeek)} due this week</>}
            </Stat>
            <Stat label="You owe suppliers" value={ksh(data.payables.total)}>
              {data.payables.overdue > 0 ? <Link to="/purchases" className="font-semibold text-bad hover:underline">{ksh(data.payables.overdue)} overdue ({data.payables.overdueCount})</Link> : 'Nothing overdue'}
              {data.payables.dueThisWeek > 0 && <> · {ksh(data.payables.dueThisWeek)} due this week</>}
            </Stat>
            <Stat label={`Profit in ${monthName} so far`} value={ksh(data.month.profit)} tone={data.month.profit < 0 ? 'bad' : 'good'}>
              Earned {ksh(data.month.income)} · spent {ksh(data.month.expenses)}
              {change !== null && <span className={cn('ml-1 font-semibold', change >= 0 ? 'text-good' : 'text-bad')}>{change >= 0 ? '▲' : '▼'} {Math.abs(change)}% on last month</span>}
            </Stat>
          </div>

          <div className="grid gap-5 lg:grid-cols-[1.2fr_1fr]">
            <section className="card p-4 sm:p-5" aria-labelledby="due-title">
              <h2 id="due-title" className="flex items-center gap-2 font-semibold"><Landmark className="h-4 w-4 text-muted" /> Due to KRA and others</h2>
              <ul className="mt-3 divide-y divide-line">
                {data.deadlines.map((d) => (
                  <li key={d.what} className="flex items-center gap-3 py-3">
                    <div className="min-w-0 flex-1">
                      <Link to={d.href} className="font-semibold hover:text-brand">{d.what}</Link>
                      <p className="text-xs text-muted">Pay to {d.payTo} · by {dayYear(d.due)} · <DueIn date={d.due} /></p>
                    </div>
                    <div className="text-right">
                      <b className="num block">{ksh(d.amount)}</b>
                      {d.soFar && <span className="text-xs text-muted">so far</span>}
                    </div>
                  </li>
                ))}
              </ul>
              {data.statutory.length > 0 && (
                <p className="mt-2 text-xs text-muted">
                  Held from pay and not yet paid over: {data.statutory.map((s) => `${s.name} ${ksh(s.owed)}`).join(' · ')}.
                </p>
              )}
            </section>

            <section className="card p-4 sm:p-5" aria-labelledby="close-title">
              <h2 id="close-title" className="flex items-center gap-2 font-semibold"><Lock className="h-4 w-4 text-muted" /> Before month-end</h2>
              <p className="mt-0.5 text-xs text-muted">{data.lockDate ? `Books closed up to ${dayYear(data.lockDate)}.` : 'No month has been closed yet.'}</p>
              {data.todo.length === 0 ? (
                <p className="mt-4 flex items-center gap-2 rounded-md bg-good-soft px-3 py-3 text-sm font-semibold text-good"><CheckCircle2 className="h-4 w-4" /> Nothing is holding up month-end.</p>
              ) : (
                <ul className="mt-3 space-y-2">
                  {data.todo.map((t) => (
                    <li key={t.key}>
                      <Link to={t.href} className="group flex items-start gap-3 rounded-md p-2.5 ring-1 ring-line hover:bg-paper">
                        {t.key === 'close' ? <Lock className="mt-0.5 h-4 w-4 shrink-0 text-brand" /> : <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-warn" />}
                        <span className="min-w-0 flex-1">
                          <b className="block text-sm">{t.title}{t.amount ? <span className="num font-normal text-muted"> · {ksh(t.amount)}</span> : null}</b>
                          <span className="text-xs text-muted">{t.detail}</span>
                        </span>
                        <ArrowRight className="mt-0.5 h-4 w-4 shrink-0 text-faint group-hover:text-brand" />
                      </Link>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        </div>
      )}
    </>
  );
}
