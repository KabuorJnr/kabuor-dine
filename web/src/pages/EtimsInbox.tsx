// Supplier invoices from eTIMS: sales suppliers signed naming you as the buyer. KRA now checks every expense in a
// return against these, so each one needs a decision: it's ours (make the bill, claim the VAT) or it isn't (reject,
// and the supplier must credit it). The list shows what and how much, so the decision takes one look.
import { useState } from 'react';
import { Link } from 'react-router';
import { Check, Loader2, RefreshCw, X } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { day, ksh } from '../lib/format';
import { Chips, Empty, ErrorNote, Loading, PageHeader, Status, askDialog } from '../components/ui';

export default function EtimsInbox() {
  const [status, setStatus] = useState<'new' | 'accepted' | 'rejected'>('new');
  const { data, error, reload } = useApi<any>(`/etims/purchases?status=${status}`);
  const [state, setState] = useState({ busy: '', error: '', note: '' });
  const run = async (key: string, fn: () => Promise<any>, note?: (r: any) => string) => {
    setState({ busy: key, error: '', note: '' });
    try {
      const r = await fn();
      setState({ busy: '', error: '', note: note?.(r) ?? '' });
      reload();
    } catch (e) {
      setState({ busy: '', error: (e as Error).message, note: '' });
    }
  };
  return (
    <div>
      <PageHeader
        title="Supplier invoices from eTIMS"
        subtitle="Invoices your suppliers signed with KRA naming you as the buyer. Accept the ones that are yours: they become bills and their VAT is claimable."
        actions={
          <button className="btn btn-primary" disabled={Boolean(state.busy)} onClick={() => run('sync', () => api('/etims/purchases/sync', { body: {} }), (r) => (r.received ? `${r.received} new invoice${r.received === 1 ? '' : 's'} from KRA.` : 'Nothing new from KRA.'))}>
            {state.busy === 'sync' ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />} Check KRA for new ones
          </button>
        }
      />
      <div className="mb-4">
        <Chips label="Show" value={status} onChange={setStatus} options={[['new', 'To review', data?.counts.waiting], ['accepted', 'Accepted', data?.counts.accepted], ['rejected', 'Rejected', data?.counts.rejected]]} />
      </div>
      {state.note && <p className="mb-3 rounded-md bg-good-soft px-4 py-2.5 text-sm font-medium text-good">{state.note}</p>}
      {state.error && <div className="mb-3"><ErrorNote>{state.error}</ErrorNote></div>}
      {error ? <ErrorNote>{error}</ErrorNote> : !data ? <Loading /> : data.items.length === 0 ? (
        <Empty title={status === 'new' ? 'Nothing to review' : 'None yet'}>{status === 'new' ? 'Check KRA to fetch invoices suppliers have signed for you since last time. (eTIMS must be connected in Settings.)' : undefined}</Empty>
      ) : (
        <ul className="space-y-3">
          {data.items.map((p: any) => {
            const lines = JSON.parse(p.lines || '[]');
            return (
              <li key={p.id} className="card p-4">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <span>
                    <b className="block">{p.supplierName || p.supplierPin}</b>
                    <span className="text-xs text-muted">PIN {p.supplierPin} · invoice {p.supplierInvoiceNo} · {day(p.date)}</span>
                  </span>
                  <span className="text-right">
                    <b className="num block text-lg">{ksh(p.total)}</b>
                    <span className="num text-xs text-muted">VAT {ksh(p.tax)}</span>
                  </span>
                </div>
                <ul className="mt-2 space-y-0.5 text-sm text-ink-2">
                  {lines.slice(0, 6).map((l: any, i: number) => (
                    <li key={i} className="flex justify-between gap-3"><span className="min-w-0 truncate">{l.quantity} × {l.name}</span><span className="num shrink-0">{ksh(l.total)}</span></li>
                  ))}
                  {lines.length > 6 && <li className="text-xs text-muted">and {lines.length - 6} more</li>}
                </ul>
                {p.note && <p className="mt-2 text-xs text-muted">{p.note}</p>}
                <div className="mt-3 flex flex-wrap gap-2">
                  {p.status === 'New' ? (
                    <>
                      <button className="btn btn-primary" disabled={Boolean(state.busy)} onClick={() => run(p.id, () => api(`/etims/purchases/${p.id}/accept`, { body: {} }), (r) => `Bill ${r.number} made and posted.`)}>
                        {state.busy === p.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Check className="h-4 w-4" />} It’s ours: make the bill
                      </button>
                      <button className="btn btn-ghost" disabled={Boolean(state.busy)} onClick={async () => { const reason = await askDialog({ title: 'Not your purchase?', label: 'Why isn’t it yours?', placeholder: 'e.g. wrong buyer, wrong amount', required: true, confirm: 'Reject' }); if (reason) run(p.id, () => api(`/etims/purchases/${p.id}/reject`, { body: { reason } })); }}>
                        <X className="h-4 w-4" /> Not ours
                      </button>
                    </>
                  ) : p.status === 'Accepted' && p.billId ? (
                    <Link to={`/purchases/${p.billId}`} className="btn btn-ghost">Open the bill</Link>
                  ) : (
                    <Status tone="plain">Rejected</Status>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
