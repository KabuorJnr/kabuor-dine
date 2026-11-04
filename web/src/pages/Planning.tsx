// The monthly planning meeting (sales and operations planning): one page where the leadership balances demand,
// supply, capacity and money, writes down what it decided, and agrees it. Once agreed, the decisions are sealed in the
// approvals register with the numbers they were made on.
import { useState } from 'react';
import { Link } from 'react-router';
import { CalendarCheck2 } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, day, dateTime, ksh as kshExact } from '../lib/format';
import { MODULES } from '../lib/modules';
import { ErrorNote, Field, Loading, PageError, PageHeader, Stat, Status, Tabs } from '../components/ui';
import { ForecastBars, type Series } from './BusinessOS';

const ksh = (c: number) => kshExact(Math.round(c / 100) * 100);
type Week = { week: string; demand: number; available: number; load: number | null };
type Plan = {
  label: string; month: string;
  demand: Series[];
  assetRisk: { id: string; name: string; risk30: number; expectedAround: string }[];
  supply: { toMake: { itemId: string; name: string; unit: string; batches: number; toMake: number }[]; toBuy: { itemId: string; name: string; unit: string; toBuy: number; value: number; orderBy: string }[]; toBuyValue: number };
  capacity: { technicians: number; weeks: Week[]; availability: number | null; unestimated: number };
  money: { cash: { now: number; in30: number; overdue: number }; budgets: { department: string; budget: number; projected: number; over: boolean }[] };
  people: { headcount: number; openPositions: number; onLeave: number };
  meeting: { id: string; attendees: string; decisions: string; agreedBy: string; agreedAt: string | null } | null;
  past: { id: string; month: string; agreedBy: string; agreedAt: string; decisions: string }[];
  canAgree: boolean;
};

