// The guest's menu (/m/:token), opened from the QR on their table, on their own phone, with no app and no sign-in.
// A guest wants three things: to see what there is and what it costs, to order exactly how they like it without
// waving for a waiter, and to pay and go when they're ready. So the page is the menu first, a basket that remembers
// their choices, then "My table": where each order has got to, a button to call the waiter, and the bill.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useParams } from 'react-router';
import { BellRing, Check, ChefHat, ChevronRight, Flame, Loader2, Minus, Plus, Receipt, ShoppingBag, Smartphone, Star, Utensils, X } from 'lucide-react';
import { cn, ksh } from '../lib/format';
import { optionText, STATUS, type CartLine, type Dish, type Visit } from './lib';

type Menu = {
  restaurant: { name: string; phone: string; county: string; paybill: string; welcome: string };
  table: { name: string; area: string };
  open: boolean; payMode: 'Tab' | 'PayFirst'; mpesaPrompt: boolean;
  menu: Dish[]; visit: Visit | null;
};

const KEY = (t: string) => `kdine-${t}`;

function useGuest(table: string) {
  const [token, setTokenState] = useState<string | null>(() => { try { return localStorage.getItem(KEY(table)); } catch { return null; } });
  const setToken = (t: string | null) => { try { t ? localStorage.setItem(KEY(table), t) : localStorage.removeItem(KEY(table)); } catch { /* private mode */ } setTokenState(t); };
  const call = async <T,>(path: string, body?: unknown): Promise<T> => {
    const res = await fetch(`/api/dine/m/${table}${path}`, {
      method: body !== undefined ? 'POST' : 'GET',
      headers: { ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...(token ? { 'X-Dine-Token': token } : {}) },
      body: body !== undefined ? JSON.stringify(body) : undefined,
    });
    const data = await res.json().catch(() => ({}));
    if (!res.ok) throw Object.assign(new Error(data.error ?? 'Something went wrong. Please try again or ask a waiter.'), { status: res.status });
    return data as T;
  };
  return { token, setToken, call };
}

