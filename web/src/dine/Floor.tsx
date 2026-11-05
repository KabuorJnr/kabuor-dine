// The floor: every table at a glance for the waiters and the manager. What needs someone right now comes first
// (a table to confirm, a guest calling, food ready on the pass, a table asking for the bill), then the room by area.
// Tapping a table opens it: its orders, taking an order by hand, the bill and payment, and clearing it for the next party.
import { useEffect, useMemo, useRef, useState } from 'react';
import { BellRing, Check, ChefHat, CircleDollarSign, Clock, Loader2, Plus, Printer, Receipt, ShieldCheck, Sparkles, UserCheck, Users } from 'lucide-react';
import { api, can, getSession } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { confirmDialog, ErrorNote, Loading, NumberInput, PageError, Sheet, SheetFooter, Status } from '../components/ui';
import { chime, mins, optionText, STATUS, usePoll, type Dish, type Visit } from './lib';

type FloorTable = {
  id: string; name: string; area: string; seats: number;
  session: null | {
    id: string; number: string; status: 'Open' | 'Paid'; approved: boolean; openedAt: string; minutes: number;
    callWaiter: string | null; askedForBill: string | null; awaitingApproval: number; awaitingPayment: number;
    inKitchen: number; ready: number; total: number; paid: number; due: number; rating: number;
  };
};
type FloorData = { payMode: 'Tab' | 'PayFirst'; open: boolean; tables: FloorTable[] };

/** What a table needs from the team right now, most urgent first. */
function needs(t: FloorTable) {
  const s = t.session;
  if (!s) return [];
  const out: { key: string; label: string; tone: 'bad' | 'warn' | 'good' | 'info' }[] = [];
  if (s.awaitingApproval) out.push({ key: 'approve', label: 'Confirm table', tone: 'warn' });
  if (s.callWaiter) out.push({ key: 'call', label: 'Calling you', tone: 'bad' });
  if (s.ready) out.push({ key: 'ready', label: `${s.ready} ready to serve`, tone: 'good' });
  if (s.askedForBill) out.push({ key: 'bill', label: 'Wants the bill', tone: 'warn' });
  return out;
}

