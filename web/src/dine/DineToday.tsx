// The restaurant's day (or week, or month) for the owner: what was sold and collected, how many tables and how big
// the average bill, how fast the kitchen was, the best sellers, the busy hours, and what guests said.
import { useState } from 'react';
import { Star } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { Chips, Loading, PageError, PageHeader, Stat } from '../components/ui';

type Report = {
  days: number; from: string; sales: number; paid: number; orders: number; cancelled: number; visits: number; averageBill: number;
  prepMinutes: number | null; slowest: { number: string; table: string; minutes: number }[]; fromGuests: number;
  rating: number | null; ratings: number; comments: { rating: number; feedback: string; table: string; openedAt: string }[];
  top: { name: string; quantity: number; sales: number }[]; byHour: { hour: number; orders: number; sales: number }[];
  byStation: { station: string; quantity: number; sales: number }[];
};

export default function DineToday() {
  const [days, setDays] = useState<'1' | '7' | '30'>('1');
  const { data, error } = useApi<Report>(`/dine/report?days=${days}`);
  if (error && !data) return <PageError>{error}</PageError>;
  const peak = Math.max(1, ...(data?.byHour ?? []).map((h) => h.sales));
  const topMax = Math.max(1, ...(data?.top ?? []).map((t) => t.quantity));

  return (
    <div className="space-y-5">
      <PageHeader title={days === '1' ? 'Today' : `Last ${days} days`} subtitle="Sales, tables, the kitchen and what guests said." print={`Kabuor Dine report, ${days === '1' ? 'today' : `last ${days} days`}`}
        actions={<Chips label="Period" value={days} onChange={setDays} options={[['1', 'Today'], ['7', '7 days'], ['30', '30 days']]} />} />
      {!data ? <Loading /> : (
        <>
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <Stat label="Sales" value={ksh(data.sales)}>{ksh(data.paid)} collected</Stat>
            <Stat label="Tables served" value={data.visits}>{data.orders} orders{data.cancelled ? ` · ${data.cancelled} cancelled` : ''}</Stat>
            <Stat label="Average bill" value={ksh(data.averageBill)}>{data.orders ? `${Math.round((data.fromGuests / data.orders) * 100)}% ordered by QR` : 'per table'}</Stat>
            <Stat label="Kitchen time" value={data.prepMinutes == null ? '—' : `${data.prepMinutes} min`} tone={data.prepMinutes != null && data.prepMinutes > 20 ? 'bad' : undefined}>from order to ready</Stat>
          </div>

          <div className="grid gap-4 lg:grid-cols-2">
            <section className="card p-4">
              <h2 className="font-bold">Best sellers</h2>
              {data.top.length === 0 ? <p className="mt-3 text-sm text-muted">No orders yet.</p> : (
                <ul className="mt-3 space-y-2.5">
                  {data.top.map((t) => (
                    <li key={t.name}>
                      <div className="flex justify-between gap-3 text-sm"><span className="font-medium">{t.name}</span><span className="num text-muted">{t.quantity} · {ksh(t.sales)}</span></div>
                      <div className="mt-1 h-2 rounded-full bg-paper"><div className="h-2 rounded-full bg-dine" style={{ width: `${(t.quantity / topMax) * 100}%` }} /></div>
                    </li>
                  ))}
                </ul>
              )}
            </section>

            <section className="card p-4">
              <h2 className="font-bold">Busy hours</h2>
              {data.byHour.length === 0 ? <p className="mt-3 text-sm text-muted">No orders yet.</p> : (
                <div className="mt-4 flex h-40 items-end gap-1.5" role="img" aria-label="Sales by hour">
                  {data.byHour.map((h) => (
                    <div key={h.hour} className="flex flex-1 flex-col items-center gap-1" title={`${h.hour}:00 · ${h.orders} orders · ${ksh(h.sales)}`}>
                      <div className="w-full max-w-10 rounded-t bg-dine/80" style={{ height: `${Math.max(4, (h.sales / peak) * 128)}px` }} />
                      <span className="text-[11px] text-muted">{h.hour === 0 ? '12a' : h.hour < 12 ? `${h.hour}a` : h.hour === 12 ? '12p' : `${h.hour - 12}p`}</span>
                    </div>
                  ))}
                </div>
              )}
              {data.byStation.length > 0 && <p className="mt-3 text-sm text-muted">{data.byStation.map((s) => `${s.station}: ${s.quantity} items, ${ksh(s.sales)}`).join(' · ')}</p>}
            </section>

            <section className="card p-4">
              <h2 className="font-bold">The kitchen</h2>
              <p className="mt-1 text-sm text-muted">Average {data.prepMinutes == null ? '—' : `${data.prepMinutes} minutes`} from order to ready.</p>
              {data.slowest.length > 0 && (
                <ul className="mt-3 divide-y divide-line text-sm">
                  {data.slowest.map((o) => <li key={o.number} className="flex justify-between py-2"><span>{o.number} · {o.table}</span><span className={cn('num font-semibold', o.minutes > 20 && 'text-bad')}>{o.minutes} min</span></li>)}
                </ul>
              )}
            </section>

            <section className="card p-4">
              <h2 className="flex items-center gap-2 font-bold">What guests said {data.rating != null && <span className="flex items-center gap-1 text-sm font-semibold text-ink-2"><Star className="h-4 w-4 fill-gold text-gold" /> {data.rating} from {data.ratings}</span>}</h2>
              {data.comments.length === 0 ? <p className="mt-3 text-sm text-muted">Guests rate their visit on their phone after paying. Their comments appear here.</p> : (
                <ul className="mt-3 space-y-3">
                  {data.comments.map((c, i) => (
                    <li key={i} className={cn('rounded-lg p-3 text-sm', c.rating <= 2 ? 'bg-bad-soft' : 'bg-paper')}>
                      <p><span className="text-gold">{'★'.repeat(c.rating)}</span><span className="text-line">{'★'.repeat(5 - c.rating)}</span> <span className="text-xs text-muted">· {c.table}</span></p>
                      <p className="mt-1">“{c.feedback}”</p>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </div>
        </>
      )}
    </div>
  );
}
