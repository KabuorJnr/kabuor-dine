// Stock takes: counting a whole store. The storekeeper is deciding "does what's on the shelves match the books, and if
// not, by how much?", so a count is started (the system notes what it expects), counters enter what they find (blind:
// they don't see the expected figure), and the differences are reviewed, with their value, before they're posted.
import { useMemo, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ClipboardCheck, Eye, Plus } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dateTime, ksh } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageError, PageHeader, SearchBox, Sheet, SheetFooter, Stat, Status, confirmDialog } from '../components/ui';

type TakeRow = { id: string; number: string; location: string; status: 'Counting' | 'Posted' | 'Cancelled'; blind: boolean; startedBy: string; startedAt: string; postedBy: string; postedAt: string | null; varianceValue: number; counted: number; total: number; accuracy: number | null };
type Take = {
  id: string; number: string; location: string; status: TakeRow['status']; blind: boolean; note: string; startedBy: string; startedAt: string; postedBy: string; postedAt: string | null; varianceValue: number;
  showsExpected: boolean; progress: { counted: number; total: number };
  summary: { matched: number; short_: number; over: number; value: number; accuracy: number | null } | null;
  lines: { itemId: string; name: string; sku: string; barcode: string; unit: string; counted: number | null; countedBy: string; countedAt: string | null; expected: number | null; difference: number | null; value: number | null }[];
};

export default function StockTakes() {
  const session = useSession();
  const canCount = can(session, 'stock');
  const [params, setParams] = useSearchParams();
  const { data, error, reload } = useApi<TakeRow[]>('/stock-takes');
  const [starting, setStarting] = useState(false);
  const openId = params.get('id');
  const open = (id: string | null) => setParams(id ? { id } : {}, { replace: true });
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div>
      <PageHeader print
        title="Stock takes"
        subtitle="Count a store, review what differs, and post it: stock and the books then match the shelves."
        actions={canCount && <button className="btn btn-primary" onClick={() => setStarting(true)}><Plus className="h-4 w-4" /> Start a count</button>}
      />
      {data.length === 0 ? (
        <Empty title="No stock takes yet" action={canCount && <button className="btn btn-primary" onClick={() => setStarting(true)}>Start the first count</button>}>
          A full count once a quarter (and a quick one of fast movers every month) keeps losses visible.
        </Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {data.map((t) => (
            <li key={t.id}>
              <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => open(t.id)}>
                <ClipboardCheck className="h-4 w-4 shrink-0 text-faint" />
                <span className="min-w-0 flex-1">
                  <span className="flex items-center gap-2"><code className="text-xs font-semibold text-muted">{t.number}</code><b className="text-sm">{t.location}</b>{t.blind && <span className="text-xs text-muted">blind</span>}</span>
                  <span className="mt-0.5 block text-sm text-muted">{t.status === 'Counting' ? `${t.counted} of ${t.total} counted · started ${dateTime(t.startedAt)} by ${t.startedBy}` : t.postedAt ? `Posted ${dateTime(t.postedAt)} by ${t.postedBy}` : 'Cancelled'}</span>
                </span>
                {t.status === 'Posted' && <span className={cn('num text-sm', t.varianceValue < 0 ? 'text-bad' : 'text-good')}>{t.varianceValue > 0 ? '+' : ''}{ksh(t.varianceValue)}</span>}
                {t.accuracy !== null && <span className="text-xs text-muted">{t.accuracy}% exact</span>}
                <Status tone={t.status === 'Counting' ? 'warn' : t.status === 'Posted' ? 'good' : 'plain'}>{t.status === 'Counting' ? 'Counting' : t.status}</Status>
              </button>
            </li>
          ))}
        </ul>
      )}
      {starting && <StartSheet onClose={() => setStarting(false)} onStarted={(id) => (setStarting(false), reload(), open(id))} />}
      {openId && <TakeSheet id={openId} canCount={canCount} onClose={() => open(null)} onChange={reload} />}
    </div>
  );
}

