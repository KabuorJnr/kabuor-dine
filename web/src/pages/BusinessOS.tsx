// Business OS: the five core modules as one system. The tiles say how each module is doing; the chain reads the business
// link by link (assets → maintenance → parts and tools → people → customers → marketing → money → decisions), each from
// the module that owns it, in the words of the business's industry; foresight says what the business's own data
// expects next, with how accurate each model has been.
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import Intelligence from './Intelligence';
import Planning from './Planning';
import HealthPage from './Health';
import Leads from './Leads';
import { AlertTriangle, ArrowRight, CheckCircle2, ChevronDown, CircleAlert } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, day, ksh as kshExact, kshShort } from '../lib/format';

/** Forecasts and summaries in whole shillings: cents would claim a precision the models don't have. */
const ksh = (cents: number) => kshExact(Math.round(cents / 100) * 100);
import { CHAIN_MODULE, LENSES, MODULE_ORDER, MODULES, lensFor, type ChainKey, type ModuleKey } from '../lib/modules';
import { Loading, PageError, PageHeader, Status, Tabs } from '../components/ui';

type Health = 'good' | 'warn' | 'bad';
export type ChainLink = { key: ChainKey; module: string; health: Health; headline: string; metrics: { label: string; value: string | number; unit: string | null }[]; blockers: string[]; to: string };
type Chain = { company: string; industry: string; links: ChainLink[]; asOf: string };
export type Series = {
  key: string; name: string; history: { month: string; value: number }[]; soFar: number;
  forecast: { model: string; explain: string; accuracy: number | null; months: number; tested: number; low: number; high: number; values: { month: string; value: number }[] } | null;
};
type Foresight = {
  money: Series[];
  assets: { id: string; tag: string; name: string; criticality: string; failures: number; mtbfDays: number; daysSinceLast: number; risk30: number; expectedAround: string; nextService: string | null; advice: string }[];
  stores: { id: string; name: string; unit: string; category: string; onHand: number; jobsNeed: number; perWeek: number; daysLeft: number | null; runsOutOn: string | null }[];
  budgets: { departmentId: string; department: string; budget: number; spent: number; projected: number; over: boolean; usedPct: number | null }[];
  cash: { now: number; customersDue: number; expectedIn: number; collectionRate: number; suppliersDue: number; payroll: number; in30: number; overdue: number };
};

const HEALTH: Record<Health, { label: string; tone: 'good' | 'warn' | 'bad'; icon: typeof CheckCircle2 }> = {
  good: { label: 'Healthy', tone: 'good', icon: CheckCircle2 },
  warn: { label: 'Needs attention', tone: 'warn', icon: CircleAlert },
  bad: { label: 'Broken link', tone: 'bad', icon: AlertTriangle },
};
const worst = (hs: Health[]): Health => (hs.includes('bad') ? 'bad' : hs.includes('warn') ? 'warn' : 'good');

type OsTab = 'overview' | 'forecasts' | 'insights' | 'leads' | 'plan' | 'health';
const OS_TABS: [OsTab, string][] = [['overview', 'Overview'], ['forecasts', 'Forecasts'], ['insights', 'Sales & customers'], ['leads', 'Leads'], ['plan', 'Monthly plan'], ['health', 'ERP health']];

/** Business OS: the whole business on one page, and the intelligence on top of it, in tabs. */
export default function BusinessOS() {
  const [params, setParams] = useSearchParams();
  const tab = (OS_TABS.find(([k]) => k === params.get('tab'))?.[0] ?? 'overview') as OsTab;
  const go = (t: OsTab) => setParams(t === 'overview' ? {} : { tab: t });
  return (
    <div className="space-y-6">
      <PageHeader print title="Business OS" subtitle="Five core modules, one source of truth, and what the numbers say. Every figure comes from the records the modules write." />
      <div className="-mt-2 overflow-x-auto"><Tabs label="Business OS" value={tab} onChange={go} options={OS_TABS} /></div>
      {tab === 'overview' && <Overview />}
      {tab === 'forecasts' && <Forecasts />}
      {tab === 'insights' && <Intelligence embedded />}
      {tab === 'leads' && <Leads />}
      {tab === 'plan' && <Planning embedded />}
      {tab === 'health' && <HealthPage embedded />}
    </div>
  );
}