export default function Planning({ embedded }: { embedded?: boolean }) {
  const { data, error, reload } = useApi<Plan>('/os/plan');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-6">
      {!embedded && <PageHeader print title={`Monthly plan · ${data.label}`} />}
      <div className="rounded-lg bg-paper p-4 text-sm ring-1 ring-line">
        <p className="font-semibold">The monthly plan for {data.label}: four questions, then decide.</p>
        <p className="mt-1 text-muted">Once a month the owner and the heads of department look at this page together. 1. How much will we sell? 2. Can we make or buy enough? 3. Do we have the people and machines? 4. Is the money there? Then write down what you’ll do about it and agree it. Once agreed it’s sealed, so next month you can check what was decided against what happened.</p>
      </div>
      <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
        <Demand series={data.demand} />
        <section className="card min-w-0 p-4 sm:p-5">
          <h2 className="font-semibold">2 · Supply: make and buy</h2>
          <p className="mt-1 text-sm text-muted">What to make to meet the forecast, and the materials to buy for it (from the product blueprints).</p>
          <div className="mt-3 grid grid-cols-2 gap-3">
            <Stat label="Products to make" value={data.supply.toMake.length} />
            <Stat label="Materials to buy" value={ksh(data.supply.toBuyValue)}>{data.supply.toBuy.length} item(s)</Stat>
          </div>
          <ul className="mt-3 space-y-1 text-sm">
            {data.supply.toMake.slice(0, 4).map((p) => <li key={p.itemId}>Make <b>{p.toMake} {p.unit}</b> of {p.name} ({p.batches} batch{p.batches === 1 ? '' : 'es'})</li>)}
            {data.supply.toBuy.slice(0, 4).map((m) => <li key={m.itemId}>Buy <b>{m.toBuy} {m.unit}</b> {m.name} by {day(m.orderBy)}</li>)}
            {data.supply.toMake.length + data.supply.toBuy.length === 0 && <li className="text-muted">Nothing to make or buy for the plan.</li>}
          </ul>
          <Link to="/production?tab=plan" className="mt-3 inline-block text-sm font-semibold text-brand hover:underline">See what to make & buy</Link>
        </section>
        <Capacity c={data.capacity} risk={data.assetRisk} />
        <section className="card min-w-0 p-4 sm:p-5">
          <h2 className="font-semibold">4 · Money and people</h2>
          <div className="mt-3 grid grid-cols-1 gap-3 sm:grid-cols-2 2xl:grid-cols-3">
            <Stat label="Cash now" value={ksh(data.money.cash.now)} />
            <Stat label="In 30 days" value={ksh(data.money.cash.in30)} tone={data.money.cash.in30 < 0 ? 'bad' : undefined} />
            <Stat label="People" value={data.people.headcount}>{data.people.onLeave} on leave · {data.people.openPositions} open</Stat>
          </div>
          {data.money.budgets.length > 0 && (
            <ul className="mt-3 space-y-1 text-sm">
              {data.money.budgets.map((b) => <li key={b.department} className="flex justify-between gap-2"><span>{b.department}</span><span className={cn('num', b.over && 'font-semibold text-bad')}>{b.over ? `heading to ${ksh(b.projected)} of ${ksh(b.budget)}` : `on course: ${ksh(b.projected)} of ${ksh(b.budget)}`}</span></li>)}
            </ul>
          )}
        </section>
      </div>
      <Decisions plan={data} onSaved={reload} />
      {data.past.length > 0 && (
        <section>
          <h2 className="mb-2 font-semibold">Earlier plans</h2>
          <ul className="card divide-y divide-line text-sm">
            {data.past.map((p) => (
              <li key={p.id} className="px-4 py-3">
                <p className="font-semibold">{new Date(p.month).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })} <span className="font-normal text-muted">· agreed by {p.agreedBy}, {dateTime(p.agreedAt)}</span></p>
                <p className="mt-1 whitespace-pre-line text-muted">{p.decisions}</p>
              </li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}

function Demand({ series }: { series: Series[] }) {
  const [key, setKey] = useState(series[0]?.key ?? 'income');
  const s = series.find((x) => x.key === key) ?? series[0];
  return (
    <section className="card min-w-0 p-4 sm:p-5">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 className="font-semibold">1 · Demand: what the money says</h2>
        <Tabs label="Series" value={key} onChange={setKey} options={series.map((x) => [x.key, x.name] as [string, string])} />
      </div>
      {s?.forecast ? (
        <>
          <ForecastBars s={s} />
          <p className="mt-2 text-sm text-muted">Next three months: {s.forecast.values.map((v) => `${v.month} ${ksh(v.value)}`).join(' · ')}. {s.forecast.model}, {s.forecast.accuracy}% accurate over the last {s.forecast.tested} months.</p>
        </>
      ) : <p className="mt-3 rounded-md bg-paper p-3 text-sm text-muted">Forecasts start after four complete months of records.</p>}
    </section>
  );
}

function Capacity({ c, risk }: { c: Plan['capacity']; risk: Plan['assetRisk'] }) {
  const color = MODULES.operations.color;
  const max = Math.max(1, ...c.weeks.flatMap((w) => [w.demand, w.available]));
  return (
    <section className="card min-w-0 p-4 sm:p-5">
      <h2 className="font-semibold">3 · Capacity: can we do it?</h2>
      <p className="mt-1 text-sm text-muted">Maintenance hours planned in the next four weeks against the hours {c.technicians} technical staff have (40 a week each, less approved leave).{c.availability !== null && ` Assets available now: ${c.availability}%.`}</p>
      <div className="mt-3 space-y-2">
        {c.weeks.map((w) => (
          <div key={w.week} className="grid grid-cols-[4.5rem_1fr_4.5rem] items-center gap-2 text-sm" title={`Week of ${w.week}: ${w.demand} h planned, ${w.available} h available`}>
            <span className="text-muted">{w.week}</span>
            <div className="relative h-3 rounded-full bg-paper ring-1 ring-line">
              <div className="absolute inset-y-0 left-0 rounded-full bg-line" style={{ width: `${(w.available / max) * 100}%` }} />
              <div className="absolute inset-y-0 left-0 rounded-full" style={{ width: `${(Math.min(w.demand, max) / max) * 100}%`, background: w.load !== null && w.load > 100 ? 'var(--color-bad)' : color }} />
            </div>
            <span className={cn('num text-right', w.load !== null && w.load > 100 && 'font-semibold text-bad')}>{w.load === null ? `${w.demand} h` : `${w.load}%`}</span>
          </div>
        ))}
      </div>
      <p className="mt-2 flex flex-wrap gap-4 text-2xs text-muted"><span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm" style={{ background: color }} />Hours planned</span><span className="flex items-center gap-1.5"><span className="h-2 w-3 rounded-sm bg-line" />Hours available</span></p>
      {c.unestimated > 0 && <p className="mt-2 text-xs text-warn">{c.unestimated} open job(s) have no hours estimated, so the load is understated.</p>}
      {risk.length > 0 && <p className="mt-2 text-sm">Likely breakdowns: {risk.slice(0, 3).map((r) => `${r.name} (${r.risk30}%)`).join(', ')}.</p>}
    </section>
  );
}

function Decisions({ plan, onSaved }: { plan: Plan; onSaved: () => void }) {
  const m = plan.meeting;
  const [f, setF] = useState({ attendees: m?.attendees ?? '', decisions: m?.decisions ?? '' });
  const [state, setState] = useState({ busy: false, error: '', saved: false });
  const save = async (agree: boolean) => {
    setState({ busy: true, error: '', saved: false });
    try { await api('/os/plan', { body: { month: plan.month, ...f, agree } }); onSaved(); setState({ busy: false, error: '', saved: true }); } catch (e) { setState({ busy: false, error: (e as Error).message, saved: false }); }
  };
  if (m?.agreedAt) return (
    <section className="card p-4 sm:p-5 ring-1 ring-good/30">
      <p className="flex items-center gap-2 font-semibold text-good"><CalendarCheck2 className="h-5 w-5" /> Plan agreed by {m.agreedBy}, {dateTime(m.agreedAt)}</p>
      {m.attendees && <p className="mt-1 text-sm text-muted">Present: {m.attendees}</p>}
      <p className="mt-2 whitespace-pre-line text-sm">{m.decisions}</p>
      <p className="mt-2 text-xs text-muted">Sealed in the approvals register with the numbers above. Changes go in next month’s meeting.</p>
    </section>
  );
  return (
    <section className="card space-y-3 p-4 sm:p-5">
      <h2 className="font-semibold">5 · What we decided</h2>
      <Field label="Who was there"><input className="field" value={f.attendees} onChange={(e) => setF({ ...f, attendees: e.target.value })} placeholder="Owner, head of engineering, sales lead, finance" /></Field>
      <Field label="Decisions" hint="One per line: what, who, by when."><textarea className="field min-h-32" value={f.decisions} onChange={(e) => setF({ ...f, decisions: e.target.value })} placeholder={'Bring the tug overhaul forward to week 2 (Peter)\nOrder brake spares now, ahead of the December peak (procurement)\nHold new hiring until January (owner)'} /></Field>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <div className="flex flex-wrap items-center gap-2">
        <button className="btn btn-ghost" disabled={state.busy} onClick={() => save(false)}>Save draft</button>
        {plan.canAgree ? <button className="btn btn-primary" disabled={state.busy || f.decisions.trim().length < 10} onClick={() => save(true)}>Agree the plan</button> : <Status tone="info">The owner or an accountant agrees it</Status>}
        {state.saved && <span className="text-sm text-muted">Saved.</span>}
      </div>
    </section>
  );
}