function StartSheet({ onClose, onStarted }: { onClose: () => void; onStarted: (id: string) => void }) {
  const locations = useApi<{ id: string; name: string }[]>('/locations');
  const [f, setF] = useState({ locationId: '', blind: true, note: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const start = async () => {
    setState({ busy: true, error: '' });
    try {
      const t = await api<{ id: string }>('/stock-takes', { body: { locationId: f.locationId || null, blind: f.blind, note: f.note } });
      onStarted(t.id);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  return (
    <Sheet open onClose={onClose} title="Start a stock take" description="The system notes what it expects for every stocked item in this store now. Count when sales and deliveries are quiet.">
      <div className="space-y-4">
        <Field label="Store">
          <select className="field" value={f.locationId} onChange={(e) => setF({ ...f, locationId: e.target.value })}>
            <option value="">Main store</option>
            {(locations.data ?? []).map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
          </select>
        </Field>
        <label className="flex items-start gap-2.5 text-sm"><input type="checkbox" className="mt-0.5 h-4 w-4" checked={f.blind} onChange={(e) => setF({ ...f, blind: e.target.checked })} /><span><b>Blind count</b><span className="block text-muted">Counters don’t see what the system expects, so they count what’s really there. Recommended.</span></span></label>
        <Field label="Note (optional)"><input className="field" value={f.note} onChange={(e) => setF({ ...f, note: e.target.value })} placeholder="Quarter-end count" /></Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter><button className="btn btn-primary" disabled={state.busy} onClick={start}>Start counting</button></SheetFooter>
      </div>
    </Sheet>
  );
}

function TakeSheet({ id, canCount, onClose, onChange }: { id: string; canCount: boolean; onClose: () => void; onChange: () => void }) {
  const [review, setReview] = useState(false);
  const { data: t, error, reload } = useApi<Take>(`/stock-takes/${id}?review=${review}`);
  const [q, setQ] = useState('');
  const [entries, setEntries] = useState<Record<string, string>>({});
  const [state, setState] = useState({ busy: '', error: '' });
  const shown = useMemo(() => (t?.lines ?? []).filter((l) => !q.trim() || l.name.toLowerCase().includes(q.toLowerCase()) || l.sku.toLowerCase().includes(q.toLowerCase()) || l.barcode === q.trim()), [t, q]);
  if (error) return <Sheet open onClose={onClose} title="Stock take"><ErrorNote>{error}</ErrorNote></Sheet>;
  if (!t) return <Sheet open onClose={onClose} title="Stock take"><Loading /></Sheet>;
  const counting = t.status === 'Counting' && canCount;
  const dirty = Object.keys(entries).length;
  const run = async (what: string, fn: () => Promise<unknown>) => {
    setState({ busy: what, error: '' });
    try { await fn(); reload(); onChange(); setState({ busy: '', error: '' }); } catch (e) { setState({ busy: '', error: (e as Error).message }); }
  };
  const saveCounts = () => run('save', async () => {
    await api(`/stock-takes/${id}/counts`, { body: Object.entries(entries).map(([itemId, v]) => ({ itemId, counted: v.trim() === '' ? null : Number(v) })) });
    setEntries({});
  });
  const s = t.summary;
  return (
    <Sheet open onClose={onClose} title={`${t.number} · ${t.location}`} description={`${t.blind ? 'Blind count' : 'Count'} started ${dateTime(t.startedAt)} by ${t.startedBy}${t.note ? ` · ${t.note}` : ''}`} size="lg">
      <div className="space-y-4 text-sm">
        <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
          <Stat label="Counted" value={`${t.progress.counted} / ${t.progress.total}`}>Items not counted stay as they are</Stat>
          {s ? <>
            <Stat label="Exact" value={s.accuracy === null ? '—' : `${s.accuracy}%`}>{s.matched} items matched</Stat>
            <Stat label="Short / over" value={`${s.short_} / ${s.over}`}>Items that differ</Stat>
            <Stat label="Value of differences" value={ksh(s.value)} tone={s.value < 0 ? 'bad' : undefined}>At average cost</Stat>
          </> : <div className="col-span-1 flex items-center rounded-md bg-paper p-3 text-muted ring-1 ring-line sm:col-span-3">Expected figures are hidden while counting. Review the differences when the count is done.</div>}
        </div>
        {t.status === 'Posted' && <p className="rounded-md bg-good-soft p-3 text-good">Posted {t.postedAt && dateTime(t.postedAt)} by {t.postedBy}: {ksh(t.varianceValue)} booked as {t.varianceValue < 0 ? 'a stock loss' : 'stock found'}.</p>}
        <div className="flex flex-wrap items-center gap-2">
          <div className="min-w-0 flex-1"><SearchBox value={q} onChange={setQ} placeholder="Find an item by name, code or barcode" /></div>
          {t.status === 'Counting' && !t.showsExpected && <button className="btn btn-ghost" onClick={() => setReview(true)}><Eye className="h-4 w-4" /> Review differences</button>}
        </div>
        <div className="overflow-x-auto rounded-md ring-1 ring-line">
          <table className="w-full">
            <thead className="bg-paper text-left text-xs text-muted">
              <tr><th className="px-3 py-2">Item</th><th className="px-3 py-2 text-right">Counted</th>{t.showsExpected && <><th className="px-3 py-2 text-right">Expected</th><th className="px-3 py-2 text-right">Difference</th><th className="px-3 py-2 text-right">Value</th></>}</tr>
            </thead>
            <tbody className="divide-y divide-line">
              {shown.map((l) => (
                <tr key={l.itemId} className={cn(l.difference !== null && l.difference !== 0 && 'bg-warn-soft/40')}>
                  <td className="px-3 py-1.5">{l.name}<span className="block text-xs text-muted">{[l.sku, l.unit, l.countedBy && `counted by ${l.countedBy}`].filter(Boolean).join(' · ')}</span></td>
                  <td className="px-3 py-1.5 text-right">
                    {counting ? (
                      <input className="field num w-24 py-1.5 text-right" inputMode="decimal" value={entries[l.itemId] ?? (l.counted?.toString() ?? '')} onChange={(e) => setEntries({ ...entries, [l.itemId]: e.target.value })} aria-label={`Count for ${l.name}`} />
                    ) : <span className="num">{l.counted ?? '—'}</span>}
                  </td>
                  {t.showsExpected && <>
                    <td className="num px-3 py-1.5 text-right text-muted">{l.expected}</td>
                    <td className={cn('num px-3 py-1.5 text-right', (l.difference ?? 0) < 0 ? 'text-bad' : (l.difference ?? 0) > 0 ? 'text-good' : '')}>{l.difference === null ? '—' : `${l.difference > 0 ? '+' : ''}${l.difference}`}</td>
                    <td className="num px-3 py-1.5 text-right">{l.value === null ? '—' : ksh(l.value)}</td>
                  </>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        {counting && (
          <SheetFooter cancel="Close">
            <button className="btn btn-primary" disabled={!!state.busy || dirty === 0} onClick={saveCounts}>Save {dirty || ''} count{dirty === 1 ? '' : 's'}</button>
            <button className="btn btn-ghost" disabled={!!state.busy || dirty > 0 || t.progress.counted === 0} onClick={async () => (await confirmDialog({ title: `Post ${t.number}?`, body: `Stock at ${t.location} becomes what was counted for the ${t.progress.counted} counted items, and the difference${s ? ` (${ksh(s.value)})` : ''} goes to the books. This can’t be undone.`, confirm: 'Post the count' })) && run('post', () => api(`/stock-takes/${id}/post`, { body: {} }))}>Post differences</button>
            <button className="btn btn-ghost text-bad" disabled={!!state.busy} onClick={async () => (await confirmDialog({ title: 'Cancel this count?', body: 'Nothing is posted.', confirm: 'Cancel count', danger: true })) && run('cancel', () => api(`/stock-takes/${id}/cancel`, { body: {} }))}>Cancel count</button>
          </SheetFooter>
        )}
      </div>
    </Sheet>
  );
}
