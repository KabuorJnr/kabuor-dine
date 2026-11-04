// Tables and their QR codes. Print the cards (A4, six to a page), cut them out and stand one on each table: a guest
// scans it and the menu opens for that table. A new code retires a card (a photo of the old one stops working).
import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { Copy, Loader2, Pencil, Plus, Printer, RefreshCw, Trash2 } from 'lucide-react';
import { api, useApi, useSession } from '../lib/api';
import { ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, confirmDialog } from '../components/ui';
import { printAs } from '../components/PrintDoc';
import { tableUrl } from './lib';

type Table = { id: string; name: string; area: string; seats: number; token: string };

export default function Tables() {
  const s = useSession();
  const { data, error, reload } = useApi<Table[]>('/dine/tables');
  const [editing, setEditing] = useState<Table | 'new' | null>(null);
  const [problem, setProblem] = useState('');
  const [copied, setCopied] = useState('');
  if (error && !data) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const act = async (fn: () => Promise<unknown>) => { setProblem(''); try { await fn(); reload(); } catch (e) { setProblem((e as Error).message); } };

  return (
    <div className="space-y-5">
      <PageHeader title="Tables & QR codes" subtitle={`${data.length} tables. Print the cards and stand one on each table.`}
        actions={<>
          <button className="btn btn-ghost" onClick={() => printAs(`${s?.company.name ?? 'Kabuor Dine'} table QR codes`)} disabled={data.length === 0}><Printer className="h-4 w-4" /> Print QR cards</button>
          <button className="btn bg-dine text-white hover:bg-dine-hover" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> Add tables</button>
        </>} />
      {problem && <ErrorNote>{problem}</ErrorNote>}

      {/* The cards: on screen with their buttons; printed as cut-out cards (PrintDoc takes this .doc, buttons removed). */}
      <div className="doc dine-qr-sheet grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-4">
        {data.map((t) => (
          <div key={t.id} className="dine-qr-card card flex flex-col items-center p-5 text-center">
            <p className="text-[11px] font-semibold tracking-[0.2em] text-dine uppercase">{s?.company.name}</p>
            <p className="mt-1 text-2xl font-extrabold">{t.name}</p>
            {t.area && <p className="text-xs text-muted">{t.area}</p>}
            <Qr value={tableUrl(t.token)} />
            <p className="text-sm font-semibold">Scan to see the menu and order</p>
            <p className="text-xs text-muted">Pay from your phone with M-Pesa</p>
            <p className="mt-2 text-[10px] text-faint">Kabuor Dine</p>
            <div className="mt-3 flex gap-1 print:hidden" data-screen-only>
              <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`Copy ${t.name}'s link`} title="Copy the link"
                onClick={() => { void navigator.clipboard?.writeText(tableUrl(t.token)); setCopied(t.id); setTimeout(() => setCopied(''), 1500); }}>
                {copied === t.id ? <span className="text-xs font-semibold text-good">✓</span> : <Copy className="h-4 w-4" />}
              </button>
              <a className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" href={tableUrl(t.token)} target="_blank" rel="noreferrer" title="Open as a guest" aria-label={`Open ${t.name} as a guest`}>↗</a>
              <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`Edit ${t.name}`} title="Rename" onClick={() => setEditing(t)}><Pencil className="h-4 w-4" /></button>
              <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`New code for ${t.name}`} title="New code (the old card stops working)"
                onClick={async () => { if (await confirmDialog({ title: `New QR code for ${t.name}?`, body: 'The printed card on the table, and any photo of it, stops working. Print the new card.', confirm: 'Make a new code' })) await act(() => api(`/dine/tables/${t.id}/new-code`, { body: {} })); }}>
                <RefreshCw className="h-4 w-4" />
              </button>
              <button className="grid h-9 w-9 place-items-center rounded-md text-bad hover:bg-bad-soft" aria-label={`Remove ${t.name}`} title="Remove"
                onClick={async () => { if (await confirmDialog({ title: `Remove ${t.name}?`, body: 'Its QR card stops working. Past orders stay in the reports.', confirm: 'Remove', danger: true })) await act(() => api(`/dine/tables/${t.id}`, { method: 'DELETE' })); }}>
                <Trash2 className="h-4 w-4" />
              </button>
            </div>
          </div>
        ))}
      </div>

      {data.length === 0 && <div className="card px-6 py-12 text-center"><p className="font-semibold">No tables yet</p><p className="mt-1 text-sm text-muted">Add your tables to get a QR code for each.</p></div>}

      {editing && <TableSheet table={editing === 'new' ? null : editing} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </div>
  );
}

function Qr({ value }: { value: string }) {
  const [src, setSrc] = useState('');
  useEffect(() => {
    let live = true;
    QRCode.toDataURL(value, { margin: 1, width: 360, errorCorrectionLevel: 'M', color: { dark: '#17110d', light: '#ffffff' } }).then((u) => live && setSrc(u)).catch(() => {});
    return () => { live = false; };
  }, [value]);
  return <div className="my-3 grid h-40 w-40 place-items-center">{src ? <img src={src} alt="QR code for this table" className="h-40 w-40" /> : <Loader2 className="h-5 w-5 animate-spin text-faint" />}</div>;
}

function TableSheet({ table, onClose, onSaved }: { table: Table | null; onClose: () => void; onSaved: () => void }) {
  const [f, setF] = useState({ name: table?.name ?? '', area: table?.area ?? '', seats: table?.seats ?? 4, count: 1, prefix: 'Table' });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError('');
    try {
      if (table) await api(`/dine/tables/${table.id}`, { method: 'PUT', body: { name: f.name, area: f.area, seats: f.seats } });
      else await api('/dine/tables', { body: f.count > 1 ? { count: f.count, prefix: f.prefix, area: f.area, seats: f.seats } : { name: f.name || undefined, area: f.area, seats: f.seats, prefix: f.prefix } });
      onSaved();
    } catch (err) { setError((err as Error).message); setBusy(false); }
  };
  return (
    <Sheet open onClose={onClose} title={table ? `Edit ${table.name}` : 'Add tables'} size="sm">
      <form onSubmit={submit} className="space-y-4">
        {!table && (
          <Field label="How many tables?" hint="Several at once are numbered after the ones you have.">
            <input type="number" className="field w-28" min={1} max={100} value={f.count} onChange={(e) => setF({ ...f, count: Math.max(1, Math.min(100, Number(e.target.value) || 1)) })} />
          </Field>
        )}
        {(table || f.count === 1)
          ? <Field label="Name"><input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} maxLength={40} placeholder={table ? '' : 'e.g. Table 7, Bar stool 2, VIP 1'} required={Boolean(table)} /></Field>
          : <Field label="Name them"><input className="field" value={f.prefix} onChange={(e) => setF({ ...f, prefix: e.target.value })} maxLength={30} placeholder="Table" /></Field>}
        <div className="grid grid-cols-2 gap-4">
          <Field label="Area"><input className="field" value={f.area} onChange={(e) => setF({ ...f, area: e.target.value })} maxLength={40} placeholder="e.g. Terrace" /></Field>
          <Field label="Seats"><input type="number" className="field" min={1} max={100} value={f.seats} onChange={(e) => setF({ ...f, seats: Number(e.target.value) || 1 })} /></Field>
        </div>
        {error && <ErrorNote>{error}</ErrorNote>}
        <SheetFooter><button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy}>{busy && <Loader2 className="h-4 w-4 animate-spin" />} {table ? 'Save' : 'Add'}</button></SheetFooter>
      </form>
    </Sheet>
  );
}
