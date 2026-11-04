// The KPI scorecard on the dashboard. The owner is asking "are we on target, and where aren't we?", so it opens
// with the count on target, then every measure grouped by area, each with its value, its target (pro-rated for
// running totals, so mid-month sales are judged against mid-month), the change on the same days of the last
// period, a six-period trend, and where to look. Targets are the owner's to set.
import { useState } from 'react';
import { Link } from 'react-router';
import { ArrowDownRight, ArrowUpRight, Loader2, Target } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { Chips, ErrorNote, Sheet, SheetFooter, PageError } from './ui';

export type Kpi = {
  key: string; group: string; name: string; unit: 'ksh' | '%' | 'count' | 'hours' | 'days'; higherIsBetter: boolean; link: string; explain: string;
  value: number | null; target: number | null; dueByNow: number | null; status: 'met' | 'close' | 'behind' | 'none'; previous: number | null; change: number | null;
  projected: number | null; trend: { from: string; value: number | null }[];
};
export type Card = { period: string; from: string; to: string; shownTo: string; elapsedShare: number; met: number; close: number; behind: number; withoutTarget: number; kpis: Kpi[] };

const shillings = (cents: number) => ksh(Math.round(cents / 100) * 100);
export function fmt(k: Pick<Kpi, 'unit'>, v: number | null) {
  if (v == null) return '—';
  switch (k.unit) {
    case 'ksh': return shillings(v);
    case '%': return `${v}%`;
    case 'hours': return `${v} h`;
    case 'days': return `${v} days`;
    default: return v.toLocaleString('en-KE');
  }
}

export const STATUS: Record<Kpi['status'], [string, string]> = {
  met: ['On target', 'bg-good-soft text-good'], close: ['Nearly there', 'bg-warn-soft text-warn'], behind: ['Behind', 'bg-bad-soft text-bad'], none: ['No target', 'bg-paper text-muted ring-1 ring-line'],
};

/** Six periods as a line: one series, its name is the tile's, so no legend; the last point is marked. */
export function Spark({ points, good }: { points: (number | null)[]; good: boolean }) {
  const vals = points.map((p) => p ?? null);
  const known = vals.filter((v): v is number => v != null);
  if (known.length < 2) return <span className="block h-8" />;
  const [min, max] = [Math.min(...known), Math.max(...known)];
  const x = (i: number) => (i / (vals.length - 1)) * 96 + 2;
  const y = (v: number) => (max === min ? 16 : 28 - ((v - min) / (max - min)) * 24);
  const d = vals.map((v, i) => (v == null ? '' : `${vals.slice(0, i).some((p) => p != null) ? 'L' : 'M'}${x(i).toFixed(1)},${y(v).toFixed(1)}`)).join(' ');
  const last = vals.length - 1;
  return (
    <svg viewBox="0 0 100 32" className={cn('h-8 w-full', good ? 'text-good' : 'text-muted')} aria-hidden>
      <path d={d} fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" vectorEffect="non-scaling-stroke" />
      {vals[last] != null && <circle cx={x(last)} cy={y(vals[last]!)} r="2.5" fill="currentColor" />}
    </svg>
  );
}

function Tile({ k }: { k: Kpi }) {
  // A target with nothing to measure yet (no payroll run, no stock count) isn't a missing target.
  const [label, tone] = k.value == null && k.target != null ? ['No data yet', STATUS.none[1]] : STATUS[k.status];
  const better = k.change == null ? null : k.higherIsBetter ? k.change > 0 : k.change < 0;
  return (
    <Link to={k.link} className="card flex flex-col gap-1.5 p-3.5 transition hover:ring-faint" title={k.explain}>
      <span className="flex items-start justify-between gap-2">
        <span className="min-w-0">
          <span className="block text-[10px] font-semibold tracking-wider text-faint uppercase">{k.group}</span>
          <span className="text-xs font-semibold text-muted">{k.name}</span>
        </span>
        <span className={cn('shrink-0 rounded-full px-2 py-0.5 text-2xs font-semibold', tone)}>{label}</span>
      </span>
      <span className="num text-xl font-bold">{fmt(k, k.value)}</span>
      <span className="text-xs text-muted">
        {k.target != null ? (k.dueByNow != null ? <>Target {fmt(k, k.target)} · {fmt(k, k.dueByNow)} by today</> : <>Target {k.higherIsBetter ? 'at least' : 'at most'} {fmt(k, k.target)}</>) : 'Set a target'}
      </span>
      <Spark points={k.trend.map((t) => t.value)} good={k.status === 'met'} />
      <span className="flex items-center justify-between text-xs">
        {k.change != null ? (
          <span className={cn('inline-flex items-center gap-0.5 font-semibold', better ? 'text-good' : 'text-bad')}>
            {k.change >= 0 ? <ArrowUpRight className="h-3.5 w-3.5" /> : <ArrowDownRight className="h-3.5 w-3.5" />}{Math.abs(k.change)}% vs last period
          </span>
        ) : <span className="text-faint">No earlier figure</span>}
        {k.projected != null && <span className="text-muted">on pace for {fmt(k, k.projected)}</span>}
      </span>
    </Link>
  );
}

