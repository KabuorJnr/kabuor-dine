// How the restaurant's tables order and pay: tab or pay-first, whether a waiter confirms a new table, the kitchen
// screens, the welcome line on the menu, and whether tables are taking orders right now. M-Pesa prompts and staff
// logins live in the back office (the ERP settings), on the same account.
import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { Check, Loader2, Smartphone } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn } from '../lib/format';
import { ErrorNote, Field, Loading, PageError, PageHeader } from '../components/ui';

type Settings = { payMode: 'Tab' | 'PayFirst'; approveFirstOrder: boolean; stations: string[]; welcome: string; open: boolean; mpesaPrompt: boolean; paybill: string; product: string };

export default function DineSettingsPage() {
  const { data, error, reload } = useApi<Settings>('/dine/settings');
  const [f, setF] = useState<Settings | null>(null);
  const [stations, setStations] = useState('');
  const [busy, setBusy] = useState(false);
  const [saved, setSaved] = useState(false);
  const [problem, setProblem] = useState('');
  useEffect(() => { if (data) { setF(data); setStations(data.stations.join(', ')); } }, [data]);
  if (error && !data) return <PageError>{error}</PageError>;
  if (!f) return <Loading />;

  const save = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setProblem(''); setSaved(false);
    try {
      await api('/dine/settings', { method: 'PUT', body: { payMode: f.payMode, approveFirstOrder: f.approveFirstOrder, stations, welcome: f.welcome, open: f.open } });
      reload(); setSaved(true);
    } catch (err) { setProblem((err as Error).message); }
    setBusy(false);
  };

  return (
    <form onSubmit={save} className="max-w-3xl space-y-5">
      <PageHeader title="Settings" subtitle="How your tables order and pay." />

      <section className="card space-y-4 p-5">
        <label className="flex items-start gap-3">
          <input type="checkbox" className="mt-1" checked={f.open} onChange={(e) => setF({ ...f, open: e.target.checked })} />
          <span><span className="block font-semibold">Taking orders from tables</span><span className="text-sm text-muted">Turn off when the kitchen closes. The menu still shows, with a note to ask a waiter.</span></span>
        </label>
      </section>

      <section className="card p-5">
        <h2 className="font-bold">When do guests pay?</h2>
        <div className="mt-3 grid gap-3 sm:grid-cols-2" role="radiogroup">
          {([['Tab', 'At the end (sit-down)', 'Orders go straight to the kitchen and add to the table’s bill. Guests pay when they’re done, from their phone or to the waiter.'],
            ['PayFirst', 'When they order (quick service)', 'The kitchen only sees an order once it’s paid. Suits cafés, food courts and takeaway counters.']] as const).map(([k, title, desc]) => (
            <button key={k} type="button" role="radio" aria-checked={f.payMode === k} onClick={() => setF({ ...f, payMode: k })}
              className={cn('rounded-lg p-4 text-left ring-1 transition', f.payMode === k ? 'bg-dine-soft ring-2 ring-dine' : 'bg-white ring-line hover:ring-ink-2/30')}>
              <span className={cn('block font-semibold', f.payMode === k && 'text-dine')}>{title}</span>
              <span className="mt-1 block text-sm text-muted">{desc}</span>
            </button>
          ))}
        </div>
        <label className="mt-4 flex items-start gap-3">
          <input type="checkbox" className="mt-1" checked={f.approveFirstOrder} onChange={(e) => setF({ ...f, approveFirstOrder: e.target.checked })} />
          <span><span className="block font-semibold">A waiter confirms each new table</span><span className="text-sm text-muted">A table’s first order waits until a waiter taps “Confirm table” on the floor, so nobody can order from a photo of the QR at home. Recommended.</span></span>
        </label>
      </section>

      <section className="card space-y-4 p-5">
        <h2 className="font-bold">The menu and the kitchen</h2>
        <Field label="Kitchen screens" hint="Separate with commas, e.g. Kitchen, Bar, Grill. Each dish goes to one; each screen shows only its own.">
          <input className="field" value={stations} onChange={(e) => setStations(e.target.value)} maxLength={250} />
        </Field>
        <Field label="Welcome line on the menu" hint="Shown under your name at the top of the guest menu.">
          <input className="field" value={f.welcome} onChange={(e) => setF({ ...f, welcome: e.target.value })} maxLength={300} placeholder="e.g. Karibu! Happy hour 5–7pm: two juices for 400." />
        </Field>
      </section>

      <section className="card p-5">
        <h2 className="flex items-center gap-2 font-bold"><Smartphone className="h-4 w-4 text-mpesa" /> M-Pesa at the table</h2>
        {f.mpesaPrompt
          ? <p className="mt-1 text-sm text-good">On. Guests get an M-Pesa prompt on their phone for the bill, and it’s recorded against it when they enter their PIN.</p>
          : <p className="mt-1 text-sm text-muted">Off. Guests ask for the bill and the waiter takes cash, card or an M-Pesa code. To send prompts to guests’ phones, add your Daraja keys in <Link className="font-semibold text-dine underline" to="/settings?tab=payments">Back office → Settings → M-Pesa &amp; payments</Link>.</p>}
        {f.paybill && <p className="mt-1 text-sm text-muted">Paybill on the menu: {f.paybill}</p>}
      </section>

      {problem && <ErrorNote>{problem}</ErrorNote>}
      <div className="flex items-center gap-3">
        <button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy}>{busy && <Loader2 className="h-4 w-4 animate-spin" />} Save</button>
        {saved && <span className="flex items-center gap-1 text-sm text-good"><Check className="h-4 w-4" /> Saved</span>}
      </div>
      <p className="text-sm text-muted">Staff logins (waiters as <b>Sales</b>, kitchen as <b>Storekeeper</b>), your plan and billing are in <Link className="font-semibold text-dine underline" to="/settings?tab=team">Back office → Settings</Link>.</p>
    </form>
  );
}
