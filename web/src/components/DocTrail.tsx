// The document trail: everything this piece of business touched, stage by stage, from the first contact to the books.
// Each card links to the document; the current one is highlighted.
import { Link } from 'react-router';
import { ArrowRight, Route } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, day, ksh } from '../lib/format';

type Trail = { start: string; count: number; stages: { stage: string; items: { kind: string; id: string; number: string; title: string; status: string; at: string; amount: number; to: string; current: boolean }[] }[] };

export default function DocTrail({ kind, id, className }: { kind: 'invoice' | 'quote' | 'purchase' | 'request' | 'workorder' | 'tender' | 'lead' | 'advance' | 'fault' | 'payment'; id: string; className?: string }) {
  const { data } = useApi<Trail>(`/trace/${kind}/${id}`);
  if (!data || data.count <= 1) return null;
  return (
    <section className={cn('print:hidden', className)} aria-label="Document trail">
      <p className="mb-2 flex items-center gap-2 text-sm font-semibold"><Route className="h-4 w-4 text-muted" /> Where this came from and where it went</p>
      <ol className="flex gap-2 overflow-x-auto pb-1">
        {data.stages.map((s, i) => (
          <li key={s.stage} className="flex shrink-0 items-start gap-2">
            {i > 0 && <ArrowRight className="mt-6 h-4 w-4 shrink-0 text-faint" aria-hidden />}
            <div className="w-48 space-y-1.5">
              <p className="text-2xs font-semibold tracking-wider text-muted uppercase">{s.stage}</p>
              {s.items.slice(0, 4).map((n) => (
                <Link key={n.id} to={n.to} className={cn('block rounded-md p-2 text-xs ring-1 transition hover:bg-paper', n.current ? 'bg-brand-soft ring-brand' : 'bg-card ring-line')}>
                  <span className="flex items-baseline justify-between gap-2"><b className="truncate">{n.number || n.title}</b>{n.amount > 0 && <span className="num shrink-0">{ksh(n.amount)}</span>}</span>
                  {n.number && <span className="block truncate text-muted">{n.title}</span>}
                  <span className="block text-faint">{n.status} · {day(n.at.slice(0, 10))}</span>
                </Link>
              ))}
              {s.items.length > 4 && <p className="text-2xs text-muted">and {s.items.length - 4} more</p>}
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}