function Forecasts() {
  const foresight = useApi<Foresight>('/os/foresight');
  return (
    <section aria-label="Forecasts">
      <p className="mb-4 max-w-3xl text-sm text-muted">Several simple models compete on your own history: each predicts the last few months from the months before them, and the closest wins. Its accuracy in that test is shown with it. The more months on record, the better the choice gets.</p>
      {foresight.error ? <PageError>{foresight.error}</PageError> : !foresight.data ? <Loading /> : <ForesightView f={foresight.data} />}
    </section>
  );
}

function Overview() {
  const chain = useApi<Chain>('/os/chain');
  const [lensKey, setLensKey] = useState<string | null>(null);
  if (chain.error) return <PageError>{chain.error}</PageError>;
  if (!chain.data) return <Loading />;
  const lens = lensKey ? LENSES[lensKey] : lensFor(chain.data.industry);
  const byModule = (m: ModuleKey) => chain.data!.links.filter((l) => CHAIN_MODULE[l.key] === m);
  return (
    <div className="space-y-8">
      <label className="flex items-center justify-end gap-2 text-sm text-muted">
        See it as
        <select className="field w-auto py-1.5" value={lensKey ?? ''} onChange={(e) => setLensKey(e.target.value || null)} aria-label="Industry view">
          <option value="">{lensFor(chain.data.industry).label} (yours)</option>
          {Object.entries(LENSES).map(([k, l]) => <option key={k} value={k}>{l.label}</option>)}
        </select>
      </label>

      {/* The modules: each tile in its own colour, with how its links are doing. */}
      <section aria-label="Modules" className="grid grid-cols-1 gap-3 sm:grid-cols-2 xl:grid-cols-3">
        {MODULE_ORDER.map((key) => {
          const m = MODULES[key];
          const links = byModule(key);
          const h = links.length ? worst(links.map((l) => l.health)) : 'good';
          const H = HEALTH[h];
          return (
            <Link key={key} to={`/w/${key}`} className="card group relative flex h-full min-w-0 flex-col overflow-hidden p-4 transition hover:shadow-md sm:p-5">
              <span className="absolute inset-x-0 top-0 h-1" style={{ background: m.color }} aria-hidden />
              <div className="flex items-center gap-3">
                <span className="grid h-10 w-10 shrink-0 place-items-center rounded-xl text-white shadow-xs" style={{ background: m.color }}><m.icon className="h-5 w-5" /></span>
                <p className="min-w-0 flex-1 font-semibold leading-tight">{m.name}</p>
                <span className="shrink-0 whitespace-nowrap"><Status tone={H.tone}><H.icon className="mr-1 inline h-3 w-3" />{H.label}</Status></span>
              </div>
              <p className="mt-2 line-clamp-2 text-sm text-muted">{m.promise}</p>
              {links.length > 0 && (
                <dl className="mt-3 divide-y divide-line border-t border-line text-sm">
                  {links.map((l) => (
                    <div key={l.key} className="flex items-baseline justify-between gap-3 py-1.5">
                      <dt className="min-w-0 text-muted">{lens.words[l.key]}</dt>
                      <dd className="max-w-[60%] text-right font-semibold">{l.headline}</dd>
                    </div>
                  ))}
                </dl>
              )}
              <span className="mt-auto inline-flex items-center gap-1 pt-3 text-sm font-semibold text-ink-2 group-hover:text-ink">Open {m.name} <ArrowRight className="h-3.5 w-3.5 transition group-hover:translate-x-0.5" /></span>
            </Link>
          );
        })}
      </section>

      {/* The chain, in the industry's words. */}
      <section aria-label="The business chain">
        <div className="mb-3">
          <h2 className="text-lg font-semibold">The chain</h2>
          <p className="max-w-3xl text-sm text-muted">{lens.story}</p>
        </div>
        <ol className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-4">
          {chain.data.links.map((l, i) => <ChainCard key={l.key} link={l} step={i + 1} word={lens.words[l.key]} />)}
        </ol>
      </section>

    </div>
  );
}

