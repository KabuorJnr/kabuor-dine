// Tracking links. A salesperson or the owner is deciding "where do I share, and is it working?", so each link
// shows its whole funnel (visits → people → inquiries → customers → sales → money) next to a copy button and a
// QR code for flyers, and a new link takes only a name and the channel it's for.
import { useEffect, useState } from 'react';
import QRCode from 'qrcode';
import { Check, Copy, Loader2, Plus, QrCode, Trash2 } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { Empty, ErrorNote, Field, Loading, PageHeader, Sheet, Status, SheetFooter, PageError, confirmDialog } from '../components/ui';

const CHANNELS = ['WhatsApp', 'TikTok', 'Instagram', 'Facebook', 'LinkedIn', 'X', 'YouTube', 'Google', 'Email', 'Sms', 'Website', 'Referral', 'Event', 'Other'];

function CopyButton({ text }: { text: string }) {
  const [done, setDone] = useState(false);
  return (
    <button className="btn btn-ghost px-2" aria-label="Copy link" onClick={async () => { try { await navigator.clipboard.writeText(text); setDone(true); setTimeout(() => setDone(false), 1500); } catch {} }}>
      {done ? <Check className="h-4 w-4 text-good" /> : <Copy className="h-4 w-4" />}
    </button>
  );
}

function Qr({ url, name }: { url: string; name: string }) {
  const [src, setSrc] = useState('');
  useEffect(() => { QRCode.toDataURL(url, { margin: 1, width: 240 }).then(setSrc).catch(() => {}); }, [url]);
  return src ? <a href={src} download={`${name}.png`} title="Download QR code"><img src={src} alt={`QR code for ${name}`} className="h-28 w-28" /></a> : null;
}

export default function Links() {
  const session = useSession();
  const { data, error, reload } = useApi<any>('/links');
  const users = useApi<any[]>(can(session, 'admin') ? '/settings/users' : null);
  const [making, setMaking] = useState(false);
  const [qr, setQr] = useState<any>(null);
  const [f, setF] = useState({ name: '', channel: 'WhatsApp', ownerUserId: '' });
  const [state, setState] = useState({ busy: false, error: '' });

  const make = async () => {
    setState({ busy: true, error: '' });
    try {
      await api('/links', { body: { name: f.name, channel: f.channel, ownerUserId: f.ownerUserId || null, target: 'shop' } });
      setMaking(false);
      setF({ name: '', channel: 'WhatsApp', ownerUserId: '' });
      setState({ busy: false, error: '' });
      reload();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const active = data.links.filter((l: any) => l.active);
  return (
    <div>
      <PageHeader title="Tracking links" subtitle="One link per salesperson and per channel. Everyone who comes through it, and everything they buy, is credited to it."
        actions={<button className="btn btn-primary" onClick={() => setMaking(true)}><Plus className="h-4 w-4" /> New link</button>} />
      {data.portal && <p className="mb-4 flex items-center gap-2 text-sm text-muted">Your online shop: <a className="font-semibold text-brand underline" href={data.portal} target="_blank" rel="noreferrer">{data.portal}</a> <CopyButton text={data.portal} /></p>}
      {active.length === 0 ? (
        <Empty title="No links yet" action={<button className="btn btn-primary" onClick={() => setMaking(true)}>Make the first link</button>}>
          Make one for each salesperson to share on WhatsApp, and one for each place you post (TikTok bio, LinkedIn, Facebook page). Then Kabuor Intelligence can tell you which brings customers.
        </Empty>
      ) : (
        <div className="card overflow-x-auto">
          <table className="w-full min-w-[46rem] text-sm">
            <thead><tr className="border-b border-line text-left text-xs font-semibold text-muted">
              {['Link', 'Visits', 'People', 'Inquiries', 'Customers', 'Sales', 'Revenue', ''].map((h, i) => <th key={i} className={cn('px-3 py-2.5', i > 0 && i < 7 && 'text-right')}>{h}</th>)}
            </tr></thead>
            <tbody className="divide-y divide-line">
              {active.map((l: any) => (
                <tr key={l.id}>
                  <td className="px-3 py-2.5">
                    <b className="block">{l.name}</b>
                    <span className="text-xs text-muted">{l.channel}{l.ownerName && ` · ${l.ownerName}`} · <span className="font-mono">{l.url.replace(/^https?:\/\//, '')}</span></span>
                  </td>
                  {[l.visits, l.people, l.inquiries, l.customers, l.sales].map((n: number, i: number) => <td key={i} className="num px-3 py-2.5 text-right">{n}</td>)}
                  <td className="num px-3 py-2.5 text-right font-semibold">{ksh(l.revenue)}</td>
                  <td className="px-3 py-2.5">
                    <span className="flex justify-end">
                      <CopyButton text={l.url} />
                      <button className="btn btn-ghost px-2" aria-label="QR code" onClick={() => setQr(l)}><QrCode className="h-4 w-4" /></button>
                      <button className="btn btn-ghost px-2 text-bad" aria-label="Switch off" onClick={async () => (await confirmDialog({ title: `Switch off “${l.name}”?`, body: 'Its history stays.', confirm: 'Switch off', danger: true })) && api(`/links/${l.id}`, { method: 'DELETE' }).then(reload)}><Trash2 className="h-4 w-4" /></button>
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      <p className="mt-3 text-xs text-muted">People are counted once per phone or browser. A customer who first came through a link keeps counting for it, and for its salesperson, on every later sale.</p>

      <Sheet open={making} onClose={() => setMaking(false)} title="New tracking link">
        <div className="space-y-4">
          <Field label="Name" hint="Where it will be shared, so you can tell links apart.">
            <input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} placeholder="e.g. Mercy’s WhatsApp status, TikTok bio" />
          </Field>
          <div>
            <p className="mb-1.5 text-sm font-semibold">Channel</p>
            <div className="flex flex-wrap gap-1.5">
              {CHANNELS.map((c) => <button key={c} onClick={() => setF({ ...f, channel: c })} className={cn('rounded-full px-3 py-1 text-xs font-semibold ring-1', f.channel === c ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line')}>{c === 'Sms' ? 'SMS' : c}</button>)}
            </div>
          </div>
          {users.data && (
            <Field label="Salesperson" hint="Their sales and the customers it brings count for them.">
              <select className="field" value={f.ownerUserId} onChange={(e) => setF({ ...f, ownerUserId: e.target.value })}>
                <option value="">Nobody (the business)</option>
                {users.data.filter((u: any) => u.active && u.role !== 'Employee').map((u: any) => <option key={u.id} value={u.id}>{u.name}</option>)}
              </select>
            </Field>
          )}
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <SheetFooter>
          <button className="btn btn-primary" disabled={state.busy || f.name.trim().length < 2} onClick={make}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Make link</button>
          </SheetFooter>
        </div>
      </Sheet>
      <Sheet open={Boolean(qr)} onClose={() => setQr(null)} title={qr?.name ?? ''}>
        {qr && (
          <div className="space-y-3 text-center">
            <div className="flex justify-center"><Qr url={qr.url} name={qr.name} /></div>
            <p className="text-sm text-muted">Print it on flyers, receipts or a shop sign. Tap the code to download it.</p>
            <p className="font-mono text-sm">{qr.url}</p>
            <Status tone="plain">{qr.channel}</Status>
          </div>
        )}
      </Sheet>
    </div>
  );
}
