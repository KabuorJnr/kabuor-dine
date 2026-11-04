// Dashboards for a big screen: the office TV, a projector at the Monday meeting, a second monitor. Each board
// answers one question from live figures (the KPI scorecard, the ledger, stock and deliveries), every number with
// its context: against target and pace, against the same days last month, and its six-month trend. "Present" fills
// the screen, refreshes every minute, and can rotate through the boards on its own.
import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { ArrowDownRight, ArrowUpRight, ChevronLeft, ChevronRight, Maximize2, Minimize2, Pause, Play, RefreshCw } from 'lucide-react';
import { can, useApi, useSession } from '../lib/api';
import { cn, dayYear, ksh } from '../lib/format';
import { Chips, ErrorNote, Loading, PageHeader } from '../components/ui';
import { fmt, Spark, STATUS, type Card, type Kpi } from '../components/KpiBoard';
import { Columns, compact, HBars, Legend, money, monthName, SERIES, type Charts } from '../components/DashboardCharts';

type Board = 'business' | 'sales' | 'cash' | 'operations';
const BOARDS: [Board, string, string][] = [
  ['business', 'Business', 'How the month is going against its targets'],
  ['sales', 'Sales', 'What sells, who buys, and where they come from'],
  ['cash', 'Cash', 'Money in and out, who owes, and what’s due'],
  ['operations', 'Operations', 'Stock on the shelf and goods on the road'],
];
type Finance = {
  cash: { accounts: { name: string; balance: number }[]; total: number };
  receivables: { total: number; overdue: number; overdueCount: number; dueThisWeek: number };
  payables: { total: number; overdue: number; overdueCount: number; dueThisWeek: number };
  month: { from: string; profit: number; previousProfit: number; income: number; expenses: number };
  deadlines: { what: string; payTo: string; due: string; amount: number; soFar: boolean }[];
};
type Stock = { counts: { all: number; low: number; out: number }; totalValue: number };
type Trips = { all: number; draft: number; dispatched: number; completed: number };
type Fleet = { counts: { all: number; available: number; out: number; repair: number } };

const REFRESH = 60_000;
const ROTATE = 30_000;

/** A KPI as a large tile: value, status in words, progress against target (for "more is better" measures), the
 * change on the same days last period, and six periods of trend. */
const VALUE = 'num font-display text-[clamp(1.25rem,1.7vw,1.9rem)] leading-tight font-semibold tracking-tight whitespace-nowrap overflow-hidden text-ellipsis';
const TILE = 'card relative flex min-w-0 flex-col gap-1.5 overflow-hidden p-4 transition hover:ring-faint';