export function ChainCard({ link, step, word }: { link: ChainLink; step: number; word: string }) {
  const m = MODULES[CHAIN_MODULE[link.key]];
  const H = HEALTH[link.health];
  return (
    <li className={cn('card relative flex h-full min-w-0 flex-col p-4', link.health === 'bad' && 'ring-2 ring-bad/40')}>
      <div className="flex items-start gap-2">
        <span className="grid h-6 w-6 shrink-0 place-items-center rounded-full text-2xs font-bold text-white" style={{ background: m.color }}>{step}</span>
        <div className="min-w-0 flex-1">
          <p className="line-clamp-2 font-semibold leading-snug">{word}</p>
          <p className="mt-0.5 flex items-center gap-1.5 text-2xs text-muted"><span className="h-2 w-2 rounded-full" style={{ background: m.color }} />{m.name}</p>
        </div>
        <H.icon className={cn('mt-0.5 h-4 w-4 shrink-0', link.health === 'good' ? 'text-good' : link.health === 'warn' ? 'text-warn' : 'text-bad')} aria-label={H.label} />
      </div>
      <p className="mt-3 text-base font-semibold leading-snug">{link.headline}</p>
      {link.metrics.length > 0 && (
        <dl className="mt-2 divide-y divide-line border-t border-line text-sm">
          {link.metrics.slice(0, 4).map((x) => (
            <div key={x.label} className="flex items-baseline justify-between gap-3 py-1.5">
              <dt className="min-w-0 text-muted">{x.label}</dt>
              <dd className="num shrink-0 font-semibold">{x.value}{x.unit ?? ''}</dd>
            </div>
          ))}
        </dl>
      )}
      {link.blockers.length > 0 && (
        <ul className="mt-3 space-y-1 rounded-md bg-paper p-2 text-xs">
          {link.blockers.slice(0, 2).map((b) => <li key={b} className="flex gap-1.5"><span className={link.health === 'bad' ? 'text-bad' : 'text-warn'}>•</span><span className="line-clamp-2">{b}</span></li>)}
          {link.blockers.length > 2 && <li className="text-muted">+{link.blockers.length - 2} more</li>}
        </ul>
      )}
      <Link to={link.to} className="mt-auto inline-flex items-center gap-1 pt-3 text-sm font-semibold text-brand hover:underline">Go there <ArrowRight className="h-3.5 w-3.5" /></Link>
    </li>
  );
}