export const kpisPath = (period: string, offset: number) => `/insights/kpis?period=${period}&offset=${offset}`;

export default function KpiBoard() {
  const session = useSession();
  const [period, setPeriod] = useState<'month' | 'quarter' | 'year'>('month');
  const [offset, setOffset] = useState(0);
  const { data, error, reload } = useApi<Card>(kpisPath(period, offset));
  const [editing, setEditing] = useState(false);
  if (error) return <PageError>{error}</PageError>;
  const groups = data ? [...new Set(data.kpis.map((k) => k.group))] : [];
  const title = data ? new Date(`${data.from}T00:00:00`).toLocaleDateString('en-GB', period === 'year' ? { year: 'numeric' } : { month: 'long', year: 'numeric' }) : '';
  const scored = data ? data.met + data.close + data.behind : 0;
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <div className="min-w-0 flex-1">
          <h2 className="flex items-center gap-2 font-semibold"><Target className="h-4 w-4" /> KPIs · {period === 'quarter' && data ? `Quarter from ${title}` : title}</h2>
          {data && (
            <p className="text-sm text-muted">
              {scored > 0 ? <><b className="text-good">{data.met} on target</b>{data.close > 0 && <>, <b className="text-warn">{data.close} nearly</b></>}{data.behind > 0 && <>, <b className="text-bad">{data.behind} behind</b></>} of {scored} with targets</> : 'No targets set yet'}
              {offset === 0 && data.elapsedShare < 100 && <> · {data.elapsedShare}% of the {period} gone</>}
            </p>
          )}
        </div>
        <Chips label="Period" value={period} onChange={(p) => (setPeriod(p), setOffset(0))} options={[['month', 'Month'], ['quarter', 'Quarter'], ['year', 'Year']]} />
        <span className="flex gap-1">
          <button className="btn btn-ghost px-2.5" onClick={() => setOffset(offset - 1)} aria-label="Earlier period">‹</button>
          <button className="btn btn-ghost px-2.5" disabled={offset === 0} onClick={() => setOffset(offset + 1)} aria-label="Later period">›</button>
        </span>
        {can(session, 'admin') && <button className="btn btn-ghost" onClick={() => setEditing(true)}>Set targets</button>}
      </div>
      {!data ? <Loader2 className="h-5 w-5 animate-spin text-faint" /> : (
        <div className="grid gap-2.5 sm:grid-cols-2 lg:grid-cols-4">
          {groups.flatMap((g) => data.kpis.filter((k) => k.group === g)).map((k) => <Tile key={k.key} k={k} />)}
        </div>
      )}
      {data && editing && <Targets kpis={data.kpis} onClose={() => setEditing(false)} onSaved={() => (setEditing(false), reload())} />}
    </section>
  );
}

function Targets({ kpis, onClose, onSaved }: { kpis: Kpi[]; onClose: () => void; onSaved: () => void }) {
  // Money is entered in shillings; the API keeps cents.
  const [f, setF] = useState<Record<string, string>>(() => Object.fromEntries(kpis.map((k) => [k.key, k.target == null ? '' : String(k.unit === 'ksh' ? k.target / 100 : k.target)])));
  const [state, setState] = useState({ busy: false, error: '' });
  const save = async () => {
    setState({ busy: true, error: '' });
    try {
      await api('/insights/kpis/targets', { method: 'PUT', body: Object.fromEntries(Object.entries(f).map(([k, v]) => [k, v.trim() === '' ? null : Number(v)])) });
      onSaved();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const unitLabel = (k: Kpi) => (k.unit === 'ksh' ? 'KSh per period (month)' : k.unit === '%' ? '%' : k.unit);
  return (
    <Sheet open onClose={onClose} title="KPI targets">
      <div className="space-y-3">
        <p className="text-sm text-muted">Leave a target empty to just watch the number. Running totals (sales, money collected, new customers) are monthly targets; quarters and years scale them.</p>
        {kpis.map((k) => (
          <label key={k.key} className="grid grid-cols-[1fr_8rem] items-center gap-3 text-sm">
            <span><b className="block">{k.name}</b><span className="text-xs text-muted">{k.higherIsBetter ? 'At least' : 'At most'} · {unitLabel(k)}</span></span>
            <input className="field num" inputMode="decimal" value={f[k.key]} onChange={(e) => setF({ ...f, [k.key]: e.target.value })} placeholder="No target" />
          </label>
        ))}
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy} onClick={save}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save targets</button>
        </SheetFooter>
      </div>
    </Sheet>
  );
}
