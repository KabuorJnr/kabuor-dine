// Tables and their QR codes. Print the cards (A4, six to a page), cut them out and stand one on each table: a guest
// scans it and the menu opens for that table. A new code retires a card (a photo of the old one stops working).
import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { Copy, Download, ExternalLink, Loader2, Pencil, Plus, Printer, RefreshCw, Trash2 } from 'lucide-react';
import { api, useApi, useSession } from '../lib/api';
import { ErrorNote, Field, Loading, PageError, PageHeader, Sheet, SheetFooter, confirmDialog } from '../components/ui';
import { printAs } from '../components/PrintDoc';
import { tableUrl } from './lib';
import { DineWord } from './Logo';

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
      <PageHeader title="Tables & QR codes" subtitle={`${data.length} tables. Each code opens this restaurant's menu at that table: guests order and pay, no app or sign-up.`}
        actions={<>
          <button className="btn btn-ghost" onClick={() => printAs(`${s?.company.name ?? 'Kabuor Dine'} table QR codes`)} disabled={data.length === 0}><Printer className="h-4 w-4" /> Print all cards</button>
          <button className="btn btn-ghost" disabled={data.length === 0} onClick={async () => { for (const t of data) { await downloadCard(t, s?.company.name ?? ''); await new Promise((r) => setTimeout(r, 250)); } }}><Download className="h-4 w-4" /> Download all</button>
          <button className="btn bg-dine text-white hover:bg-dine-hover" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> Add tables</button>
        </>} />
      {problem && <ErrorNote>{problem}</ErrorNote>}

      {/* The cards: on screen with their buttons; printed as cut-out cards (PrintDoc takes this .doc, buttons removed). */}
      <div className="doc dine-qr-sheet grid grid-cols-[repeat(auto-fill,minmax(14rem,1fr))] gap-4">
        {data.map((t) => (
          <div key={t.id} className="dine-qr-card card flex flex-col items-center overflow-hidden rounded-2xl bg-gradient-to-b from-dine-soft/70 to-white px-5 pt-5 pb-4 text-center">
            <p className="max-w-full truncate text-[11px] font-semibold tracking-[0.2em] text-dine uppercase">{s?.company.name}</p>
            <p className="font-brand mt-1 text-[28px] leading-tight font-extrabold tracking-[-0.03em] text-espresso">{t.name}</p>
            {t.area && <p className="text-xs text-muted">{t.area}</p>}
            <Qr value={tableUrl(t.token)} />
            <p className="text-[15px] font-semibold text-espresso">Scan to see the menu, order and pay</p>
            <p className="mt-0.5 text-xs text-muted">No app or sign-up. Pay by M-Pesa from your phone.</p>
            <p className="mt-3 flex items-center"><DineWord className="h-3" /></p>
            <div className="mt-3 flex gap-1 print:hidden" data-screen-only>
              <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`Download ${t.name}'s card`} title="Download the card (PNG)" onClick={() => void downloadCard(t, s?.company.name ?? '')}><Download className="h-4 w-4" /></button>
              <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`Copy ${t.name}'s link`} title="Copy the link"
                onClick={() => { void navigator.clipboard?.writeText(tableUrl(t.token)); setCopied(t.id); setTimeout(() => setCopied(''), 1500); }}>
                {copied === t.id ? <span className="text-xs font-semibold text-good">✓</span> : <Copy className="h-4 w-4" />}
              </button>
              <a className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" href={tableUrl(t.token)} target="_blank" rel="noreferrer" title="Open as a guest" aria-label={`Open ${t.name} as a guest`}><ExternalLink className="h-4 w-4" /></a>
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

// The table's code with the Dine mark in its centre. High error correction keeps it scannable with the mark on top.
async function qrWithMark(value: string, size: number): Promise<HTMLCanvasElement> {
  const canvas = document.createElement('canvas');
  await QRCode.toCanvas(canvas, value, { margin: 1, width: size, errorCorrectionLevel: 'H', color: { dark: '#2E211A', light: '#FFFFFF' } });
  const ctx = canvas.getContext('2d')!;
  const mark = await loadImage('/brand/kabuor-dine-mark.svg');
  const box = Math.round(size * 0.22);
  const x = (size - box) / 2;
  ctx.fillStyle = '#FFFFFF';
  ctx.beginPath();
  ctx.roundRect(x, x, box, box, box * 0.22);
  ctx.fill();
  const w = box * 0.78, h = w * (mark.naturalHeight / mark.naturalWidth || 100 / 120);
  ctx.drawImage(mark, (size - w) / 2, (size - h) / 2, w, h);
  return canvas;
}

const loadImage = (src: string) => new Promise<HTMLImageElement>((resolve, reject) => { const i = new Image(); i.onload = () => resolve(i); i.onerror = reject; i.src = src; });

function Qr({ value }: { value: string }) {
  const [src, setSrc] = useState('');
  useEffect(() => {
    let live = true;
    qrWithMark(value, 360).then((c) => live && setSrc(c.toDataURL('image/png'))).catch(() => {});
    return () => { live = false; };
  }, [value]);
  return <div className="my-3 grid h-44 w-44 place-items-center rounded-2xl bg-white p-2 shadow-sm ring-1 ring-dine/10">{src ? <img src={src} alt="QR code for this table" className="h-full w-full" /> : <Loader2 className="h-5 w-5 animate-spin text-faint" />}</div>;
}

/** A print-ready card for one table (PNG, 1200 × 1700): the restaurant, the table, its code and how it works. */
async function downloadCard(t: Table, restaurant: string) {
  await document.fonts?.ready;
  const W = 1200, H = 1700;
  const c = document.createElement('canvas');
  c.width = W; c.height = H;
  const g = c.getContext('2d')!;
  const bg = g.createLinearGradient(0, 0, 0, H);
  bg.addColorStop(0, '#F4ECE3'); bg.addColorStop(0.45, '#FFFFFF');
  g.fillStyle = bg; g.fillRect(0, 0, W, H);
  g.textAlign = 'center';
  g.fillStyle = '#9C6B4A'; g.font = '600 38px Onest, sans-serif';
  g.fillText(restaurant.toUpperCase().split('').join(String.fromCharCode(8202)), W / 2, 150, W - 160);
  g.fillStyle = '#2E211A'; g.font = '800 120px Bricolage, Onest, sans-serif';
  g.fillText(t.name, W / 2, 290, W - 140);
  if (t.area) { g.fillStyle = '#585C80'; g.font = '500 40px Onest, sans-serif'; g.fillText(t.area, W / 2, 350); }
  const qr = await qrWithMark(tableUrl(t.token), 760);
  g.fillStyle = '#FFFFFF';
  g.beginPath(); g.roundRect((W - 840) / 2, 400, 840, 840, 56); g.fill();
  g.strokeStyle = 'rgba(156,107,74,0.18)'; g.lineWidth = 3; g.stroke();
  g.drawImage(qr, (W - 760) / 2, 440);
  g.fillStyle = '#2E211A'; g.font = '700 56px Onest, sans-serif';
  g.fillText('Scan to see the menu, order and pay', W / 2, 1360, W - 120);
  g.fillStyle = '#585C80'; g.font = '500 40px Onest, sans-serif';
  g.fillText('No app or sign-up. Pay by M-Pesa from your phone.', W / 2, 1430, W - 120);
  const logo = await loadImage('/brand/kabuor-dine-logo.svg');
  const lh = 52, lw = lh * (logo.naturalWidth / logo.naturalHeight);
  g.drawImage(logo, (W - lw) / 2, 1540, lw, lh);
  const a = document.createElement('a');
  a.href = c.toDataURL('image/png');
  a.download = `${restaurant || 'Kabuor Dine'} - ${t.name} QR.png`.replace(/[\\/:*?"<>|]+/g, '-');
  a.click();
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