export default function GuestMenu() {
  const { token: table = '' } = useParams();
  const { token, setToken, call } = useGuest(table);
  const [menu, setMenu] = useState<Menu | null>(null);
  const [visit, setVisit] = useState<Visit | null>(null);
  const [error, setError] = useState('');
  const [view, setView] = useState<'menu' | 'table'>('menu');
  const [category, setCategory] = useState('');
  const [picking, setPicking] = useState<Dish | null>(null);
  const [suggest, setSuggest] = useState<Dish | null>(null);
  const [cart, setCart] = useState<CartLine[]>([]);
  const [basketOpen, setBasketOpen] = useState(false);

  const load = () => call<Menu>('').then((m) => { setMenu(m); setVisit(m.visit); if (m.visit) setView((v) => v); }).catch((e) => setError(e.message));
  useEffect(() => { void load(); }, [token]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (menu) document.title = `${menu.restaurant.name} · ${menu.table.name}`; }, [menu]);

  // While the guest has a visit open, keep it fresh: the kitchen's progress and the bill.
  useEffect(() => {
    if (!token || !visit || visit.status === 'Closed') return;
    const id = window.setInterval(() => {
      if (document.visibilityState !== 'visible') return;
      call<Visit>('/visit').then(setVisit).catch((e) => { if (e.status === 401) { setToken(null); setVisit(null); } });
    }, 5000);
    return () => window.clearInterval(id);
  }, [token, visit?.id, visit?.status]); // eslint-disable-line react-hooks/exhaustive-deps

  const dishes = menu?.menu ?? [];
  const categories = useMemo(() => [...new Set(dishes.map((d) => d.category || 'Menu'))], [dishes]);
  const popular = dishes.filter((d) => d.popular && !d.soldOut);
  const count = cart.reduce((n, l) => n + l.quantity, 0);
  const total = cart.reduce((n, l) => n + l.quantity * l.dish.price, 0);

  const add = (dish: Dish, quantity: number, choices: Record<string, string>, note: string) => {
    setCart((c) => {
      const key = `${dish.id}|${optionText(choices)}|${note}`;
      const same = c.find((l) => l.key === key);
      return same ? c.map((l) => (l === same ? { ...l, quantity: l.quantity + quantity } : l)) : [...c, { key, dish, quantity, choices, note }];
    });
    setPicking(null);
    const s = dish.suggest && dishes.find((d) => d.id === dish.suggest!.id && !d.soldOut);
    setSuggest(s && !cart.some((l) => l.dish.id === s.id) ? s : null);
  };

  if (error && !menu) return (
    <Page>
      <div className="mx-auto mt-24 max-w-sm px-6 text-center">
        <Utensils className="mx-auto h-10 w-10 text-dine" />
        <p className="mt-4 text-lg font-semibold">We couldn’t open the menu</p>
        <p className="mt-1 text-sm text-muted">{error}</p>
        <button className="btn mt-6 bg-dine text-white hover:bg-dine-hover" onClick={() => { setError(''); void load(); }}>Try again</button>
      </div>
    </Page>
  );
  if (!menu) return <Page><div className="grid h-[70vh] place-items-center"><Loader2 className="h-7 w-7 animate-spin text-dine" /></div></Page>;

  const live = visit && visit.status !== 'Closed' && visit.orders.length > 0;
  const waiting = visit?.orders.filter((o) => o.status !== 'Served').length ?? 0;

  return (
    <Page>
      {/* Header: where you are */}
      <header className="bg-dine-night px-5 pt-6 pb-5 text-white">
        <div className="mx-auto max-w-xl">
          <p className="text-xs font-semibold tracking-[0.18em] text-orange-200/80 uppercase">{menu.table.name}{menu.table.area ? ` · ${menu.table.area}` : ''}</p>
          <h1 className="mt-1 text-2xl font-bold tracking-tight">{menu.restaurant.name}</h1>
          <p className="mt-1 text-sm text-white/70">{menu.restaurant.welcome || 'Order from your phone. We’ll bring it to your table.'}</p>
          {!menu.open && <p className="mt-3 rounded-md bg-white/10 px-3 py-2 text-sm">We’re not taking orders from the table right now. Please ask a waiter.</p>}
        </div>
      </header>

      {/* Menu | My table */}
      {live && (
        <div className="sticky top-0 z-20 border-b border-line bg-white">
          <div className="mx-auto flex max-w-xl">
            {(['menu', 'table'] as const).map((v) => (
              <button key={v} onClick={() => setView(v)} className={cn('flex-1 py-3 text-sm font-semibold', view === v ? 'border-b-2 border-dine text-dine' : 'text-muted')}>
                {v === 'menu' ? 'Menu' : <>My table{waiting > 0 && <span className="ml-1.5 rounded-full bg-dine px-1.5 py-0.5 text-[11px] text-white">{waiting}</span>}{visit!.due > 0 && waiting === 0 && <span className="ml-1.5 text-xs text-muted">· {ksh(visit!.due)} to pay</span>}</>}
              </button>
            ))}
          </div>
        </div>
      )}

      {view === 'table' && visit ? (
        <MyTable menu={menu} visit={visit} setVisit={setVisit} call={call} onMore={() => setView('menu')} />
      ) : (
        <main className="mx-auto max-w-xl pb-32">
          {/* Sections */}
          <nav className={cn('no-scrollbar sticky z-10 flex gap-2 overflow-x-auto bg-paper/95 px-4 py-3 backdrop-blur', live ? 'top-[45px]' : 'top-0')}>
            {['', ...categories].map((c) => (
              <button key={c || 'all'} onClick={() => { setCategory(c); window.scrollTo({ top: 0, behavior: 'smooth' }); }}
                className={cn('shrink-0 rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1 transition', category === c ? 'bg-dine text-white ring-dine' : 'bg-white text-ink-2 ring-line')}>
                {c || 'All'}
              </button>
            ))}
          </nav>

          {!category && popular.length > 0 && (
            <section className="px-4 pt-2">
              <h2 className="flex items-center gap-1.5 text-sm font-bold"><Flame className="h-4 w-4 text-dine" /> Guests love</h2>
              <div className="no-scrollbar -mx-4 mt-2 flex gap-3 overflow-x-auto px-4 pb-1">
                {popular.map((d) => (
                  <button key={d.id} onClick={() => setPicking(d)} disabled={!menu.open} className="w-44 shrink-0 rounded-xl bg-white p-3 text-left shadow-sm ring-1 ring-line">
                    <span className="line-clamp-2 block text-sm font-semibold">{d.name}</span>
                    <span className="mt-2 flex items-center justify-between text-sm"><span className="num font-semibold text-dine">{ksh(d.price)}</span><Plus className="h-4 w-4 text-dine" /></span>
                  </button>
                ))}
              </div>
            </section>
          )}

          {(category ? [category] : categories).map((c) => (
            <section key={c} className="px-4 pt-5">
              <h2 className="text-base font-bold">{c}</h2>
              <ul className="mt-2 divide-y divide-line overflow-hidden rounded-xl bg-white ring-1 ring-line">
                {dishes.filter((d) => (d.category || 'Menu') === c).map((d) => (
                  <li key={d.id}>
                    <button onClick={() => setPicking(d)} disabled={d.soldOut || !menu.open} className="flex w-full items-start gap-3 px-4 py-3.5 text-left disabled:opacity-55">
                      <span className="min-w-0 flex-1">
                        <span className="block font-semibold">{d.name}</span>
                        {d.description && <span className="mt-0.5 line-clamp-2 block text-sm text-muted">{d.description}</span>}
                        <span className="num mt-1.5 block text-sm font-semibold text-ink">{ksh(d.price)}</span>
                      </span>
                      {d.soldOut
                        ? <span className="mt-1 shrink-0 rounded-full bg-paper px-2.5 py-1 text-xs font-semibold text-muted">Finished</span>
                        : <span className="mt-1 grid h-8 w-8 shrink-0 place-items-center rounded-full bg-dine-soft text-dine"><Plus className="h-4 w-4" /></span>}
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          ))}
          {dishes.length === 0 && <p className="px-6 py-16 text-center text-muted">The menu is being prepared. Please ask a waiter.</p>}
          <p className="px-6 pt-8 text-center text-xs text-faint">Prices include VAT. Powered by Kabuor Dine.</p>
        </main>
      )}

      {/* "Goes well with" */}
      {suggest && view === 'menu' && (
        <div className="fixed inset-x-0 bottom-20 z-30 px-4">
          <div className="mx-auto flex max-w-xl items-center gap-3 rounded-xl bg-white p-3 shadow-lg ring-1 ring-line">
            <span className="min-w-0 flex-1 text-sm"><span className="text-muted">Goes well with it: </span><span className="font-semibold">{suggest.name}</span> <span className="num text-muted">{ksh(suggest.price)}</span></span>
            <button className="btn shrink-0 bg-dine px-3 py-2 text-white" onClick={() => { const s = suggest; setSuggest(null); setPicking(s); }}>Add</button>
            <button aria-label="No thanks" className="grid h-8 w-8 place-items-center text-faint" onClick={() => setSuggest(null)}><X className="h-4 w-4" /></button>
          </div>
        </div>
      )}

      {/* Basket bar */}
      {count > 0 && view === 'menu' && (
        <div className="fixed inset-x-0 bottom-0 z-30 border-t border-line bg-white/95 p-3 backdrop-blur">
          <button onClick={() => setBasketOpen(true)} className="mx-auto flex w-full max-w-xl items-center justify-between rounded-xl bg-dine px-4 py-3.5 font-semibold text-white">
            <span className="flex items-center gap-2"><ShoppingBag className="h-5 w-5" /> Your order · {count}</span>
            <span className="num flex items-center gap-1">{ksh(total)} <ChevronRight className="h-4 w-4" /></span>
          </button>
        </div>
      )}

      {picking && <Picker dish={picking} onClose={() => setPicking(null)} onAdd={add} />}
      {basketOpen && (
        <Basket menu={menu} cart={cart} setCart={setCart} onClose={() => setBasketOpen(false)}
          onSend={async (guestName, note) => {
            const r = await call<{ token: string; visit: Visit }>('/orders', {
              guestName, note,
              lines: cart.map((l) => ({ itemId: l.dish.id, quantity: l.quantity, options: optionText(l.choices), note: l.note })),
            });
            setToken(r.token);
            setVisit(r.visit);
            setCart([]);
            setBasketOpen(false);
            setView('table');
            window.scrollTo({ top: 0 });
          }} />
      )}
    </Page>
  );
}

function Page({ children }: { children: React.ReactNode }) {
  return <div className="min-h-screen bg-paper text-ink">{children}</div>;
}

/** A bottom sheet on a phone. */
function Drawer({ title, onClose, children, footer }: { title: string; onClose: () => void; children: React.ReactNode; footer?: React.ReactNode }) {
  useEffect(() => {
    document.body.style.overflow = 'hidden';
    const on = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', on);
    return () => { document.body.style.overflow = ''; window.removeEventListener('keydown', on); };
  }, [onClose]);
  return (
    <div className="fixed inset-0 z-50 flex items-end justify-center bg-black/40 sm:items-center" onClick={onClose}>
      <div role="dialog" aria-label={title} className="flex max-h-[90vh] w-full max-w-xl flex-col rounded-t-2xl bg-white sm:rounded-2xl" onClick={(e) => e.stopPropagation()}>
        <div className="flex items-start justify-between gap-3 px-5 pt-5 pb-3">
          <h2 className="text-lg font-bold">{title}</h2>
          <button aria-label="Close" onClick={onClose} className="-mt-1 -mr-2 grid h-9 w-9 place-items-center rounded-full text-muted hover:bg-paper"><X className="h-5 w-5" /></button>
        </div>
        <div className="overflow-y-auto px-5 pb-4">{children}</div>
        {footer && <div className="border-t border-line p-4">{footer}</div>}
      </div>
    </div>
  );
}

/** Choosing how they'd like a dish: each choice group, how many, and a note for the kitchen. */
function Picker({ dish, onClose, onAdd }: { dish: Dish; onClose: () => void; onAdd: (d: Dish, q: number, c: Record<string, string>, note: string) => void }) {
  const [choices, setChoices] = useState<Record<string, string>>(() => Object.fromEntries(dish.options.map((g) => [g.name, g.choices[0] ?? ''])));
  const [quantity, setQuantity] = useState(1);
  const [note, setNote] = useState('');
  return (
    <Drawer title={dish.name} onClose={onClose} footer={
      <div className="flex items-center gap-3">
        <Stepper value={quantity} onChange={setQuantity} />
        <button className="btn flex-1 bg-dine py-3.5 text-base text-white hover:bg-dine-hover" onClick={() => onAdd(dish, quantity, choices, note.trim())}>
          Add · <span className="num">{ksh(dish.price * quantity)}</span>
        </button>
      </div>
    }>
      {dish.description && <p className="-mt-1 text-sm text-muted">{dish.description}</p>}
      {dish.options.map((g) => (
        <fieldset key={g.name} className="mt-5">
          <legend className="mb-2 text-sm font-semibold">{g.name}</legend>
          <div className="flex flex-wrap gap-2">
            {g.choices.map((c) => (
              <button key={c} type="button" onClick={() => setChoices({ ...choices, [g.name]: c })} aria-pressed={choices[g.name] === c}
                className={cn('rounded-full px-3.5 py-2 text-sm font-medium ring-1 transition', choices[g.name] === c ? 'bg-dine-soft font-semibold text-dine ring-2 ring-dine' : 'bg-white ring-line')}>
                {choices[g.name] === c && <Check className="mr-1 inline h-3.5 w-3.5" />}{c}
              </button>
            ))}
          </div>
        </fieldset>
      ))}
      <label className="mt-5 block">
        <span className="mb-1.5 block text-sm font-semibold">Anything the kitchen should know?</span>
        <input className="field" value={note} onChange={(e) => setNote(e.target.value)} maxLength={200} placeholder="e.g. no onions, allergic to nuts" />
      </label>
    </Drawer>
  );
}

function Stepper({ value, onChange, min = 1 }: { value: number; onChange: (n: number) => void; min?: number }) {
  return (
    <div className="flex items-center rounded-full ring-1 ring-line">
      <button type="button" aria-label="One less" className="grid h-11 w-11 place-items-center text-ink-2 disabled:opacity-40" disabled={value <= min} onClick={() => onChange(value - 1)}><Minus className="h-4 w-4" /></button>
      <span className="num w-6 text-center font-semibold">{value}</span>
      <button type="button" aria-label="One more" className="grid h-11 w-11 place-items-center text-ink-2" disabled={value >= 50} onClick={() => onChange(value + 1)}><Plus className="h-4 w-4" /></button>
    </div>
  );
}

function Basket({ menu, cart, setCart, onClose, onSend }: {
  menu: Menu; cart: CartLine[]; setCart: (c: CartLine[]) => void; onClose: () => void; onSend: (guestName: string, note: string) => Promise<void>;
}) {
  const [name, setName] = useState(() => { try { return localStorage.getItem('kdine-name') ?? ''; } catch { return ''; } });
  const [note, setNote] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const total = cart.reduce((n, l) => n + l.quantity * l.dish.price, 0);
  const payFirst = menu.payMode === 'PayFirst';
  const send = async () => {
    setBusy(true); setError('');
    try { localStorage.setItem('kdine-name', name.trim()); } catch { /* fine */ }
    try { await onSend(name.trim(), note.trim()); } catch (e) { setError((e as Error).message); setBusy(false); }
  };
  return (
    <Drawer title="Your order" onClose={onClose} footer={
      <>
        {error && <p className="mb-3 rounded-md bg-bad-soft px-3 py-2 text-sm text-bad">{error}</p>}
        <button className="btn w-full bg-dine py-3.5 text-base text-white hover:bg-dine-hover" disabled={busy || cart.length === 0} onClick={send}>
          {busy ? <Loader2 className="h-5 w-5 animate-spin" /> : <ChefHat className="h-5 w-5" />} {payFirst ? 'Order and pay' : 'Send to the kitchen'} · <span className="num">{ksh(total)}</span>
        </button>
        <p className="mt-2 text-center text-xs text-muted">{payFirst ? 'The kitchen starts as soon as it’s paid.' : 'You pay at the end, from your phone or to the waiter.'}</p>
      </>
    }>
      <ul className="divide-y divide-line">
        {cart.map((l) => (
          <li key={l.key} className="flex items-start gap-3 py-3">
            <div className="min-w-0 flex-1">
              <p className="font-semibold">{l.dish.name}</p>
              {(optionText(l.choices) || l.note) && <p className="text-sm text-muted">{[optionText(l.choices), l.note].filter(Boolean).join(' · ')}</p>}
              <p className="num mt-0.5 text-sm">{ksh(l.dish.price * l.quantity)}</p>
            </div>
            <Stepper value={l.quantity} min={0} onChange={(q) => setCart(q === 0 ? cart.filter((x) => x !== l) : cart.map((x) => (x === l ? { ...x, quantity: q } : x)))} />
          </li>
        ))}
      </ul>
      <div className="mt-3 grid gap-3">
        <label className="block"><span className="mb-1.5 block text-sm font-semibold">Your name <span className="font-normal text-muted">(so the waiter knows whose it is)</span></span>
          <input className="field" value={name} onChange={(e) => setName(e.target.value)} maxLength={60} placeholder="Optional" /></label>
        <label className="block"><span className="mb-1.5 block text-sm font-semibold">A note for the whole order</span>
          <input className="field" value={note} onChange={(e) => setNote(e.target.value)} maxLength={300} placeholder="e.g. bring the drinks first" /></label>
      </div>
    </Drawer>
  );
}

/** The guest's table: where each order has got to, the waiter at the tap of a button, and the bill. */
function MyTable({ menu, visit, setVisit, call, onMore }: {
  menu: Menu; visit: Visit; setVisit: (v: Visit) => void; call: <T>(path: string, body?: unknown) => Promise<T>; onMore: () => void;
}) {
  const [phone, setPhone] = useState(() => { try { return localStorage.getItem('kdine-phone') ?? ''; } catch { return ''; } });
  const [busy, setBusy] = useState('');
  const [note, setNote] = useState('');
  const [error, setError] = useState('');
  const [stars, setStars] = useState(visit.rating);
  const [comment, setComment] = useState('');
  const [rated, setRated] = useState(visit.rating > 0);
  const payRef = useRef<HTMLDivElement>(null);
  const awaitingPay = visit.orders.some((o) => o.status === 'AwaitingPayment');
  const paid = visit.status === 'Paid' || (visit.total > 0 && visit.due === 0);

  const act = async (what: string, fn: () => Promise<void>) => {
    setBusy(what); setError(''); setNote('');
    try { await fn(); } catch (e) { setError((e as Error).message); }
    setBusy('');
  };
  const refresh = async () => setVisit(await call<Visit>('/visit'));

  return (
    <main className="mx-auto max-w-xl space-y-4 px-4 py-5 pb-24">
      {/* Orders */}
      {visit.orders.map((o) => {
        const s = STATUS[o.status];
        const step = ['New', 'Preparing', 'Ready', 'Served'].indexOf(o.status);
        return (
          <section key={o.id} className="rounded-xl bg-white p-4 ring-1 ring-line">
            <div className="flex items-start justify-between gap-3">
              <div>
                <p className="text-xs font-semibold tracking-wider text-faint uppercase">Order {o.number}{o.guestName ? ` · ${o.guestName}` : ''}</p>
                <p className={cn('mt-1 font-semibold', s.tone === 'good' ? 'text-good' : s.tone === 'warn' ? 'text-warn' : 'text-ink')}>{s.guest}</p>
              </div>
              <span className="num text-sm font-semibold">{ksh(o.total)}</span>
            </div>
            {step >= 0 && (
              <div className="mt-3 grid grid-cols-4 gap-1.5" aria-hidden>
                {[0, 1, 2, 3].map((i) => <span key={i} className={cn('h-1.5 rounded-full', i <= step ? 'bg-dine' : 'bg-line')} />)}
              </div>
            )}
            <ul className="mt-3 space-y-1 text-sm">
              {o.lines.map((l, i) => (
                <li key={i} className="flex justify-between gap-3"><span>{l.quantity} × {l.name}{l.options && <span className="text-muted"> · {l.options}</span>}{l.note && <span className="text-muted"> · {l.note}</span>}</span></li>
              ))}
            </ul>
          </section>
        );
      })}

      {/* The waiter */}
      {!paid && (
        <div className="grid grid-cols-2 gap-3">
          <button className="btn btn-ghost py-3" disabled={busy !== '' || visit.calledWaiter} onClick={() => act('waiter', async () => { await call('/call', { kind: 'waiter' }); await refresh(); setNote('The waiter is on the way.'); })}>
            <BellRing className="h-4 w-4" /> {visit.calledWaiter ? 'Waiter called' : 'Call the waiter'}
          </button>
          <button className="btn btn-ghost py-3" onClick={onMore}><Plus className="h-4 w-4" /> Order more</button>
        </div>
      )}

      {/* The bill */}
      <section ref={payRef} className="rounded-xl bg-white p-4 ring-1 ring-line">
        <div className="flex items-center justify-between">
          <h2 className="flex items-center gap-2 font-bold"><Receipt className="h-4 w-4 text-dine" /> The bill</h2>
          <span className="num text-lg font-bold">{ksh(visit.total)}</span>
        </div>
        {visit.paid > 0 && <p className="num mt-1 flex justify-between text-sm text-good"><span>Paid</span><span>{ksh(visit.paid)}</span></p>}
        {paid ? (
          <div className="mt-4 rounded-lg bg-good-soft p-4 text-center">
            <Check className="mx-auto h-7 w-7 text-good" />
            <p className="mt-1 font-semibold text-good">Paid. Thank you for dining with us!</p>
            {visit.receipts.map((r) => (
              <p key={r.id} className="mt-1 text-xs text-muted">Receipt {r.number}{r.etimsInvoiceNo ? ` · KRA ${r.etimsInvoiceNo}` : ''}{r.etimsQrUrl && <> · <a className="underline" href={r.etimsQrUrl} target="_blank" rel="noreferrer">check with KRA</a></>}</p>
            ))}
          </div>
        ) : visit.due > 0 && (
          <>
            <p className="num mt-1 flex justify-between text-sm font-semibold"><span>To pay</span><span>{ksh(visit.due)}</span></p>
            {visit.orders.some((o) => o.status === 'AwaitingApproval') ? (
              <p className="mt-3 text-sm text-muted">A waiter will confirm your table first.</p>
            ) : (
              <div className="mt-4">
                {menu.mpesaPrompt ? (
                  <>
                    <label className="block"><span className="mb-1.5 block text-sm font-semibold">Your M-Pesa number</span>
                      <input className="field" type="tel" inputMode="tel" value={phone} onChange={(e) => setPhone(e.target.value)} placeholder="0712 345 678" /></label>
                    <button className="btn btn-mpesa mt-3 w-full py-3.5 text-base" disabled={busy !== '' || visit.paying}
                      onClick={() => act('pay', async () => {
                        try { localStorage.setItem('kdine-phone', phone); } catch { /* fine */ }
                        await call('/pay', { phone });
                        await refresh();
                        setNote('Check your phone and enter your M-Pesa PIN. This page updates when it’s paid.');
                      })}>
                      {busy === 'pay' ? <Loader2 className="h-5 w-5 animate-spin" /> : <Smartphone className="h-5 w-5" />} {visit.paying ? 'Waiting for your PIN…' : `Pay ${ksh(visit.due)} with M-Pesa`}
                    </button>
                  </>
                ) : (
                  <button className="btn w-full bg-dine py-3.5 text-base text-white" disabled={busy !== '' || visit.askedForBill}
                    onClick={() => act('bill', async () => { await call('/call', { kind: 'bill' }); await refresh(); setNote('The waiter is bringing your bill.'); })}>
                    <Receipt className="h-5 w-5" /> {visit.askedForBill ? 'Bill on its way' : 'Ask for the bill'}
                  </button>
                )}
                {!menu.mpesaPrompt && menu.restaurant.paybill && <p className="mt-2 text-center text-xs text-muted">Or pay to M-Pesa paybill {menu.restaurant.paybill} and show the waiter the message.</p>}
                {menu.mpesaPrompt && <button className="mt-3 w-full text-center text-sm font-semibold text-dine" disabled={visit.askedForBill} onClick={() => act('bill', async () => { await call('/call', { kind: 'bill' }); await refresh(); setNote('The waiter is coming to take payment.'); })}>{visit.askedForBill ? 'The waiter is coming' : 'Pay cash or card to the waiter instead'}</button>}
                {awaitingPay && <p className="mt-2 text-center text-xs text-muted">Your order goes to the kitchen as soon as it’s paid.</p>}
              </div>
            )}
          </>
        )}
        {note && <p className="mt-3 rounded-md bg-dine-soft px-3 py-2 text-sm text-dine">{note}</p>}
        {error && <p className="mt-3 rounded-md bg-bad-soft px-3 py-2 text-sm text-bad">{error}</p>}
      </section>

      {/* How was it? */}
      {paid && (
        <section className="rounded-xl bg-white p-4 text-center ring-1 ring-line">
          {rated ? <p className="font-semibold">Thank you for your feedback!</p> : (
            <>
              <p className="font-semibold">How was everything?</p>
              <div className="mt-2 flex justify-center gap-1">
                {[1, 2, 3, 4, 5].map((n) => (
                  <button key={n} aria-label={`${n} stars`} onClick={() => setStars(n)} className="p-1"><Star className={cn('h-8 w-8', n <= stars ? 'fill-gold text-gold' : 'text-line')} /></button>
                ))}
              </div>
              {stars > 0 && (
                <>
                  <textarea className="field mt-3" rows={2} value={comment} onChange={(e) => setComment(e.target.value)} maxLength={500} placeholder={stars >= 4 ? 'What did you enjoy?' : 'What could we do better?'} />
                  <button className="btn mt-3 w-full bg-dine text-white" disabled={busy !== ''} onClick={() => act('rate', async () => { await call('/rate', { stars, comment }); setRated(true); })}>Send</button>
                </>
              )}
            </>
          )}
        </section>
      )}
    </main>
  );
}