function ForesightView({ f }: { f: Foresight }) {
  const [series, setSeries] = useState(f.money[0].key);
  const s = f.money.find((x) => x.key === series) ?? f.money[0];
  const c = f.cash;
  const income = f.money[0];
  const atRisk = f.assets.filter((a) => a.risk30 >= 30).length;
  const over = f.budgets.filter((b) => b.over).length;
  const short = f.stores.filter((x) => x.jobsNeed > x.onHand || (x.daysLeft !== null && x.daysLeft <= 14)).length;
  const tested = (n: number) => (n === 1 ? 'the last month' : `the last ${n} months`);
  return (
    <div className="space-y-4">
      {/* The answers first: one number per question, each a link to the detail below. */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-5">
        <Kpi label={`${income.name} next month`} value={income.forecast ? kshShort(income.forecast.values[0].value) : '—'}
          note={income.forecast ? (income.forecast.accuracy !== null ? `${income.forecast.accuracy}% accurate` : income.forecast.model) : 'Needs 4 months of records'} href="#fc-chart" />
        <Kpi label="Cash in 30 days" value={kshShort(c.in30)} tone={c.in30 < 0 ? 'bad' : undefined} note={`${kshShort(c.now)} now`} href="#fc-cash" />
        <Kpi label="Assets at risk" value={String(atRisk)} tone={atRisk ? 'warn' : 'good'} note="likely to break in 30 days" href="#fc-assets" />
        <Kpi label="Budgets heading over" value={`${over} of ${f.budgets.length}`} tone={over ? 'bad' : 'good'} note="at this month's pace" href="#fc-budgets" />
        <Kpi label="Stores running out" value={String(short)} tone={short ? 'warn' : 'good'} note="within 2 weeks" href="#fc-stores" />
      </div>

      <div className="grid grid-cols-1 items-start gap-4 xl:grid-cols-3">
        <div id="fc-chart" className="card min-w-0 scroll-mt-20 p-4 sm:p-5 xl:col-span-2">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <Tabs label="Forecast" value={series} onChange={setSeries} options={f.money.map((m) => [m.key, m.name] as [string, string])} />
            {s.forecast && <span className="text-xs text-muted">Model <b className="text-ink">{s.forecast.model}</b>{s.forecast.accuracy !== null && <> · <b className="text-ink">{s.forecast.accuracy}%</b> accurate over {tested(s.forecast.tested)}</>}</span>}
          </div>
          {s.forecast ? (
            <>
              <ForecastBars s={s} />
              <div className="mt-4 grid grid-cols-1 gap-3 rounded-lg bg-paper p-3 text-sm sm:grid-cols-3">
                <div><p className="text-2xs text-muted">Next month</p><p className="num text-lg font-bold">{kshShort(s.forecast.values[0].value)}</p></div>
                <div><p className="text-2xs text-muted">Likely range</p><p className="num font-semibold">{kshShort(s.forecast.low)} – {kshShort(s.forecast.high)}</p></div>
                <div><p className="text-2xs text-muted">So far this month</p><p className="num font-semibold">{kshShort(s.soFar)}</p></div>
              </div>
              <p className="mt-2 text-xs text-muted">Why: the winning model says {s.forecast.explain}.</p>
            </>
          ) : <p className="mt-6 rounded-md bg-paper p-4 text-sm text-muted">Forecasts start once there are four complete months of records. Until then, nothing is guessed.</p>}
        </div>

        <div id="fc-cash" className="card min-w-0 scroll-mt-20 p-4 sm:p-5">
          <p className="font-semibold">Cash in 30 days</p>
          <dl className="mt-3 space-y-2 text-sm">
            <Row label="Cash, M-Pesa and bank now" value={c.now} />
            <Row label={`Customers should pay (${c.collectionRate}% usually on time)`} value={c.expectedIn} sign="+" />
            <Row label="Due to suppliers" value={c.suppliersDue} sign="−" />
            <Row label="Pay and statutory (last month’s)" value={c.payroll} sign="−" />
            <div className="border-t border-line pt-2"><Row label="In 30 days" value={c.in30} strong /></div>
          </dl>
          {c.overdue > 0 && <p className="mt-3 rounded-md bg-warn/10 p-2 text-xs text-warn">{kshShort(c.overdue)} is already overdue from customers.</p>}
        </div>
      </div>

      <Panel id="fc-budgets" title="Budgets this month" count={f.budgets.length} empty="No approved budgets yet. Departments prepare them in Finance → Budgets.">
        {f.budgets.length > 0 && (
          <div className="overflow-x-auto">
            <table className="w-full min-w-[40rem] text-sm">
              <thead className="text-left text-xs text-muted"><tr><th className="py-2 pr-3">Department</th><th className="py-2 pr-3 text-right">Budget</th><th className="py-2 pr-3 text-right">Spent</th><th className="py-2 pr-3 text-right">On course for</th><th className="w-[30%] py-2">Used</th></tr></thead>
              <tbody className="divide-y divide-line">
                {[...f.budgets].sort((a, b) => Number(b.over) - Number(a.over) || b.projected / Math.max(1, b.budget) - a.projected / Math.max(1, a.budget)).map((b) => {
                  const used = b.budget > 0 ? Math.min(100, (100 * b.spent) / b.budget) : 0;
                  const proj = b.budget > 0 ? Math.min(100, (100 * b.projected) / b.budget) : 0;
                  return (
                    <tr key={b.departmentId}>
                      <td className="py-2 pr-3 font-semibold">{b.department}</td>
                      <td className="num py-2 pr-3 text-right">{kshShort(b.budget)}</td>
                      <td className="num py-2 pr-3 text-right">{kshShort(b.spent)}</td>
                      <td className={cn('num py-2 pr-3 text-right', b.over && 'font-semibold text-bad')}>{kshShort(b.projected)}{b.over && ' ↑'}</td>
                      <td className="py-2">
                        <div className="flex items-center gap-2">
                          <div className="relative h-2 flex-1 rounded-full bg-paper ring-1 ring-line" title={`Spent ${ksh(b.spent)} · on course for ${ksh(b.projected)} · budget ${ksh(b.budget)}`}>
                            <div className="absolute inset-y-0 left-0 rounded-full bg-line" style={{ width: `${proj}%` }} />
                            <div className={cn('absolute inset-y-0 left-0 rounded-full', b.over ? 'bg-bad' : 'bg-good')} style={{ width: `${used}%` }} />
                          </div>
                          <span className="num w-10 shrink-0 text-right text-xs text-muted">{Math.round(used)}%</span>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </Panel>

      <div className="grid grid-cols-1 items-start gap-4 xl:grid-cols-2">
        <Panel id="fc-assets" title="Assets likely to break in 30 days" count={f.assets.length} empty="Not enough breakdown history yet: risk is worked out from at least two past breakdowns per asset.">
          {f.assets.length > 0 && (
            <ul className="divide-y divide-line text-sm">
              {f.assets.map((a) => (
                <li key={a.id} className="flex items-start gap-3 py-2.5">
                  <span className={cn('num w-12 shrink-0 rounded-md py-1 text-center text-sm font-bold', a.risk30 >= 60 ? 'bg-bad/10 text-bad' : a.risk30 >= 30 ? 'bg-warn/10 text-warn' : 'bg-paper text-ink-2')}>{a.risk30}%</span>
                  <div className="min-w-0 flex-1">
                    <p><Link className="font-semibold text-brand hover:underline" to={`/assets?id=${a.id}`}>{a.name}</Link><span className="ml-1 text-xs text-muted">{a.tag}{a.criticality === 'High' && ' · critical'}</span></p>
                    <p className="text-xs text-muted">Breaks about every {Math.round(a.mtbfDays)} days · expected around {day(a.expectedAround)}</p>
                    <p className="mt-0.5 text-xs">{a.advice}</p>
                  </div>
                </li>
              ))}
            </ul>
          )}
        </Panel>

        <Panel id="fc-stores" title="Stores running out" count={f.stores.length} empty="Nothing in stores is running low at the rate it’s used.">
          {f.stores.length > 0 && (
            <ul className="divide-y divide-line text-sm">
              {f.stores.map((x) => (
                <li key={x.id} className="flex items-start justify-between gap-3 py-2.5">
                  <div className="min-w-0">
                    <p className="font-semibold">{x.name}{x.category && <span className="ml-1 text-xs font-normal text-muted">{x.category}</span>}</p>
                    <p className="text-xs text-muted">{x.onHand} {x.unit} in stores{x.jobsNeed ? ` · open jobs need ${x.jobsNeed}` : ''}{x.perWeek ? ` · ${x.perWeek} used a week` : ''}</p>
                  </div>
                  <span className="shrink-0 text-right text-xs">
                    {x.jobsNeed > x.onHand ? <Status tone="bad">Short for jobs</Status> : x.runsOutOn ? <><b>{day(x.runsOutOn)}</b><br /><span className="text-muted">{x.daysLeft} days left</span></> : <span className="text-muted">Below reorder level</span>}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </Panel>
      </div>
    </div>
  );
}

function Kpi({ label, value, note, tone, href }: { label: string; value: string; note: string; tone?: 'good' | 'warn' | 'bad'; href: string }) {
  return (
    <a href={href} onClick={(e) => { e.preventDefault(); document.querySelector(href)?.scrollIntoView({ behavior: 'smooth', block: 'start' }); }} className="card block min-w-0 p-3 transition hover:shadow-md sm:p-4">
      <p className="truncate text-2xs font-medium uppercase tracking-wide text-muted">{label}</p>
      <p className={cn('num mt-1 truncate text-xl font-bold', tone === 'bad' && 'text-bad', tone === 'warn' && 'text-warn')}>{value}</p>
      <p className="mt-0.5 truncate text-2xs text-muted">{note}</p>
    </a>
  );
}

function Row({ label, value, sign, strong }: { label: string; value: number; sign?: string; strong?: boolean }) {
  return (
    <div className="flex items-baseline justify-between gap-3">
      <dt className={cn('text-muted', strong && 'font-semibold text-ink')}>{label}</dt>
      <dd className={cn('num shrink-0', strong && 'text-base font-bold', strong && value < 0 && 'text-bad')}>{sign ? `${sign} ` : ''}{kshShort(value)}</dd>
    </div>
  );
}

function Panel({ id, title, count, empty, children, className }: { id?: string; title: string; count?: number; empty: string; children: React.ReactNode; className?: string }) {
  const [open, setOpen] = useState(true);
  const has = !!children;
  return (
    <div id={id} className={cn('card min-w-0 scroll-mt-20 p-4 sm:p-5', className)}>
      <button className="flex w-full items-center justify-between text-left" onClick={() => setOpen(!open)} aria-expanded={open}>
        <p className="font-semibold">{title}{!!count && <span className="ml-2 rounded-full bg-paper px-2 py-0.5 text-2xs font-semibold text-muted ring-1 ring-line">{count}</span>}</p>
        <ChevronDown className={cn('h-4 w-4 text-muted transition', !open && '-rotate-90')} />
      </button>
      {open && <div className="mt-3">{has ? children : <p className="text-sm text-muted">{empty}</p>}</div>}
    </div>
  );
}

/** Twelve months of history and the next three forecast, one series, one axis. Forecast bars are lighter and labelled. */
export function ForecastBars({ s }: { s: Series }) {
  const fc = s.forecast!;
  const first = s.history.findIndex((h) => h.value !== 0);
  const history = first < 0 ? s.history : s.history.slice(first);
  const bars = [...history.map((h) => ({ ...h, forecast: false })), ...fc.values.map((v) => ({ ...v, forecast: true }))];
  const max = Math.max(1, ...bars.map((b) => Math.abs(b.value)), fc.high);
  const color = MODULES.intelligence.color;
  const [hover, setHover] = useState<number | null>(null);
  return (
    <div className="mt-4">
      <div className="relative flex h-48 items-end gap-1 border-b border-line" role="img" aria-label={`${s.name}: last ${history.length} months and the next ${fc.values.length} forecast`}>
        {bars.map((b, i) => (
          <div key={i} className="relative flex h-full min-w-0 flex-1 flex-col justify-end" onMouseEnter={() => setHover(i)} onMouseLeave={() => setHover(null)}>
            {hover === i && (
              <div className="pointer-events-none absolute bottom-full left-1/2 z-10 mb-1 -translate-x-1/2 whitespace-nowrap rounded-md bg-ink px-2 py-1 text-2xs text-white shadow">
                {b.month}{b.forecast ? ' (forecast)' : ''}: {kshShort(b.value)}
              </div>
            )}
            <div
              className="w-full rounded-t-[4px]"
              style={{
                height: `${(Math.max(0, b.value) / max) * 100}%`, minHeight: b.value > 0 ? 2 : 0,
                background: b.forecast ? `repeating-linear-gradient(45deg, ${color}55 0 4px, ${color}22 4px 8px)` : color,
                outline: b.forecast ? `1px dashed ${color}` : undefined, opacity: hover === null || hover === i ? 1 : 0.55,
              }}
            />
          </div>
        ))}
      </div>
      <div className="mt-1 flex gap-1 text-[10px] text-faint">
        {bars.map((b, i) => <span key={i} className={cn('min-w-0 flex-1 truncate text-center', bars.length > 10 && i % 2 === 1 && 'hidden sm:block', b.forecast && 'font-semibold text-ink-2')}>{b.month.split(' ')[0]}</span>)}
      </div>
      <p className="mt-2 flex items-center gap-4 text-2xs text-muted">
        <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-sm" style={{ background: color }} />Actual</span>
        <span className="flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-sm" style={{ background: `repeating-linear-gradient(45deg, ${color}55 0 2px, ${color}22 2px 4px)`, outline: `1px dashed ${color}` }} />Forecast</span>
      </p>
    </div>
  );
}