/** A KPI as a dashboard tile: a status stripe, the figure, its trend, and how it stands against target and last month. */
function KpiTile({ k }: { k?: Kpi }) {
  if (!k) return null;
  const [label, tone] = k.value == null && k.target != null ? ['No data yet', STATUS.none[1]] : STATUS[k.status];
  const better = k.change == null ? null : k.higherIsBetter ? k.change > 0 : k.change < 0;
  const goal = k.dueByNow ?? k.target;
  const share = k.higherIsBetter && goal && k.value != null ? Math.max(0, Math.min(1, k.value / goal)) : null;
  const stripe = k.value == null || k.target == null ? 'bg-line' : k.status === 'met' ? 'bg-good' : k.status === 'close' ? 'bg-warn' : 'bg-bad';
  return (
    <Link to={k.link} className={TILE} title={k.explain}>
      <span className={cn('absolute inset-x-0 top-0 h-1', stripe)} aria-hidden />
      <span className="flex items-start justify-between gap-2">
        <span className="truncate text-2xs font-semibold tracking-wider text-muted uppercase">{k.name}</span>
        <span className={cn('shrink-0 rounded-full px-2 py-px text-2xs font-semibold', tone)}>{label}</span>
      </span>
      <span className="flex items-end justify-between gap-3">
        <span className={VALUE}>{fmt(k, k.value)}</span>
        <span className="w-20 shrink-0"><Spark points={k.trend.map((t) => t.value)} good={k.status === 'met'} /></span>
      </span>
      {share != null && (
        <span className="block h-1.5 overflow-hidden rounded-full bg-paper ring-1 ring-line" role="meter" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(share * 100)} aria-label={`${k.name} against ${k.dueByNow != null ? 'where it should be by today' : 'target'}`}>
          <span className={cn('block h-full rounded-full', k.status === 'met' ? 'bg-good' : k.status === 'close' ? 'bg-warn' : 'bg-bad')} style={{ width: `${share * 100}%` }} />
        </span>
      )}
      <span className="flex flex-wrap items-center justify-between gap-x-3 text-xs text-muted">
        <span>{k.target != null ? (k.dueByNow != null ? <>{fmt(k, k.dueByNow)} by today</> : <>Target {k.higherIsBetter ? '≥' : '≤'} {fmt(k, k.target)}</>) : 'No target'}</span>
        {k.change != null ? (
          <span className={cn('inline-flex items-center gap-0.5 font-semibold', better ? 'text-good' : 'text-bad')}>
            {k.change >= 0 ? <ArrowUpRight className="h-3.5 w-3.5" /> : <ArrowDownRight className="h-3.5 w-3.5" />}{Math.abs(k.change)}% vs last month
          </span>
        ) : k.projected != null ? <span>On pace for {fmt(k, k.projected)}</span> : null}
      </span>
    </Link>
  );
}

/** A figure that isn't a KPI (cash, what's owed), with what it's made of underneath. */
function Figure({ label, value, tone, to, children }: { label: string; value: string; tone?: 'bad' | 'good'; to?: string; children?: ReactNode }) {
  const body = (
    <>
      <span className={cn('absolute inset-x-0 top-0 h-1', tone === 'bad' ? 'bg-bad' : tone === 'good' ? 'bg-good' : 'bg-brand')} aria-hidden />
      <span className="truncate text-2xs font-semibold tracking-wider text-muted uppercase">{label}</span>
      <span className={cn(VALUE, tone === 'bad' && 'text-bad', tone === 'good' && 'text-good')}>{value}</span>
      {children && <span className="line-clamp-2 text-xs text-muted">{children}</span>}
    </>
  );
  return to ? <Link to={to} className={TILE}>{body}</Link> : <div className={TILE}>{body}</div>;
}

function Panel({ title, answer, children, legend }: { title: string; answer?: ReactNode; children: ReactNode; legend?: ReactNode }) {
  return (
    <section className="card flex min-w-0 flex-col gap-3 p-4">
      <div>
        <h3 className="text-2xs font-semibold tracking-wider text-muted uppercase">{title}</h3>
        {answer && <p className="font-semibold">{answer}</p>}
      </div>
      {legend}
      {children}
    </section>
  );
}

