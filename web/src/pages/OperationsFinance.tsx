// The money side of operations, for the owner and the accountant. They're deciding "is each department within its
// budget, what are our assets costing us to keep running, and what money is already promised?", so the page leads
// with departments against budget and commitments, then maintenance by asset and month, then the buying pipeline.
// Every figure comes from the books, charged to a department, asset or job, so it agrees with the profit and loss.
import { useState } from 'react';
import { Link } from 'react-router';
import { useApi } from '../lib/api';
import { cn, ksh, kshShort, todayIso } from '../lib/format';
import { Chips, Loading, PageError, PageHeader, Stat } from '../components/ui';

type Data = {
  from: string; to: string; months: number;
  expenses: { total: number; tagged: number; untagged: number; taggedShare: number | null };
  byDepartment: { id: string; name: string; code: string; spent: number; budget: number; committed: number; remaining: number | null; used: number | null; supplies: number; maintenance: number }[];
  maintenance: { total: number; onJobs: number; byAsset: { id: string; tag: string; name: string; type: string; cost: number; value: number; share: number | null }[]; byType: { type: string; cost: number }[]; byMonth: { month: string; maintenance: number; supplies: number }[] };
  supplies: number;
  pipeline: { awaitingApproval: { count: number; value: number }; approvedNotBought: { count: number; value: number }; openOrders: { count: number; value: number }; unpaidBills: { count: number; value: number; overdue: number }; tenders: number };
  jobs: { closed: number; estimated: number; actual: number; overEstimate: { id: string; number: string; title: string; estimate: number; actual: number }[]; labourIncluded: boolean };
  stock: { value: number; takes: number; variance: number };
  topAccounts: { code: string; name: string; amount: number }[];
};

const firstOf = (monthsBack: number) => { const d = new Date(Date.now() + 3 * 3600e3); d.setUTCDate(1); d.setUTCMonth(d.getUTCMonth() - monthsBack); return d.toISOString().slice(0, 10); };

