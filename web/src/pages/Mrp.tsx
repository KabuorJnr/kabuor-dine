// The material plan (MRP): for everything the business makes, the forecast demand less what's on the shelf and already
// planned gives what to make; the recipes give the materials that takes, less stock and what's on order, gives what to
// buy and by when. Chosen materials become a request, which goes through procurement and finance like any other.
import { useState } from 'react';
import { Link } from 'react-router';
import { ClipboardList } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, day, ksh } from '../lib/format';
import { Empty, ErrorNote, Loading, PageError, PageHeader, Stat, Status } from '../components/ui';

type Product = { itemId: string; name: string; unit: string; recipe: string; demand: number[]; demandFrom: string; onHand: number; planned: number; safety: number; toMake: number; batches: number; outputPerBatch: number };
type Material = { itemId: string; name: string; unit: string; use: string; needed: number; onHand: number; onOrder: number; toBuy: number; orderBy: string; unitCost: number; value: number; for: string[] };
type View = { months: string[]; products: Product[]; materials: Material[]; toBuyValue: number; toMake: number; leadDays: number };

export default function Mrp({ embedded }: { embedded?: boolean }) {
  const [months, setMonths] = useState(3);
  const { data, error } = useApi<View>(`/os/mrp?months=${months}`);
  const departments = useApi<{ id: string; name: string; active?: boolean }[]>('/departments');
  const [picked, setPicked] = useState<Set<string>>(new Set());
  const [dept, setDept] = useState('');
  const [state, setState] = useState<{ busy: boolean; error: string; made?: { id: string; number: string } }>({ busy: false, error: '' });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const buy = data.materials.filter((m) => m.toBuy > 0);
  const raise = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await api<{ id: string; number: string }>('/os/mrp/request', { body: { departmentId: dept, lines: buy.filter((m) => picked.has(m.itemId)).map((m) => ({ itemId: m.itemId, quantity: m.toBuy })) } });
      setState({ busy: false, error: '', made: r });
      setPicked(new Set());
    } catch (e) { setState({ busy: false, error: (e as Error).message }); }
  };
  return (
    <div className="space-y-5">
      {!embedded && <PageHeader print title="What to make & buy" />}
      <div className="flex flex-wrap items-center justify-between gap-3 rounded-lg bg-paper p-4 text-sm ring-1 ring-line">
        <p className="max-w-3xl"><b>How this works:</b> from your own sales, Kabuor forecasts how much of each product you’ll sell. Less what’s on the shelf and already planned, that’s what to make. Each blueprint then says what those batches take; less what’s in stock and on order, that’s what to buy, and by when.</p>
        <select className="field w-auto" value={months} onChange={(e) => setMonths(Number(e.target.value))} aria-label="Plan ahead">{[1, 2, 3, 6].map((m) => <option key={m} value={m}>Next {m} month{m > 1 ? 's' : ''}</option>)}</select>
      </div>
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-3">
        <Stat label="Products to make" value={data.toMake} />
        <Stat label="Materials to buy" value={buy.length} />
        <Stat label="Value to buy" value={ksh(data.toBuyValue)} />
      </div>
      {data.products.length === 0 ? (
        <Empty title="No blueprints yet" action={<Link className="btn btn-primary" to="/production">Add a blueprint</Link>}>This plan works from blueprints: what each product is made of. Add them under Blueprints.</Empty>
      ) : (
        <section className="card overflow-x-auto">
          <table className="w-full min-w-[46rem] text-sm">
            <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2.5">Product</th>{data.months.map((m) => <th key={m} className="px-3 py-2.5 text-right">{m}</th>)}<th className="px-3 py-2.5 text-right">On hand</th><th className="px-3 py-2.5 text-right">Planned</th><th className="px-3 py-2.5 text-right">To make</th></tr></thead>
            <tbody className="divide-y divide-line">
              {data.products.map((p) => (
                <tr key={p.itemId}>
                  <td className="px-4 py-3"><b>{p.name}</b><span className="block text-xs text-muted">{p.demandFrom}</span></td>
                  {p.demand.map((d, i) => <td key={i} className="num px-3 py-3 text-right">{d}</td>)}
                  <td className="num px-3 py-3 text-right">{p.onHand}</td>
                  <td className="num px-3 py-3 text-right">{p.planned || '—'}</td>
                  <td className="px-3 py-3 text-right">{p.toMake > 0 ? <b className="num">{p.toMake} {p.unit}<span className="block text-xs font-normal text-muted">{p.batches} batch{p.batches === 1 ? '' : 'es'} of {p.outputPerBatch}</span></b> : <Status tone="good">Covered</Status>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
      {data.materials.length > 0 && (
        <section className="space-y-3">
          <h2 className="font-semibold">Materials</h2>
          <div className="card overflow-x-auto">
            <table className="w-full min-w-[46rem] text-sm">
              <thead className="bg-paper text-left text-xs text-muted"><tr><th className="w-8 px-3 py-2.5" /><th className="px-3 py-2.5">Material</th><th className="px-3 py-2.5 text-right">Needed</th><th className="px-3 py-2.5 text-right">On hand</th><th className="px-3 py-2.5 text-right">On order</th><th className="px-3 py-2.5 text-right">To buy</th><th className="px-3 py-2.5">Order by</th><th className="px-3 py-2.5 text-right">Value</th></tr></thead>
              <tbody className="divide-y divide-line">
                {data.materials.map((m) => (
                  <tr key={m.itemId} className={cn(m.toBuy === 0 && 'text-muted')}>
                    <td className="px-3 py-2.5">{m.toBuy > 0 && <input type="checkbox" className="h-4 w-4" aria-label={`Buy ${m.name}`} checked={picked.has(m.itemId)} onChange={(e) => { const s = new Set(picked); if (e.target.checked) s.add(m.itemId); else s.delete(m.itemId); setPicked(s); }} />}</td>
                    <td className="px-3 py-2.5"><b>{m.name}</b><span className="block text-xs text-muted">for {m.for.join(', ')}</span></td>
                    <td className="num px-3 py-2.5 text-right">{m.needed} {m.unit}</td>
                    <td className="num px-3 py-2.5 text-right">{m.onHand}</td>
                    <td className="num px-3 py-2.5 text-right">{m.onOrder || '—'}</td>
                    <td className="num px-3 py-2.5 text-right font-semibold">{m.toBuy || '—'}</td>
                    <td className={cn('px-3 py-2.5', m.toBuy > 0 && m.orderBy <= new Date().toISOString().slice(0, 10) && 'font-semibold text-bad')}>{m.toBuy > 0 ? day(m.orderBy) : ''}</td>
                    <td className="num px-3 py-2.5 text-right">{m.value ? ksh(m.value) : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          {buy.length > 0 && (
            <div className="card flex flex-wrap items-center gap-2 p-3">
              <ClipboardList className="h-4 w-4 text-muted" />
              <span className="text-sm">Raise a request for the {picked.size || 'chosen'} material(s), from</span>
              <select className="field w-auto" value={dept} onChange={(e) => setDept(e.target.value)} aria-label="Department"><option value="">Choose a department…</option>{(departments.data ?? []).filter((d) => d.active !== false).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}</select>
              <button className="btn btn-primary" disabled={state.busy || !dept || picked.size === 0} onClick={raise}>Raise request</button>
              <button className="btn btn-ghost" onClick={() => setPicked(new Set(buy.map((m) => m.itemId)))}>Choose all</button>
              {state.made && <span className="text-sm">Made <Link className="font-semibold text-brand hover:underline" to={`/procurement?id=${state.made.id}`}>{state.made.number}</Link>: it goes to procurement, then finance.</span>}
            </div>
          )}
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <p className="text-xs text-muted">Order-by dates assume about {data.leadDays} days from order to delivery.</p>
        </section>
      )}
    </div>
  );
}
