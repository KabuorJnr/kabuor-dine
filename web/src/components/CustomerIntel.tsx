// A customer, profiled and traced: what kind of customer they are (and what to do about it), what they're worth,
// how they pay, what they buy, where they came from, and their whole journey from the first click to the stock
// that left the shelf for them.
import { useState } from 'react';
import { Link } from 'react-router';
import { ChevronDown } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, dateTime, day, ksh } from '../lib/format';
import { Loading, Status } from './ui';

const ADVICE: Record<string, string> = {
  Champions: 'One of your best: thank them and ask who else they know.', Loyal: 'Buys often: a price list could keep them.', 'Big spenders': 'Spends the most: look after them personally.',
  Promising: 'Could buy more: follow up after each order.', New: 'New: make their second order easy.', 'At risk': 'Used to buy often and has gone quiet: call them.', Sleeping: 'Hasn’t bought in a while: an offer might bring them back.',
  'Never bought': 'Hasn’t bought yet: follow up their inquiry.',
};

export function CustomerProfile({ id }: { id: string }) {
  const { data } = useApi<any>(`/insights/customer/${id}`);
  if (!data) return null;
  return (
    <section className="card space-y-3 p-4">
      <div className="flex items-center justify-between gap-2"><h2 className="text-sm font-semibold text-muted">Profile</h2><Status tone={data.segment === 'At risk' ? 'warn' : data.segment === 'Champions' ? 'good' : 'plain'}>{data.segment}</Status></div>
      <p className="text-sm">{ADVICE[data.segment]}</p>
      <dl className="grid grid-cols-2 gap-2 text-sm">
        <div><dt className="text-xs text-muted">Bought in total</dt><dd className="num font-semibold">{ksh(data.lifetimeValue)}</dd></div>
        <div><dt className="text-xs text-muted">This year</dt><dd className="num font-semibold">{ksh(data.yearValue)} · {data.invoicesThisYear} inv.</dd></div>
        <div><dt className="text-xs text-muted">Pays in</dt><dd className="font-semibold">{data.averageDaysToPay == null ? '—' : `${data.averageDaysToPay} days`}</dd></div>
        <div><dt className="text-xs text-muted">Came from</dt><dd className="font-semibold">{data.cameFrom.link ? `${data.cameFrom.link}` : data.cameFrom.channel ?? 'Not known'}{data.cameFrom.by && <span className="block text-xs font-normal text-muted">{data.cameFrom.by}</span>}</dd></div>
      </dl>
      {data.favourites.length > 0 && <p className="text-sm"><span className="text-xs text-muted">Buys most: </span>{data.favourites.map((f: any) => f.name).join(', ')}</p>}
      {data.inquiries.total > 0 && <p className="text-xs text-muted">{data.inquiries.total} inquir{data.inquiries.total === 1 ? 'y' : 'ies'}: {data.inquiries.won} bought, {data.inquiries.lost} lost{data.lastPortalVisit && ` · last on the portal ${day(data.lastPortalVisit)}`}</p>}
    </section>
  );
}

const DOT: Record<string, string> = { found: 'bg-indigo', inquiry: 'bg-gold', reply: 'bg-brand', invoice: 'bg-good', payment: 'bg-good', stock: 'bg-ink', supply: 'bg-faint', lost: 'bg-bad', won: 'bg-good' };

export function CustomerJourney({ id }: { id: string }) {
  const [open, setOpen] = useState(false);
  const { data } = useApi<any>(open ? `/insights/journey/${id}` : null);
  return (
    <section className="card p-4">
      <button className="flex w-full items-center justify-between text-left" onClick={() => setOpen(!open)} aria-expanded={open}>
        <span><h2 className="text-sm font-semibold">Journey</h2><span className="text-xs text-muted">From the first click to what left the shelf</span></span>
        <ChevronDown className={cn('h-4 w-4 transition', open && 'rotate-180')} />
      </button>
      {open && (!data ? <Loading className="py-6" /> : (
        <ol className="mt-3 space-y-2.5 border-l-2 border-line pl-4">
          {data.events.map((e: any, i: number) => (
            <li key={i} className="relative text-sm">
              <span className={cn('absolute top-1.5 -left-[21px] h-2.5 w-2.5 rounded-full', DOT[e.kind] ?? 'bg-line')} />
              <span className="text-xs text-faint">{dateTime(e.at)}</span>
              <p>{e.link ? <Link to={e.link} className="hover:underline">{e.text}</Link> : e.text}</p>
            </li>
          ))}
        </ol>
      ))}
    </section>
  );
}