export default function OperationsFinance() {
  const [range, setRange] = useState('3');
  const { data, error } = useApi<Data>(`/operations/finance?from=${firstOf(Number(range) - 1)}&to=${todayIso()}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const p = data.pipeline;
  return (
    <div>
      <PageHeader print title="Operations costs" subtitle="Departments against budget, what keeping the assets running costs, and money already promised to suppliers. Before VAT, from the books." />
      <div className="space-y-5">
        <Chips label="Period" value={range} onChange={setRange} options={[['1', 'This month'], ['3', 'Last 3 months'], ['6', 'Last 6 months'], ['12', 'Last 12 months']]} />
        <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
          <Stat label="Maintenance" value={ksh(data.maintenance.total)}>{ksh(data.maintenance.onJobs)} of it on work orders</Stat>
          <Stat label="Supplies used by departments" value={ksh(data.supplies)}>Issued from the store</Stat>
          <Stat label="Promised, not yet billed" value={ksh(p.approvedNotBought.value + p.openOrders.value)}>{p.openOrders.count} open order{p.openOrders.count === 1 ? '' : 's'} · {p.approvedNotBought.count} approved request{p.approvedNotBought.count === 1 ? '' : 's'}</Stat>
          <Stat label="Charged to a department" value={data.expenses.taggedShare === null ? '—' : `${data.expenses.taggedShare}%`}>{ksh(data.expenses.untagged)} of expenses not charged to one</Stat>
        </div>

        <section className="card overflow-x-auto">
          <p className="border-b border-line px-4 py-3 font-semibold">Departments{data.months > 1 && <span className="font-normal text-muted"> · budget for {data.months} months</span>}</p>
          {data.byDepartment.length === 0 ? <p className="p-4 text-sm text-muted">No departments yet. <Link className="text-brand hover:underline" to="/departments">Set them up</Link> to see spending by department.</p> : (
            <table className="w-full min-w-[48rem] text-sm">
              <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2">Department</th><th className="px-4 py-2 text-right">Spent</th><th className="px-4 py-2 text-right">Of which maintenance</th><th className="px-4 py-2 text-right">Supplies</th><th className="px-4 py-2 text-right">Committed</th><th className="px-4 py-2 text-right">Budget</th><th className="px-4 py-2">Used</th></tr></thead>
              <tbody className="divide-y divide-line">
                {data.byDepartment.map((d) => (
                  <tr key={d.id}>
                    <td className="px-4 py-2.5 font-semibold">{d.name}</td>
                    <td className="num px-4 py-2.5 text-right">{ksh(d.spent)}</td>
                    <td className="num px-4 py-2.5 text-right text-muted">{ksh(d.maintenance)}</td>
                    <td className="num px-4 py-2.5 text-right text-muted">{ksh(d.supplies)}</td>
                    <td className="num px-4 py-2.5 text-right">{ksh(d.committed)}</td>
                    <td className="num px-4 py-2.5 text-right">{d.budget ? ksh(d.budget) : <span className="text-muted">—</span>}</td>
                    <td className="px-4 py-2.5">
                      {d.used === null ? <span className="text-muted">No budget</span> : (
                        <div className="flex min-w-32 items-center gap-2">
                          <div className="h-1.5 flex-1 rounded-full bg-paper ring-1 ring-line"><div className={cn('h-1.5 rounded-full', d.used >= 100 ? 'bg-bad' : d.used >= 80 ? 'bg-warn' : 'bg-good')} style={{ width: `${Math.min(100, d.used)}%` }} /></div>
                          <span className={cn('num w-10 text-right text-xs', d.used >= 100 && 'font-semibold text-bad')}>{d.used}%</span>
                        </div>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <div className="grid gap-4 lg:grid-cols-2">
          <section className="card p-4">
            <p className="mb-1 font-semibold">Maintenance by month</p>
            <p className="mb-3 text-xs text-muted">Parts, payments and contractor bills booked to repairs and maintenance.</p>
            <MonthBars rows={data.maintenance.byMonth.map((m) => [m.month, m.maintenance])} />
          </section>
          <section className="card overflow-hidden">
            <p className="border-b border-line px-4 py-3 font-semibold">Assets costing the most to keep running</p>
            {data.maintenance.byAsset.length === 0 ? <p className="p-4 text-sm text-muted">No maintenance has been charged to an asset in this period.</p> : (
              <table className="w-full text-sm">
                <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2">Asset</th><th className="px-4 py-2 text-right">Maintenance</th><th className="px-4 py-2 text-right">Of its cost</th></tr></thead>
                <tbody className="divide-y divide-line">
                  {data.maintenance.byAsset.map((a) => (
                    <tr key={a.id}>
                      <td className="px-4 py-2"><Link className="hover:underline" to={`/assets?id=${a.id}`}><code className="text-xs text-muted">{a.tag}</code> {a.name}</Link><span className="block text-xs text-muted">{a.type}</span></td>
                      <td className="num px-4 py-2 text-right">{ksh(a.cost)}</td>
                      <td className={cn('num px-4 py-2 text-right', (a.share ?? 0) >= 50 && 'font-semibold text-bad')}>{a.share === null ? '—' : `${a.share}%`}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
            {data.maintenance.byAsset.some((a) => (a.share ?? 0) >= 50) && <p className="border-t border-line px-4 py-2 text-xs text-muted">Maintenance at half of what an asset cost, or more, is usually the point to replace it rather than keep repairing it.</p>}
          </section>
        </div>

        <div className="grid gap-4 lg:grid-cols-3">
          <section className="card p-4 text-sm">
            <p className="mb-3 font-semibold">Buying, where it stands</p>
            <dl className="space-y-2">
              {([
                ['Requests waiting for approval', p.awaitingApproval.count, p.awaitingApproval.value, '/procurement'],
                ['Approved, not yet bought', p.approvedNotBought.count, p.approvedNotBought.value, '/procurement'],
                ['Orders not yet billed', p.openOrders.count, p.openOrders.value, '/purchases'],
                ['Bills not yet paid', p.unpaidBills.count, p.unpaidBills.value, '/purchases'],
              ] as [string, number, number, string][]).map(([k, n, v, to]) => (
                <div key={k} className="flex items-center justify-between gap-2"><dt><Link className="hover:underline" to={to}>{k}</Link> <span className="text-muted">({n})</span></dt><dd className="num font-semibold">{ksh(v)}</dd></div>
              ))}
              {p.unpaidBills.overdue > 0 && <div className="flex justify-between text-bad"><dt>of which overdue</dt><dd className="num font-semibold">{ksh(p.unpaidBills.overdue)}</dd></div>}
              <div className="flex justify-between text-muted"><dt><Link className="hover:underline" to="/procurement/tenders">Tenders running</Link></dt><dd className="num">{p.tenders}</dd></div>
            </dl>
          </section>
          <section className="card p-4 text-sm">
            <p className="mb-3 font-semibold">Jobs closed: estimate against cost</p>
            {data.jobs.closed === 0 ? <p className="text-muted">No work orders were closed in this period.</p> : (
              <>
                <p><span className="num text-xl font-semibold">{ksh(data.jobs.actual)}</span> <span className="text-muted">against {ksh(data.jobs.estimated)} estimated, {data.jobs.closed} jobs{!data.jobs.labourIncluded && ' (without labour)'}</span></p>
                {data.jobs.overEstimate.length > 0 && (
                  <ul className="mt-3 space-y-1.5">
                    {data.jobs.overEstimate.map((j) => <li key={j.id} className="flex justify-between gap-2"><Link className="truncate hover:underline" to={`/maintenance?wo=${j.id}`}>{j.number} · {j.title}</Link><span className="num shrink-0 text-bad">+{ksh(j.actual - j.estimate)}</span></li>)}
                  </ul>
                )}
              </>
            )}
          </section>
          <section className="card p-4 text-sm">
            <p className="mb-3 font-semibold">Stock</p>
            <p><span className="num text-xl font-semibold">{ksh(data.stock.value)}</span> <span className="text-muted">on hand, at average cost</span></p>
            <p className="mt-2 text-muted">{data.stock.takes === 0 ? <>No stock takes posted in this period. <Link className="text-brand hover:underline" to="/stock/takes">Count a store</Link>.</> : <>{data.stock.takes} stock take{data.stock.takes === 1 ? '' : 's'} found <span className={cn('num font-semibold', data.stock.variance < 0 ? 'text-bad' : 'text-good')}>{ksh(data.stock.variance)}</span> in differences.</>}</p>
          </section>
        </div>

        <section className="card overflow-hidden">
          <p className="border-b border-line px-4 py-3 font-semibold">Where the money went</p>
          <table className="w-full text-sm">
            <tbody className="divide-y divide-line">
              {data.topAccounts.map((a) => <tr key={a.code}><td className="w-20 px-4 py-2 text-muted">{a.code}</td><td className="px-4 py-2">{a.name}</td><td className="num px-4 py-2 text-right">{ksh(a.amount)}</td></tr>)}
              {data.topAccounts.length === 0 && <tr><td className="px-4 py-3 text-muted">No expenses in this period.</td></tr>}
            </tbody>
          </table>
        </section>
      </div>
    </div>
  );
}

/** One series by month: thin bars from a common baseline, the value on hover and on the tallest bar. */
function MonthBars({ rows }: { rows: [string, number][] }) {
  const max = Math.max(1, ...rows.map((r) => r[1]));
  const top = rows.reduce((b, r) => (r[1] > b[1] ? r : b), rows[0] ?? ['', 0]);
  if (rows.every((r) => r[1] === 0)) return <p className="text-sm text-muted">Nothing booked yet.</p>;
  return (
    <div>
      <div className="flex h-40 items-end gap-1.5 border-b border-line" role="img" aria-label="Maintenance cost by month">
        {rows.map(([m, v]) => (
          <div key={m} className="group relative flex h-full flex-1 flex-col items-center justify-end">
            {m === top[0] && v > 0 && <span className="num mb-1 text-[11px] text-muted">{kshShort(v)}</span>}
            <div className="w-full max-w-10 rounded-t bg-brand transition group-hover:brightness-110" style={{ height: `${Math.max(v > 0 ? 2 : 0, (100 * v) / max)}%` }} />
            <span className="pointer-events-none absolute -top-7 z-10 hidden rounded bg-ink px-2 py-1 text-xs whitespace-nowrap text-white group-hover:block">{new Date(`${m}T00:00:00`).toLocaleDateString('en-GB', { month: 'short', year: 'numeric' })}: {ksh(v)}</span>
          </div>
        ))}
      </div>
      <div className="mt-1 flex gap-1.5">{rows.map(([m]) => <span key={m} className="flex-1 text-center text-[11px] text-muted">{new Date(`${m}T00:00:00`).toLocaleDateString('en-GB', { month: 'short' })}</span>)}</div>
    </div>
  );
}