export default function Floor() {
  const { data, error, reload } = usePoll<FloorData>('/dine/floor', 4000);
  const [open, setOpen] = useState<FloorTable | null>(null);
  const attention = useMemo(() => (data?.tables ?? []).reduce((n, t) => n + needs(t).length, 0), [data]);
  const last = useRef(0);
  useEffect(() => { if (attention > last.current) chime(); last.current = attention; }, [attention]);

  if (error && !data) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const seated = data.tables.filter((t) => t.session).length;
  const urgent = data.tables.filter((t) => needs(t).length > 0).sort((a, b) => (a.session!.callWaiter ? -1 : 0) - (b.session!.callWaiter ? -1 : 0));
  const areas = [...new Set(data.tables.map((t) => t.area))];
  const owing = data.tables.reduce((n, t) => n + (t.session?.due ?? 0), 0);

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <div>
          <h1 className="text-2xl font-bold tracking-tight sm:text-[28px]">Floor</h1>
          <p className="mt-0.5 text-sm text-muted">{data.payMode === 'PayFirst' ? 'Guests pay when they order' : 'Guests pay at the end'} · updates by itself</p>
        </div>
        {!data.open && <Status tone="warn">Not taking orders from tables (Settings)</Status>}
      </div>

      {/* The room at a glance */}
      <dl className="grid grid-cols-2 gap-2.5 sm:grid-cols-4">
        {([
          ['Seated', `${seated}/${data.tables.length}`, Users, 'text-ink'],
          ['To confirm', data.tables.filter((t) => t.session?.awaitingApproval).length, ShieldCheck, 'text-warn'],
          ['Ready to serve', data.tables.reduce((n, t) => n + (t.session?.ready ?? 0), 0), ChefHat, 'text-good'],
          ['To collect', ksh(owing), Receipt, 'text-ink'],
        ] as const).map(([label, value, Icon, tone]) => (
          <div key={label} className="flex items-center gap-3 rounded-xl bg-white px-3.5 py-3 ring-1 ring-line">
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-lg bg-dine-soft text-dine"><Icon className="h-[18px] w-[18px]" /></span>
            <span className="min-w-0">
              <dt className="truncate text-xs font-medium text-muted">{label}</dt>
              <dd className={cn('num truncate text-lg leading-tight font-bold', tone)}>{value}</dd>
            </span>
          </div>
        ))}
      </dl>

      {urgent.length > 0 && (
        <section className="card overflow-hidden ring-2 ring-dine/40">
          <h2 className="flex items-center gap-2 bg-dine-soft px-4 py-2.5 text-sm font-bold text-dine"><BellRing className="h-4 w-4" /> Needs you now</h2>
          <ul className="divide-y divide-line">
            {urgent.map((t) => (
              <li key={t.id}>
                <button onClick={() => setOpen(t)} className="flex w-full flex-wrap items-center gap-x-3 gap-y-1 px-4 py-3 text-left hover:bg-paper">
                  <span className="w-24 font-bold">{t.name}</span>
                  <span className="flex flex-1 flex-wrap gap-1.5">{needs(t).map((n) => <Status key={n.key} tone={n.tone}>{n.label}</Status>)}</span>
                  <span className="text-xs text-muted">{mins(t.session!.minutes)} seated</span>
                </button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {areas.map((area) => (
        <section key={area || 'main'}>
          {areas.length > 1 && <h2 className="mb-2.5 flex items-center gap-2 text-xs font-semibold tracking-wider text-faint uppercase">{area || 'Main room'}<span className="h-px flex-1 bg-line" /></h2>}
          <div className="grid grid-cols-[repeat(auto-fill,minmax(10.5rem,1fr))] gap-3">
            {data.tables.filter((t) => t.area === area).map((t) => <TableCard key={t.id} t={t} onOpen={() => setOpen(t)} />)}
          </div>
        </section>
      ))}

      {data.tables.length === 0 && (
        <div className="card px-6 py-12 text-center">
          <p className="font-semibold">No tables yet</p>
          <p className="mt-1 text-sm text-muted">Add your tables in Tables &amp; QR, print their QR codes and put one on each table.</p>
        </div>
      )}

      <TableSheet table={open} onClose={() => { setOpen(null); void reload(); }} onChanged={() => void reload()} />
    </div>
  );
}

function TableCard({ t, onOpen }: { t: FloorTable; onOpen: () => void }) {
  const s = t.session;
  const n = needs(t);
  const tone = !s ? 'free' : s.status === 'Paid' ? 'paid' : n.some((x) => x.tone === 'bad' || x.key === 'approve') ? 'urgent' : n.length ? 'attention' : 'seated';
  return (
    <button onClick={onOpen} className={cn('relative flex min-h-[8.5rem] flex-col rounded-2xl p-3.5 text-left ring-1 transition hover:-translate-y-0.5 hover:shadow-md',
      tone === 'free' && 'border border-dashed border-ink-2/20 bg-white/50 ring-transparent text-muted hover:bg-white',
      tone === 'seated' && 'bg-dine-soft/60 ring-dine/25',
      tone === 'attention' && 'bg-dine-soft ring-2 ring-dine/60',
      tone === 'urgent' && 'bg-warn-soft ring-2 ring-warn',
      tone === 'paid' && 'bg-good-soft ring-good/30')}>
      {tone === 'urgent' && <span className="absolute top-3 right-3 h-2.5 w-2.5 animate-ping rounded-full bg-warn" />}
      <span className="flex items-baseline justify-between gap-2">
        <span className="text-lg font-bold">{t.name}</span>
        <span className="flex items-center gap-1 text-xs text-muted"><Users className="h-3.5 w-3.5" />{t.seats}</span>
      </span>
      {!s ? (
        <span className="mt-auto flex items-center gap-1.5 text-sm text-faint"><span className="h-1.5 w-1.5 rounded-full bg-good" /> Free · tap to seat</span>
      ) : (
        <>
          <span className="mt-1 flex items-center gap-1 text-xs text-muted"><Clock className="h-3.5 w-3.5" /> {mins(s.minutes)}</span>
          <span className="mt-2 flex flex-wrap gap-1">
            {s.status === 'Paid' ? <Status tone="good">Paid · clear table</Status> : n.length ? n.map((x) => <Status key={x.key} tone={x.tone}>{x.label}</Status>)
              : s.inKitchen ? <Status tone="info">{s.inKitchen} in the kitchen</Status> : s.awaitingPayment ? <Status tone="warn">Awaiting payment</Status> : <Status tone="plain">Eating</Status>}
          </span>
          <span className="num mt-auto pt-2 text-sm font-semibold">{s.due > 0 ? <>{ksh(s.due)} <span className="font-normal text-muted">to pay</span></> : ksh(s.total)}</span>
        </>
      )}
    </button>
  );
}

type Detail = { table: { id: string; name: string; area: string }; visit: Visit; phone: string; feedback: string };

function TableSheet({ table, onClose, onChanged }: { table: FloorTable | null; onClose: () => void; onChanged: () => void }) {
  const sid = table?.session?.id ?? null;
  const { data, reload } = usePoll<Detail>(sid ? `/dine/sessions/${sid}` : null, 4000);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [ordering, setOrdering] = useState(false);
  const [paying, setPaying] = useState(false);
  const s = getSession();

  useEffect(() => { setError(''); setOrdering(false); setPaying(false); }, [table?.id]);
  if (!table) return null;
  const v = data?.visit;

  const act = async (what: string, fn: () => Promise<unknown>) => {
    setBusy(what); setError('');
    try { await fn(); await reload(); onChanged(); } catch (e) { setError((e as Error).message); }
    setBusy('');
  };

  return (
    <Sheet open onClose={onClose} title={table.name} size="md"
      description={v ? <>Visit {v.number} · {table.session ? mins(table.session.minutes) : ''} seated{data?.phone ? ` · ${data.phone}` : ''}</> : table.area || 'Free table'}>
      {error && <div className="mb-3"><ErrorNote>{error}</ErrorNote></div>}

      {!sid && !ordering && (
        <div className="py-6 text-center">
          <p className="text-muted">Nobody is seated here.</p>
          <div className="mt-4 flex justify-center gap-2">
            <button className="btn btn-ghost" disabled={busy !== ''} onClick={() => act('seat', () => api(`/dine/tables/${table.id}/seat`, { body: {} }))}><UserCheck className="h-4 w-4" /> Seat guests</button>
            <button className="btn bg-dine text-white hover:bg-dine-hover" onClick={() => setOrdering(true)}><Plus className="h-4 w-4" /> Take an order</button>
          </div>
          <p className="mt-3 text-xs text-muted">Guests can also scan the table’s QR and order themselves.</p>
        </div>
      )}

      {sid && !v && <Loading />}

      {v && !ordering && !paying && (
        <div className="space-y-4">
          {/* What the table needs */}
          {(v.orders.some((o) => o.status === 'AwaitingApproval') || v.calledWaiter || v.askedForBill) && (
            <div className="flex flex-wrap gap-2 rounded-lg bg-warn-soft p-3">
              {v.orders.some((o) => o.status === 'AwaitingApproval') && (
                <button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy !== ''} onClick={() => act('approve', () => api(`/dine/sessions/${sid}/approve`, { body: {} }))}>
                  <ShieldCheck className="h-4 w-4" /> Confirm table and send orders
                </button>
              )}
              {(v.calledWaiter || v.askedForBill) && (
                <button className="btn btn-ghost" disabled={busy !== ''} onClick={() => act('answer', () => api(`/dine/sessions/${sid}/answer`, { body: {} }))}>
                  <Check className="h-4 w-4" /> {v.askedForBill ? 'Bill request answered' : 'Answered the call'}
                </button>
              )}
            </div>
          )}

          {/* Orders */}
          <ul className="space-y-2">
            {v.orders.map((o) => (
              <li key={o.id} className="rounded-xl bg-white p-3.5 ring-1 ring-line">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span className="min-w-0">
                    <span className="block text-sm font-semibold">{o.number}{o.guestName ? ` · ${o.guestName}` : ''}</span>
                    <span className="text-xs text-muted">{new Date(o.placedAt).toLocaleTimeString('en-KE', { hour: '2-digit', minute: '2-digit' })} · {o.lines.reduce((n, l) => n + l.quantity, 0)} items</span>
                  </span>
                  <Status tone={STATUS[o.status].tone}>{STATUS[o.status].label}</Status>
                </div>
                <ul className="mt-2.5 space-y-1.5 border-t border-line pt-2.5 text-sm">
                  {o.lines.map((l, i) => (
                    <li key={i} className="flex justify-between gap-3">
                      <span className="min-w-0"><span className="font-medium"><span className="text-dine">{l.quantity}×</span> {l.name}</span>
                        {l.options && <span className="block text-xs text-muted">{l.options}</span>}
                        {l.note && <span className="mt-0.5 inline-block rounded bg-warn-soft px-1.5 text-xs font-semibold text-warn">{l.note}</span>}
                      </span>
                      <span className="num shrink-0 text-muted">{ksh(l.unitPrice * l.quantity)}</span>
                    </li>
                  ))}
                </ul>
                <p className="num mt-2 flex justify-between border-t border-line pt-2 text-sm font-semibold"><span>Order total</span><span>{ksh(o.total)}</span></p>
                {o.note && <p className="mt-1 text-sm text-warn">Note: {o.note}</p>}
                <div className="mt-2 flex flex-wrap gap-2">
                  {o.status === 'Ready' && <button className="btn btn-ghost px-3 py-1.5" disabled={busy !== ''} onClick={() => act(o.id, () => api(`/dine/orders/${o.id}/status`, { body: { status: 'Served' } }))}><Check className="h-4 w-4" /> Served</button>}
                  {!['Served', 'Cancelled'].includes(o.status) && (
                    <button className="px-2 py-1.5 text-sm font-semibold text-bad" disabled={busy !== ''} onClick={async () => {
                      if (await confirmDialog({ title: `Cancel order ${o.number}?`, body: 'The kitchen stops making it. A bill already printed for this order alone is voided.', confirm: 'Cancel order', danger: true }))
                        await act(o.id, () => api(`/dine/orders/${o.id}/status`, { body: { status: 'Cancelled' } }));
                    }}>Cancel</button>
                  )}
                </div>
              </li>
            ))}
            {v.orders.length === 0 && <li className="py-4 text-center text-sm text-muted">No orders yet.</li>}
          </ul>

          {/* The bill */}
          <div className="rounded-xl bg-dine-soft/60 p-4 ring-1 ring-dine/10">
            <p className="num flex justify-between text-sm text-ink-2"><span>Bill</span><span>{ksh(v.total)}</span></p>
            {v.paid > 0 && <p className="num mt-1 flex justify-between text-sm text-good"><span>Paid</span><span>{ksh(v.paid)}</span></p>}
            <p className="num mt-2 flex items-baseline justify-between border-t border-dine/15 pt-2"><span className="font-semibold">To pay</span><span className="text-xl font-bold">{ksh(v.due)}</span></p>
            {v.paying && <p className="mt-1 text-xs text-muted">The guest has an M-Pesa prompt open on their phone.</p>}
            {v.receipts.length > 0 && <p className="mt-1 text-xs text-muted">Invoices: {v.receipts.map((r) => r.number).join(', ')}</p>}
            {data?.feedback && <p className="mt-2 text-sm"><Sparkles className="mr-1 inline h-3.5 w-3.5 text-gold" />{'★'.repeat(v.rating)} “{data.feedback}”</p>}
          </div>

          <SheetFooter cancel={false}>
            {v.status === 'Open' && <button className="btn btn-ghost" onClick={() => setOrdering(true)}><Plus className="h-4 w-4" /> Add order</button>}
            {v.status === 'Open' && v.due > 0 && can(s, 'sales.view') && (
              <button className="btn btn-ghost" disabled={busy !== ''} onClick={() => act('print', async () => {
                await api(`/dine/sessions/${sid}/bill`, { body: {} });
                window.open(`/dine/bill/${sid}?print=1`, '_blank');
              })}><Printer className="h-4 w-4" /> Print bill</button>
            )}
            {v.status === 'Open' && v.due > 0 && <button className="btn btn-mpesa" onClick={() => setPaying(true)}><CircleDollarSign className="h-4 w-4" /> Take payment</button>}
            {(v.status === 'Paid' || v.orders.length === 0) && (
              <button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy !== ''} onClick={() => act('clear', async () => { await api(`/dine/sessions/${sid}/clear`, { body: {} }); onClose(); })}>
                <Check className="h-4 w-4" /> Clear table
              </button>
            )}
            {v.status === 'Open' && v.orders.length > 0 && v.due > 0 && (can(s, 'finance') || s?.user.role === 'Owner') && (
              <button className="px-2 text-sm font-semibold text-bad" disabled={busy !== ''} onClick={async () => {
                if (await confirmDialog({ title: `Clear ${table.name} unpaid?`, body: `${ksh(v.due)} hasn’t been paid. Any bill printed stays open in Invoices for follow-up.`, confirm: 'Clear unpaid', danger: true }))
                  await act('clear', async () => { await api(`/dine/sessions/${sid}/clear?force=true`, { body: {} }); onClose(); });
              }}>Clear unpaid</button>
            )}
          </SheetFooter>
        </div>
      )}

      {paying && v && sid && <TakePayment sessionId={sid} due={v.due} onDone={async () => { setPaying(false); await reload(); onChanged(); }} onBack={() => setPaying(false)} />}
      {ordering && <OrderPad tableId={table.id} onDone={async () => { setOrdering(false); await reload(); onChanged(); }} onBack={() => setOrdering(false)} />}
    </Sheet>
  );
}

function TakePayment({ sessionId, due, onDone, onBack }: { sessionId: string; due: number; onDone: () => void; onBack: () => void }) {
  const [method, setMethod] = useState<'Cash' | 'Mpesa' | 'Card'>('Mpesa');
  const [reference, setReference] = useState('');
  const [phone, setPhone] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError('');
    try { await api(`/dine/sessions/${sessionId}/pay`, { body: { method, reference, phone } }); onDone(); } catch (err) { setError((err as Error).message); setBusy(false); }
  };
  return (
    <form onSubmit={submit} className="space-y-4">
      <p className="num text-3xl font-bold">{ksh(due)}</p>
      <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="Paid by">
        {([['Mpesa', 'M-Pesa'], ['Cash', 'Cash'], ['Card', 'Card']] as const).map(([k, label]) => (
          <button key={k} type="button" role="radio" aria-checked={method === k} onClick={() => setMethod(k)}
            className={cn('rounded-lg py-3 text-sm font-semibold ring-1', method === k ? 'bg-dine-soft text-dine ring-2 ring-dine' : 'bg-white ring-line')}>{label}</button>
        ))}
      </div>
      {method === 'Mpesa' && (
        <label className="block"><span className="mb-1.5 block text-sm font-semibold">M-Pesa code from the guest’s message</span>
          <input className="field uppercase" value={reference} onChange={(e) => setReference(e.target.value)} placeholder="e.g. SFG3H5KLM2" required minLength={8} maxLength={20} autoFocus /></label>
      )}
      {method === 'Card' && (
        <label className="block"><span className="mb-1.5 block text-sm font-semibold">Card slip number <span className="font-normal text-muted">(optional)</span></span>
          <input className="field" value={reference} onChange={(e) => setReference(e.target.value)} maxLength={30} /></label>
      )}
      <label className="block"><span className="mb-1.5 block text-sm font-semibold">Guest’s phone <span className="font-normal text-muted">(optional, for their receipt and next visit)</span></span>
        <input className="field" type="tel" value={phone} onChange={(e) => setPhone(e.target.value)} placeholder="0712 345 678" /></label>
      {error && <ErrorNote>{error}</ErrorNote>}
      <SheetFooter cancel={false}>
        <button type="button" className="btn btn-ghost" onClick={onBack}>Back</button>
        <button className="btn btn-mpesa" disabled={busy}>{busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Receipt className="h-4 w-4" />} Record {ksh(due)} paid</button>
      </SheetFooter>
    </form>
  );
}

/** The waiter takes an order by hand: tap dishes, pick the guest's choices, send to the kitchen. */
function OrderPad({ tableId, onDone, onBack }: { tableId: string; onDone: () => void; onBack: () => void }) {
  const { data } = usePoll<{ stations: string[]; items: Dish[] }>('/dine/menu', 60_000);
  const [lines, setLines] = useState<{ dish: Dish; quantity: number; choices: Record<string, string>; note: string }[]>([]);
  const [q, setQ] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  if (!data) return <Loading />;
  const dishes = data.items.filter((d) => d.onMenu && d.price > 0 && (!q || d.name.toLowerCase().includes(q.toLowerCase())));
  const total = lines.reduce((n, l) => n + l.dish.price * l.quantity, 0);
  const send = async () => {
    setBusy(true); setError('');
    try {
      await api(`/dine/tables/${tableId}/orders`, { body: { lines: lines.map((l) => ({ itemId: l.dish.id, quantity: l.quantity, options: optionText(l.choices), note: l.note })) } });
      onDone();
    } catch (e) { setError((e as Error).message); setBusy(false); }
  };
  return (
    <div className="space-y-4">
      <input className="field" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Find a dish or drink" autoFocus />
      <div className="grid max-h-[38vh] grid-cols-2 gap-2 overflow-y-auto">
        {dishes.map((d) => (
          <button key={d.id} disabled={d.soldOut} onClick={() => setLines([...lines, { dish: d, quantity: 1, choices: Object.fromEntries(d.options.map((g) => [g.name, g.choices[0] ?? ''])), note: '' }])}
            className="rounded-lg p-2.5 text-left ring-1 ring-line hover:bg-dine-soft disabled:opacity-50">
            <span className="block text-sm font-semibold leading-snug">{d.name}</span>
            <span className="num text-xs text-muted">{d.soldOut ? 'Finished' : ksh(d.price)}</span>
          </button>
        ))}
      </div>
      {lines.length > 0 && (
        <ul className="divide-y divide-line rounded-lg ring-1 ring-line">
          {lines.map((l, i) => (
            <li key={i} className="space-y-2 p-3">
              <div className="flex items-center justify-between gap-2">
                <span className="text-sm font-semibold">{l.dish.name}</span>
                <span className="flex items-center gap-2">
                  <NumberInput min={1} max={50} className="field w-16 py-1.5" value={l.quantity} onChange={(n) => setLines((ls) => ls.map((x, j) => (j === i ? { ...x, quantity: n } : x)))} aria-label="How many" />
                  <button className="text-sm font-semibold text-bad" onClick={() => setLines(lines.filter((_, j) => j !== i))}>Remove</button>
                </span>
              </div>
              {l.dish.options.length > 0 && (
                <div className="flex flex-wrap gap-2">
                  {l.dish.options.map((g) => (
                    <select key={g.name} className="field w-auto py-1.5" aria-label={g.name} value={l.choices[g.name]} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, choices: { ...x.choices, [g.name]: e.target.value } } : x)))}>
                      {g.choices.map((c) => <option key={c} value={c}>{g.name}: {c}</option>)}
                    </select>
                  ))}
                </div>
              )}
              <input className="field py-1.5" value={l.note} onChange={(e) => setLines(lines.map((x, j) => (j === i ? { ...x, note: e.target.value } : x)))} placeholder="Note for the kitchen" maxLength={200} />
            </li>
          ))}
        </ul>
      )}
      {error && <ErrorNote>{error}</ErrorNote>}
      <SheetFooter cancel={false}>
        <button className="btn btn-ghost" onClick={onBack}>Back</button>
        <button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy || lines.length === 0} onClick={send}>
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <ChefHat className="h-4 w-4" />} Send to the kitchen · {ksh(total)}
        </button>
      </SheetFooter>
    </div>
  );
}
