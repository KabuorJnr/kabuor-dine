// Leads as analytics: how many come in, how far they get, which sources bring customers who actually pay, how long
// winning takes, and why leads are lost. Read from the pipeline, the inquiries and the invoices of won customers.
import { useState } from 'react';
import { Link } from 'react-router';
import { useApi } from '../lib/api';
import { ksh } from '../lib/format';
import { MODULES } from '../lib/modules';
import { Chips, Empty, Loading, PageError, Stat } from '../components/ui';

type LeadsData = {
  total: number; won: number; lost: number; open: number; winRate: number | null; pipeline: number; daysToWin: number | null; wonRevenue: number;
  funnel: { stage: string; count: number }[];
  sources: { source: string; leads: number; won: number; winRate: number | null; revenue: number }[];
  lostReasons: { reason: string; count: number }[];
  owners: { owner: string; leads: number; won: number }[];
};
const STAGE: Record<string, string> = { New: 'Came in', Contacted: 'Contacted', Quoted: 'Quoted', Won: 'Won' };
const SOURCE: Record<string, string> = { WalkIn: 'Walk-in', WhatsApp: 'WhatsApp', Call: 'Phone call' };

export default function Leads() {
  const [days, setDays] = useState('90');
  const to = new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 10);
  const from = new Date(Date.now() + 3 * 3600e3 - (Number(days) - 1) * 864e5).toISOString().slice(0, 10);
  const { data, error } = useApi<LeadsData>(`/insights/leads?from=${from}&to=${to}`);
  const color = MODULES.sales.color;
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const top = Math.max(1, ...data.funnel.map((f) => f.count));
  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="max-w-2xl text-sm text-muted">Where customers come from, and which sources bring the ones who pay. Leads come from the pipeline, inquiries and tracking links.</p>
        <Chips label="Period" value={days} onChange={setDays} options={[['30', '30 days'], ['90', '90 days'], ['365', 'Year']]} />
      </div>
      {data.total === 0 ? (
        <Empty title="No leads in this period" action={<Link className="btn btn-primary" to="/pipeline">Open the pipeline</Link>}>Add leads in the pipeline (or let inquiries and tracking links create them) and this page fills in.</Empty>
      ) : (
        <>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Stat label="Leads" value={data.total}>{data.open} still open</Stat>
            <Stat label="Win rate" value={data.winRate === null ? '–' : `${data.winRate}%`}>{data.won} won · {data.lost} lost</Stat>
            <Stat label="Days to win" value={data.daysToWin ?? '–'}>from first contact</Stat>
            <Stat label="Won customers have paid" value={ksh(data.wonRevenue)}>pipeline open: {ksh(data.pipeline)}</Stat>
          </div>
          <div className="grid grid-cols-1 gap-4 xl:grid-cols-2">
            <section className="card min-w-0 p-4 sm:p-5">
              <h2 className="font-semibold">How far leads get</h2>
              <div className="mt-3 space-y-2">
                {data.funnel.map((f, i) => {
                  const prev = i > 0 ? data.funnel[i - 1].count : null;
                  return (
                    <div key={f.stage} className="grid grid-cols-[6.5rem_1fr_5rem] items-center gap-2 text-sm" title={`${STAGE[f.stage] ?? f.stage}: ${f.count}`}>
                      <span className="text-muted">{STAGE[f.stage] ?? f.stage}</span>
                      <div className="h-6 rounded bg-paper ring-1 ring-line"><div className="h-6 rounded" style={{ width: `${(f.count / top) * 100}%`, background: color, minWidth: f.count ? 4 : 0 }} /></div>
                      <span className="num text-right"><b>{f.count}</b>{prev ? <span className="ml-1 text-xs text-muted">{Math.round((100 * f.count) / prev)}%</span> : null}</span>
                    </div>
                  );
                })}
              </div>
            </section>
            <section className="card min-w-0 p-4 sm:p-5">
              <h2 className="font-semibold">Why leads are lost</h2>
              {data.lostReasons.length === 0 ? <p className="mt-3 text-sm text-muted">None lost in this period.</p> : (
                <ul className="mt-3 space-y-2 text-sm">
                  {data.lostReasons.map((r) => <li key={r.reason} className="flex justify-between gap-3"><span className="min-w-0 truncate">{r.reason}</span><b className="num">{r.count}</b></li>)}
                </ul>
              )}
              <h2 className="mt-5 font-semibold">Who wins them</h2>
              <ul className="mt-2 space-y-1 text-sm">
                {data.owners.map((o) => <li key={o.owner} className="flex justify-between gap-3"><span>{o.owner}</span><span className="num">{o.won} of {o.leads}</span></li>)}
              </ul>
            </section>
          </div>
          <section className="card overflow-x-auto">
            <table className="w-full min-w-[34rem] text-sm">
              <thead className="bg-paper text-left text-xs text-muted"><tr><th className="px-4 py-2.5">Source</th><th className="px-4 py-2.5 text-right">Leads</th><th className="px-4 py-2.5 text-right">Won</th><th className="px-4 py-2.5 text-right">Win rate</th><th className="px-4 py-2.5 text-right">Won customers have paid</th></tr></thead>
              <tbody className="divide-y divide-line">
                {data.sources.map((s) => (
                  <tr key={s.source}>
                    <td className="px-4 py-2.5 font-semibold">{SOURCE[s.source] ?? s.source}</td>
                    <td className="num px-4 py-2.5 text-right">{s.leads}</td>
                    <td className="num px-4 py-2.5 text-right">{s.won}</td>
                    <td className="num px-4 py-2.5 text-right">{s.winRate === null ? '–' : `${s.winRate}%`}</td>
                    <td className="num px-4 py-2.5 text-right font-semibold">{ksh(s.revenue)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </section>
        </>
      )}
    </div>
  );
}