export default function Dashboards() {
  const session = useSession();
  const stockView = can(session, 'stock.view');
  const [board, setBoard] = useState<Board>(() => {
    try { return (localStorage.getItem('kerp-board') as Board) || 'business'; } catch { return 'business'; }
  });
  const [present, setPresent] = useState(false);
  const [rotate, setRotate] = useState(false);
  const [updated, setUpdated] = useState(() => new Date());
  const [now, setNow] = useState(() => new Date());
  const frame = useRef<HTMLDivElement>(null);

  const kpis = useApi<Card>('/insights/kpis?period=month');
  const charts = useApi<Charts>('/insights/charts?months=12');
  const finance = useApi<Finance>('/finance/overview');
  const stock = useApi<Stock>(stockView ? '/stock?filter=all&q=' : null);
  const trips = useApi<Trips>(stockView ? '/dispatch/counts' : null);
  const fleet = useApi<Fleet>(stockView ? '/dispatch/fleet' : null);

  const choose = useCallback((b: Board) => {
    setBoard(b);
    try { localStorage.setItem('kerp-board', b); } catch {}
  }, []);
  const step = useCallback((d: number) => setBoard((b) => {
    const i = (BOARDS.findIndex(([id]) => id === b) + d + BOARDS.length) % BOARDS.length;
    return BOARDS[i][0];
  }), []);
  const refresh = () => {
    kpis.reload(); charts.reload(); finance.reload(); stock.reload(); trips.reload(); fleet.reload();
    setUpdated(new Date());
  };
  // The timers start once and call the latest refresh: the clock re-renders every 15 seconds, which would
  // otherwise restart a one-minute timer before it ever fired.
  const latest = useRef(refresh);
  latest.current = refresh;

  // Live figures: every minute while the page is open, and a clock for the wall screen.
  useEffect(() => {
    const r = setInterval(() => latest.current(), REFRESH);
    const c = setInterval(() => setNow(new Date()), 15_000);
    return () => (clearInterval(r), clearInterval(c));
  }, []);
  useEffect(() => {
    if (!rotate) return;
    const t = setInterval(() => step(1), ROTATE);
    return () => clearInterval(t);
  }, [rotate, step]);
  // Arrow keys move between boards; F toggles presenting; leaving full screen ends presenting.
  useEffect(() => {
    const on = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLElement && ['INPUT', 'TEXTAREA', 'SELECT'].includes(e.target.tagName)) return;
      if (e.key === 'ArrowRight') step(1);
      if (e.key === 'ArrowLeft') step(-1);
      if (e.key.toLowerCase() === 'f') setPresent((p) => !p);
    };
    const exit = () => !document.fullscreenElement && setPresent(false);
    window.addEventListener('keydown', on);
    document.addEventListener('fullscreenchange', exit);
    return () => (window.removeEventListener('keydown', on), document.removeEventListener('fullscreenchange', exit));
  }, [step]);
  useEffect(() => {
    if (present) frame.current?.requestFullscreen?.().catch(() => {});
    else if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
  }, [present]);

  const k = (key: string) => kpis.data?.kpis.find((x) => x.key === key);
  const c = charts.data;
  const f = finance.data;
  const m = c ? c.monthly.slice(Math.max(0, c.monthly.findIndex((x) => x.sales || x.moneyIn || x.moneyOut))) : [];
  const labels = m.map((x) => monthName(x.month));
  const channelTotal = c ? c.channels.reduce((s, x) => s + x.sales, 0) : 0;
  const [, title, question] = BOARDS.find(([id]) => id === board)!;
  const profitChange = f && f.month.previousProfit ? Math.round(((f.month.profit - f.month.previousProfit) / Math.abs(f.month.previousProfit)) * 100) : null;

  const salesChart = c && m.length > 0 && (
    <Panel title="Sales by month (before VAT)" answer={c.salesTarget ? <>{m.slice(0, -1).filter((x) => x.sales >= c.salesTarget!).length} of {m.length - 1} finished months on target</> : <>Best month: {(() => { const b = [...m].sort((a, z) => z.sales - a.sales)[0]; return `${monthName(b.month, true)}, ${money(b.sales)}`; })()}</>}>
      <Columns height={present ? 300 : 240} labels={labels} series={[{ name: 'Sales', color: SERIES[0], values: m.map((x) => x.sales) }]} reference={c.salesTarget ? { value: c.salesTarget, label: `Target ${compact(c.salesTarget)}` } : null}
        tip={(i) => <><b className="block text-ink">{monthName(m[i].month, true)}</b><span className="flex justify-between"><span className="text-muted">Sales</span><b className="num">{money(m[i].sales)}</b></span><span className="flex justify-between"><span className="text-muted">Gross profit</span><span className="num">{money(m[i].grossProfit)}</span></span><span className="flex justify-between"><span className="text-muted">Invoices</span><span className="num">{m[i].invoices}</span></span></>} />
    </Panel>
  );
  const noCash = c && m.length > 0 && m.every((x) => !x.moneyIn && !x.moneyOut);
  const cashChart = noCash ? (
    <Panel title="Money in and money out"><p className="py-10 text-center text-sm text-muted">No customer payments or supplier payments recorded yet. They show here month by month as they come in and go out.</p></Panel>
  ) : c && m.length > 0 && (
    <Panel title="Money in and money out" answer={(() => { const net = m.reduce((s, x) => s + x.moneyIn - x.moneyOut, 0); return <span className={net < 0 ? 'text-bad' : 'text-good'}>{net < 0 ? '−' : '+'}{money(Math.abs(net))} net since {monthName(m[0].month, true)}</span>; })()}
      legend={<Legend items={[['Money in (customer payments)', SERIES[0]], ['Money out (suppliers and pay)', SERIES[1]]]} />}>
      <Columns height={present ? 300 : 240} labels={labels} series={[{ name: 'Money in', color: SERIES[0], values: m.map((x) => x.moneyIn) }, { name: 'Money out', color: SERIES[1], values: m.map((x) => x.moneyOut) }]}
        tip={(i) => <><b className="block text-ink">{monthName(m[i].month, true)}</b><span className="flex justify-between"><span className="text-muted">In</span><b className="num">{money(m[i].moneyIn)}</b></span><span className="flex justify-between"><span className="text-muted">Out</span><b className="num">{money(m[i].moneyOut)}</b></span></>} />
    </Panel>
  );

  const body = (
    <>
      {(kpis.error || charts.error) && <ErrorNote>{kpis.error || charts.error}</ErrorNote>}
      {!kpis.data || !c ? <Loading /> : (
        <div className="space-y-3">
          {board === 'business' && (
            <>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
                <KpiTile k={k('sales')} />
                {f && <Figure label={`Profit in ${monthName(f.month.from)} so far`} value={ksh(f.month.profit)} tone={f.month.profit < 0 ? 'bad' : undefined} to="/reports">
                  Earned {ksh(f.month.income)} · spent {ksh(f.month.expenses)}{profitChange != null && <> · <b className={profitChange >= 0 ? 'text-good' : 'text-bad'}>{profitChange >= 0 ? '▲' : '▼'} {Math.abs(profitChange)}%</b> on last month</>}
                </Figure>}
                <KpiTile k={k('margin')} />
                <KpiTile k={k('collected')} />
              </div>
              <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
                {f && <Figure label="Cash and bank" value={ksh(f.cash.total)} to="/finance">{f.cash.accounts.filter((a) => a.balance).map((a) => `${a.name} ${ksh(a.balance)}`).join(' · ') || 'M-Pesa, bank and cash'}</Figure>}
                {f && <Figure label="Customers owe you" value={ksh(f.receivables.total)} tone={f.receivables.overdue > 0 ? 'bad' : undefined} to="/invoices?status=overdue">{f.receivables.overdue > 0 ? `${ksh(f.receivables.overdue)} overdue on ${f.receivables.overdueCount} invoice${f.receivables.overdueCount === 1 ? '' : 's'}` : 'Nothing overdue'}</Figure>}
                <KpiTile k={k('dso')} />
                <KpiTile k={k('newCustomers')} />
              </div>
              <div className="grid gap-4 xl:grid-cols-2">{salesChart}{cashChart}</div>
            </>
          )}
          {board === 'sales' && (
            <>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
                {['sales', 'avgInvoice', 'newCustomers', 'repeat', 'conversion', 'pipeline'].map((key) => <KpiTile key={key} k={k(key)} />)}
              </div>
              <div className="grid gap-4 xl:grid-cols-2">
                {salesChart}
                <Panel title="Best sellers (last 90 days)" answer={c.products.length ? <>Top 3 make {channelTotal ? Math.round((100 * c.products.slice(0, 3).reduce((s, p) => s + p.sales, 0)) / channelTotal) : 0}% of sales</> : 'Nothing sold in the last 90 days'}>
                  <HBars rows={c.products.map((p) => ({ label: p.name, value: p.sales, note: p.sales && p.grossProfit === p.sales ? 'cost not recorded' : `${p.sales ? Math.round((100 * p.grossProfit) / p.sales) : 0}% margin` }))} value={money} />
                </Panel>
                <Panel title="Where sales come from (last 90 days)" answer={c.channels[0] ? <>{c.channels[0].channel}: {channelTotal ? Math.round((100 * c.channels[0].sales) / channelTotal) : 0}% of sales</> : 'No sales in the last 90 days'}>
                  <HBars rows={c.channels.slice(0, 7).map((x) => ({ label: x.channel, value: x.sales, note: `${x.invoices} invoice${x.invoices === 1 ? '' : 's'}` }))} value={money} />
                </Panel>
                <Panel title="From inquiry to sale (last 90 days)" answer={c.funnel[1]?.count ? <>{Math.round((100 * (c.funnel[c.funnel.length - 1]?.count ?? 0)) / c.funnel[1].count)}% of inquiries bought</> : 'No inquiries in the last 90 days'}>
                  <HBars rows={c.funnel.map((s) => ({ label: s.step, value: s.count }))} value={(v) => v.toLocaleString('en-KE')} />
                </Panel>
              </div>
            </>
          )}
          {board === 'cash' && (
            <>
              <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
                {f && <Figure label="Cash and bank" value={ksh(f.cash.total)} to="/finance">{f.cash.accounts.filter((a) => a.balance).map((a) => `${a.name} ${ksh(a.balance)}`).join(' · ') || 'M-Pesa, bank and cash'}</Figure>}
                {f && <Figure label="Customers owe you" value={ksh(f.receivables.total)} tone={f.receivables.overdue > 0 ? 'bad' : undefined} to="/invoices?status=overdue">{f.receivables.overdue > 0 ? `${ksh(f.receivables.overdue)} overdue` : 'Nothing overdue'}{f.receivables.dueThisWeek > 0 && ` · ${ksh(f.receivables.dueThisWeek)} due this week`}</Figure>}
                {f && <Figure label="You owe suppliers" value={ksh(f.payables.total)} tone={f.payables.overdue > 0 ? 'bad' : undefined} to="/purchases">{f.payables.overdue > 0 ? `${ksh(f.payables.overdue)} overdue` : 'Nothing overdue'}{f.payables.dueThisWeek > 0 && ` · ${ksh(f.payables.dueThisWeek)} due this week`}</Figure>}
                <KpiTile k={k('collected')} />
                <KpiTile k={k('dso')} />
                <KpiTile k={k('overdue')} />
              </div>
              <div className="grid gap-4 xl:grid-cols-2">
                {cashChart}
                <Panel title="What customers owe, by how late" answer={c.owed === 0 ? 'Nobody owes you anything' : <>{money(c.owed)} owed</>}>
                  <HBars rows={c.ageing.map((a) => ({ label: a.bucket, value: a.amount, note: `${a.invoices} invoice${a.invoices === 1 ? '' : 's'}` }))} value={money} />
                </Panel>
              </div>
              {f && f.deadlines.length > 0 && (
                <Panel title="Due to KRA and others">
                  <ul className="grid gap-3 sm:grid-cols-2 xl:grid-cols-3">
                    {f.deadlines.map((d) => (
                      <li key={d.what} className="rounded-md p-3 ring-1 ring-line">
                        <b className="block">{d.what}</b>
                        <span className="text-sm text-muted">{d.payTo} · by {dayYear(d.due)}</span>
                        <b className="num mt-1 block text-xl">{ksh(d.amount)}{d.soFar && <span className="ml-1 text-sm font-normal text-muted">so far</span>}</b>
                      </li>
                    ))}
                  </ul>
                </Panel>
              )}
            </>
          )}
          {board === 'operations' && (
            !stockView ? <p className="card p-6 text-center text-muted">Stock and deliveries aren’t part of your role.</p> : (
              <>
                <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-4">
                  {stock.data && <Figure label="Stock at cost" value={ksh(stock.data.totalValue)} to="/stock">{stock.data.counts.all} products tracked</Figure>}
                  {stock.data && <Figure label="Running low" value={String(stock.data.counts.low)} tone={stock.data.counts.low > 0 ? 'bad' : 'good'} to="/stock?filter=low">{stock.data.counts.out > 0 ? `${stock.data.counts.out} out of stock` : 'Nothing out of stock'}</Figure>}
                  {trips.data && <Figure label="Trips on the road" value={String(trips.data.dispatched)} to="/fleet">{trips.data.draft} planned, not sent · {trips.data.completed} completed</Figure>}
                  {fleet.data && <Figure label="Vehicles free" value={`${fleet.data.counts.available} of ${fleet.data.counts.all}`} tone={fleet.data.counts.repair > 0 ? 'bad' : undefined} to="/fleet?view=vehicles">{fleet.data.counts.out} out · {fleet.data.counts.repair} under repair</Figure>}
                </div>
                <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
                  {['stockDays', 'stockouts', 'accuracy'].map((key) => <KpiTile key={key} k={k(key)} />)}
                </div>
                <Panel title="Best sellers (last 90 days)" answer="Keep these on the shelf">
                  <HBars rows={c.products.map((p) => ({ label: p.name, value: p.quantity, note: money(p.sales) }))} value={(v) => v.toLocaleString('en-KE')} />
                </Panel>
              </>
            )
          )}
        </div>
      )}
    </>
  );

  const controls = (
    <div className="flex flex-wrap items-center gap-2">
      <button className="btn btn-ghost px-2.5" onClick={() => step(-1)} aria-label="Previous board"><ChevronLeft className="h-4 w-4" /></button>
      <button className="btn btn-ghost px-2.5" onClick={() => step(1)} aria-label="Next board"><ChevronRight className="h-4 w-4" /></button>
      <button className={cn('btn', rotate ? 'btn-primary' : 'btn-ghost')} onClick={() => setRotate((r) => !r)} aria-pressed={rotate} title="Move to the next board every 30 seconds">
        {rotate ? <Pause className="h-4 w-4" /> : <Play className="h-4 w-4" />} Rotate
      </button>
      <button className="btn btn-ghost" onClick={refresh} title="Fetch the latest figures now"><RefreshCw className="h-4 w-4" /> Refresh</button>
      <button className={cn('btn', present ? 'btn-ghost' : 'btn-primary')} onClick={() => setPresent((p) => !p)}>
        {present ? <><Minimize2 className="h-4 w-4" /> Exit</> : <><Maximize2 className="h-4 w-4" /> Present</>}
      </button>
    </div>
  );
  const status = `Updated ${updated.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })} · refreshes every minute`;

  if (present)
    return (
      <div ref={frame} className="fixed inset-0 z-50 overflow-y-auto bg-paper p-5 sm:p-8 lg:[zoom:1.1]">
        <header className="mb-5 flex flex-wrap items-end justify-between gap-4">
          <div>
            <p className="text-sm font-semibold text-muted">{session?.company.name} · {now.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long' })}</p>
            <h1 className="font-display text-4xl font-semibold tracking-tight">{title}</h1>
            <p className="text-muted">{question}</p>
          </div>
          <div className="text-right">
            <p className="num font-display text-4xl font-semibold">{now.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}</p>
            <p className="text-sm text-muted">{status}</p>
          </div>
        </header>
        <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
          <div className="flex gap-1.5" role="tablist" aria-label="Boards">
            {BOARDS.map(([id, label]) => (
              <button key={id} role="tab" aria-selected={board === id} onClick={() => choose(id)} className={cn('rounded-full px-3.5 py-1.5 text-sm font-semibold', board === id ? 'bg-brand-soft text-brand' : 'text-muted ring-1 ring-line hover:text-ink')}>{label}</button>
            ))}
          </div>
          {controls}
        </div>
        {body}
      </div>
    );

  return (
    <>
      <PageHeader print title="Dashboards" subtitle={`${question}. Live from your books: ${status.toLowerCase()}.`} actions={controls} />
      <div className="mb-4">
        <Chips label="Board" value={board} onChange={choose} options={BOARDS.map(([id, label]) => [id, label] as [Board, string])} />
      </div>
      <div ref={frame}>{body}</div>
    </>
  );
}
