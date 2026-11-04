// The guest's menu (/m/:token), opened from the QR on their table, on their own phone, with no app and no sign-in.
// A guest wants three things: to see what there is and what it costs, to order exactly how they like it without
// waving for a waiter, and to pay and go when they're ready. So the page is the menu first, a basket that remembers
// their choices, then "My table": where each order has got to, a button to call the waiter, and the bill.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useParams } from 'react-router';
import { BellRing, Check, ChefHat, ChevronRight, Flame, Loader2, Minus, Plus, Receipt, Search, Smartphone, Star, Utensils, X } from 'lucide-react';
import { cn, ksh } from '../lib/format';
import { optionText, STATUS, type CartLine, type Dish, type Visit } from './lib';
import { DineMark } from './Logo';

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
  const [search, setSearch] = useState('');
  const [hint, setHint] = useState(() => { try { return !localStorage.getItem('kdine-hint-seen'); } catch { return true; } });
  const [added, setAdded] = useState('');
  const [justSent, setJustSent] = useState(false);
  const closeHint = () => { setHint(false); try { localStorage.setItem('kdine-hint-seen', '1'); } catch { /* fine */ } };
  useEffect(() => { if (!added) return; const t = window.setTimeout(() => setAdded(''), 1800); return () => window.clearTimeout(t); }, [added]);
  useEffect(() => { if (!justSent) return; const t = window.setTimeout(() => setJustSent(false), 6000); return () => window.clearTimeout(t); }, [justSent]);
  const [current, setCurrent] = useState('');
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
  // The section on screen lights up its chip as the guest scrolls.
  useEffect(() => {
    if (!menu || view !== 'menu') return;
    const els = [...document.querySelectorAll<HTMLElement>('[data-section]')];
    if (!els.length) return;
    const io = new IntersectionObserver((entries) => {
      const top = entries.filter((e) => e.isIntersecting).sort((x, y) => x.boundingClientRect.top - y.boundingClientRect.top)[0];
      if (top) setCurrent(top.target.getAttribute('data-section') ?? '');
    }, { rootMargin: '-120px 0px -60% 0px' });
    els.forEach((el) => io.observe(el));
    return () => io.disconnect();
  }, [menu, view, search, categories.length]);
  const count = cart.reduce((n, l) => n + l.quantity, 0);
  const total = cart.reduce((n, l) => n + l.quantity * l.dish.price, 0);

  const add = (dish: Dish, quantity: number, choices: Record<string, string>, note: string) => {
    setCart((c) => {
      const key = `${dish.id}|${optionText(choices)}|${note}`;
      const same = c.find((l) => l.key === key);
      return same ? c.map((l) => (l === same ? { ...l, quantity: l.quantity + quantity } : l)) : [...c, { key, dish, quantity, choices, note }];
    });
    setPicking(null);
    setAdded(`${quantity > 1 ? `${quantity} × ` : ''}${dish.name}`);
    if (hint) closeHint();
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
  const q = search.trim().toLowerCase();
  const shown = q ? dishes.filter((d) => d.name.toLowerCase().includes(q) || d.description.toLowerCase().includes(q) || (d.category || '').toLowerCase().includes(q)) : dishes;
  const sections = categories.filter((c) => shown.some((d) => (d.category || 'Menu') === c));
  const jump = (c: string) => {
    const el = document.getElementById(sectionId(c));
    if (el) window.scrollTo({ top: el.getBoundingClientRect().top + window.scrollY - (live ? 160 : 112), behavior: 'smooth' });
  };

  return (
    <Page>
      {/* Header: where you are */}
      <header className="border-b border-dine/10 bg-gradient-to-b from-dine-soft to-cream px-5 pt-[calc(env(safe-area-inset-top)+1.25rem)] pb-5 text-espresso">
        <div className="mx-auto max-w-xl">
          <p className="flex items-center gap-2 text-xs font-semibold tracking-[0.16em] text-dine uppercase">
            <span className={cn('h-2 w-2 rounded-full', menu.open ? 'bg-good' : 'bg-faint')} aria-hidden />
            {menu.table.name}{menu.table.area ? ` · ${menu.table.area}` : ''}
          </p>
          <h1 className="font-brand mt-1.5 text-[28px] leading-tight font-extrabold tracking-[-0.03em]">{menu.restaurant.name}</h1>
          <p className="mt-1 text-sm text-ink-2/80">{menu.restaurant.welcome || 'Order from your phone. We’ll bring it to your table.'}</p>
          {!menu.open && <p className="mt-3 rounded-lg bg-white px-3 py-2 text-sm ring-1 ring-dine/15">We’re not taking orders from the table right now. Please ask a waiter.</p>}
        </div>
      </header>

      {/* Menu | My table */}
      {live && (
        <div className="sticky top-0 z-20 border-b border-line bg-white/95 backdrop-blur">
          <div className="mx-auto flex max-w-xl" role="tablist">
            {(['menu', 'table'] as const).map((v) => (
              <button key={v} role="tab" aria-selected={view === v} onClick={() => { setView(v); window.scrollTo({ top: 0 }); }} className={cn('flex flex-1 items-center justify-center gap-1.5 border-b-2 py-3 text-sm font-semibold transition', view === v ? 'border-dine text-dine' : 'border-transparent text-muted')}>
                {v === 'menu' ? 'Menu' : <>My table{waiting > 0 ? <span className="grid h-5 min-w-5 place-items-center rounded-full bg-dine px-1.5 text-[11px] text-white">{waiting}</span> : visit!.due > 0 && <span className="text-xs font-medium text-muted">· {ksh(visit!.due)} to pay</span>}</>}
              </button>
            ))}
          </div>
        </div>
      )}

      {view === 'table' && visit ? (
        <MyTable menu={menu} visit={visit} setVisit={setVisit} call={call} onMore={() => setView('menu')} justSent={justSent} />
      ) : (
        <main className="mx-auto max-w-xl pb-36">
          {/* Search and sections, kept in reach while scrolling */}
          <div className={cn('sticky z-10 space-y-2.5 bg-cream/95 px-4 pt-3 pb-2.5 backdrop-blur', live ? 'top-[45px]' : 'top-0')}>
            <label className="relative block">
              <Search className="pointer-events-none absolute top-1/2 left-3.5 h-4 w-4 -translate-y-1/2 text-faint" />
              <input value={search} onChange={(e) => setSearch(e.target.value)} type="search" placeholder="Search the menu" aria-label="Search the menu"
                className="w-full rounded-full bg-white py-2.5 pr-4 pl-10 text-base ring-1 ring-line outline-none placeholder:text-faint focus:ring-2 focus:ring-dine sm:text-sm" />
            </label>
            {!q && sections.length > 1 && (
              <nav className="no-scrollbar -mx-4 flex gap-2 overflow-x-auto px-4" aria-label="Menu sections">
                {sections.map((c) => (
                  <button key={c} onClick={() => jump(c)} aria-current={current === c ? 'true' : undefined}
                    className={cn('shrink-0 rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1 transition', current === c ? 'bg-espresso text-white ring-espresso' : 'bg-white text-ink-2 ring-dine/15')}>
                    {c}
                  </button>
                ))}
              </nav>
            )}
          </div>

          {hint && !live && !q && (
            <section className="px-4 pt-2">
              <div className="relative rounded-2xl bg-white p-4 ring-1 ring-dine/15">
                <button onClick={closeHint} aria-label="Got it" className="absolute top-2.5 right-2.5 grid h-8 w-8 place-items-center rounded-full text-faint hover:bg-cream"><X className="h-4 w-4" /></button>
                <p className="pr-8 font-semibold">Order right here, no app or sign-up</p>
                <ol className="mt-3 grid grid-cols-3 gap-2 text-center text-xs text-muted">
                  {([[Utensils, 'Choose', 'dishes and how you like them'], [ChefHat, 'Send', 'straight to the kitchen'], [Smartphone, 'Pay', 'by M-Pesa when you’re ready']] as const).map(([Icon, t, d], i) => (
                    <li key={t} className="rounded-xl bg-cream px-2 py-3">
                      <span className="mx-auto grid h-9 w-9 place-items-center rounded-full bg-dine-soft text-dine"><Icon className="h-[18px] w-[18px]" /></span>
                      <span className="mt-1.5 block text-[13px] font-semibold text-ink">{i + 1}. {t}</span>
                      <span className="mt-0.5 block leading-snug">{d}</span>
                    </li>
                  ))}
                </ol>
              </div>
            </section>
          )}

          {!q && popular.length > 0 && (
            <section className="px-4 pt-3">
              <h2 className="flex items-center gap-1.5 text-sm font-bold"><Flame className="h-4 w-4 text-dine" /> Guests love</h2>
              <div className="no-scrollbar -mx-4 mt-2.5 flex snap-x gap-3 overflow-x-auto px-4 pb-1">
                {popular.map((d) => (
                  <button key={d.id} onClick={() => setPicking(d)} disabled={!menu.open} className="w-40 shrink-0 snap-start overflow-hidden rounded-2xl bg-white text-left shadow-sm ring-1 ring-line transition active:scale-[0.98]">
                    <Monogram name={d.name} category={d.category} className="h-20 w-full rounded-none text-2xl" />
                    <span className="block p-3">
                      <span className="line-clamp-2 block min-h-[2.5rem] text-sm leading-snug font-semibold">{d.name}</span>
                      <span className="mt-1.5 flex items-center justify-between"><span className="num text-sm font-bold">{ksh(d.price)}</span><span className="grid h-7 w-7 place-items-center rounded-full bg-dine text-white"><Plus className="h-4 w-4" /></span></span>
                    </span>
                  </button>
                ))}
              </div>
            </section>
          )}

          {sections.map((c) => (
            <section key={c} id={sectionId(c)} data-section={c} className="px-4 pt-6">
              <h2 className="flex items-baseline justify-between text-lg font-bold tracking-tight">{c}<span className="text-xs font-medium text-faint">{shown.filter((d) => (d.category || 'Menu') === c).length}</span></h2>
              <ul className="mt-2.5 space-y-2.5">
                {shown.filter((d) => (d.category || 'Menu') === c).map((d) => (
                  <li key={d.id}>
                    <button onClick={() => setPicking(d)} disabled={d.soldOut || !menu.open}
                      className="flex w-full items-center gap-3.5 rounded-2xl bg-white p-3 text-left ring-1 ring-line transition active:scale-[0.99] disabled:opacity-55">
                      <Monogram name={d.name} category={d.category} className="h-16 w-16 shrink-0" />
                      <span className="min-w-0 flex-1">
                        <span className="flex items-center gap-1.5 font-semibold leading-snug">{d.name}{d.popular && <Flame className="h-3.5 w-3.5 shrink-0 text-dine" aria-label="Popular" />}</span>
                        {d.description && <span className="mt-0.5 line-clamp-2 block text-[13px] leading-snug text-muted">{d.description}</span>}
                        <span className="mt-1.5 flex items-center gap-2">
                          <span className="num text-sm font-bold">{ksh(d.price)}</span>
                          {d.options.length > 0 && <span className="text-xs text-faint">· {d.options.length === 1 ? `choose ${d.options[0].name.toLowerCase()}` : `${d.options.length} choices`}</span>}
                        </span>
                      </span>
                      {d.soldOut
                        ? <span className="shrink-0 rounded-full bg-paper px-2.5 py-1 text-xs font-semibold text-muted">Finished</span>
                        : <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-dine-soft text-dine"><Plus className="h-[18px] w-[18px]" /></span>}
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          ))}
          {q && shown.length === 0 && <p className="px-6 py-16 text-center text-muted">Nothing on the menu matches “{search.trim()}”.</p>}
          {dishes.length === 0 && <p className="px-6 py-16 text-center text-muted">The menu is being prepared. Please ask a waiter.</p>}
          <p className="flex items-center justify-center gap-1.5 px-6 pt-10 text-center text-xs text-faint">Prices include VAT · <DineMark className="h-3.5" /> Powered by Kabuor Dine</p>
        </main>
      )}

      {added && !suggest && view === 'menu' && (
        <div className="pointer-events-none fixed inset-x-0 bottom-[calc(env(safe-area-inset-bottom)+5.75rem)] z-30 flex justify-center px-4" role="status">
          <span className="flex items-center gap-2 rounded-full bg-espresso px-4 py-2 text-sm font-semibold text-white shadow-lg"><Check className="h-4 w-4 text-[#D6B394]" /> Added {added}</span>
        </div>
      )}

      {/* "Goes well with" */}
      {suggest && view === 'menu' && (
        <div className="fixed inset-x-0 bottom-[calc(env(safe-area-inset-bottom)+5.5rem)] z-30 px-4">
          <div className="mx-auto flex max-w-xl items-center gap-3 rounded-2xl bg-white p-3 shadow-xl ring-1 ring-line">
            <Monogram name={suggest.name} category={suggest.category} className="h-11 w-11 shrink-0 text-base" />
            <span className="min-w-0 flex-1 text-sm leading-snug"><span className="block text-xs text-muted">Goes well with it</span><span className="font-semibold">{suggest.name}</span> <span className="num text-muted">{ksh(suggest.price)}</span></span>
            <button className="btn shrink-0 bg-dine px-3.5 py-2 text-white" onClick={() => { const s = suggest; setSuggest(null); setPicking(s); }}>Add</button>
            <button aria-label="No thanks" className="grid h-8 w-8 place-items-center text-faint" onClick={() => setSuggest(null)}><X className="h-4 w-4" /></button>
          </div>
        </div>
      )}

      {/* Basket bar */}
      {count > 0 && view === 'menu' && (
        <div className="fixed inset-x-0 bottom-0 z-30 bg-gradient-to-t from-cream via-cream/95 to-transparent px-4 pt-6 pb-[calc(env(safe-area-inset-bottom)+0.75rem)]">
          <button onClick={() => setBasketOpen(true)} className="mx-auto flex w-full max-w-xl items-center justify-between rounded-2xl bg-dine px-4 py-3.5 font-semibold text-white shadow-lg shadow-dine/25 transition active:scale-[0.99]">
            <span className="flex items-center gap-2.5"><span className="grid h-7 min-w-7 place-items-center rounded-full bg-white/20 px-1.5 text-sm">{count}</span> View your order</span>
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
            setJustSent(true);
            setView('table');
            window.scrollTo({ top: 0 });
          }} />
      )}
    </Page>
  );
}

const sectionId = (c: string) => `section-${c.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`;

// A dish without a photo still gets a face: its initial on a warm tint chosen by its section, so the list has rhythm.
const TINTS = ['#F4E9DE', '#EFE6DA', '#F6ECDD', '#ECE4DA', '#F3E6DB', '#EEE8E1'];
const INKS = ['#8F5F3E', '#6E5440', '#9A6A3A', '#5F4A3A', '#8A5A45', '#6B5B4E'];
function Monogram({ name, category, className }: { name: string; category: string; className?: string }) {
  const i = [...(category || name)].reduce((n, ch) => n + ch.charCodeAt(0), 0) % TINTS.length;
  return <span aria-hidden className={cn('grid place-items-center rounded-xl text-xl font-bold', className)} style={{ background: TINTS[i], color: INKS[i] }}>{name.replace(/[^A-Za-z]/g, '').charAt(0).toUpperCase() || '•'}</span>;
}

function Page({ children }: { children: React.ReactNode }) {
  return <div className="min-h-screen bg-cream text-ink">{children}</div>;
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
      <div role="dialog" aria-label={title} className="flex max-h-[90vh] w-full max-w-xl flex-col rounded-t-3xl bg-white shadow-2xl sm:rounded-3xl" onClick={(e) => e.stopPropagation()}>
        <span className="mx-auto mt-2.5 h-1.5 w-10 rounded-full bg-line sm:hidden" aria-hidden />
        <div className="flex items-start justify-between gap-3 px-5 pt-3 pb-3">
          <h2 className="text-xl leading-snug font-bold tracking-tight">{title}</h2>
          <button aria-label="Close" onClick={onClose} className="-mt-1 -mr-2 grid h-9 w-9 place-items-center rounded-full text-muted hover:bg-paper"><X className="h-5 w-5" /></button>
        </div>
        <div className="overflow-y-auto px-5 pb-4">{children}</div>
        {footer && <div className="border-t border-line px-4 pt-3.5 pb-[calc(env(safe-area-inset-bottom)+0.875rem)]">{footer}</div>}
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
      <div className="-mt-1 flex items-start gap-3.5">
        <Monogram name={dish.name} category={dish.category} className="h-16 w-16 shrink-0" />
        <div className="min-w-0">
          {dish.description && <p className="text-sm text-muted">{dish.description}</p>}
          <p className="num mt-1 font-bold">{ksh(dish.price)}</p>
        </div>
      </div>
      {dish.options.map((g) => (
        <fieldset key={g.name} className="mt-6">
          <legend className="mb-2.5 flex w-full items-center justify-between text-sm font-semibold">{g.name}<span className="rounded-full bg-paper px-2 py-0.5 text-[11px] font-semibold text-muted">Choose 1</span></legend>
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
      <label className="mt-6 block">
        <span className="mb-1.5 block text-sm font-semibold">Anything the kitchen should know? <span className="font-normal text-muted">(optional)</span></span>
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
          <li key={l.key} className="flex items-center gap-3 py-3">
            <Monogram name={l.dish.name} category={l.dish.category} className="h-12 w-12 shrink-0 text-base" />
            <div className="min-w-0 flex-1">
              <p className="font-semibold leading-snug">{l.dish.name}</p>
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
function MyTable({ menu, visit, setVisit, call, onMore, justSent }: {
  menu: Menu; visit: Visit; setVisit: (v: Visit) => void; call: <T>(path: string, body?: unknown) => Promise<T>; onMore: () => void; justSent?: boolean;
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
      {justSent && (
        <div className="flex items-center gap-3 rounded-2xl bg-good-soft p-4 ring-1 ring-good/20" role="status">
          <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-good text-white"><Check className="h-5 w-5" /></span>
          <span><span className="block font-semibold text-good">Order sent</span><span className="text-sm text-ink-2">{visit.orders.some((o) => o.status === 'AwaitingApproval') ? 'A waiter will confirm your table, then the kitchen starts.' : 'The kitchen has it. Follow it here.'}</span></span>
        </div>
      )}
      {/* Orders */}
      {visit.orders.map((o) => {
        const s = STATUS[o.status];
        const step = ['New', 'Preparing', 'Ready', 'Served'].indexOf(o.status);
        return (
          <section key={o.id} className="rounded-2xl bg-white p-4 ring-1 ring-line">
            <div className="flex items-center justify-between gap-3">
              <p className="min-w-0 truncate text-xs font-semibold tracking-wider text-faint uppercase">Order {o.number}{o.guestName ? ` · ${o.guestName}` : ''}</p>
              <span className="num shrink-0 text-sm font-bold whitespace-nowrap">{ksh(o.total)}</span>
            </div>
            <p className={cn('mt-1.5 text-[17px] leading-snug font-semibold', s.tone === 'good' ? 'text-good' : s.tone === 'warn' ? 'text-warn' : 'text-ink')}>{s.guest}</p>
            {step >= 0 && (
              <ol className="mt-3.5 grid grid-cols-4 gap-1.5" aria-label="Progress">
                {['Sent', 'Cooking', 'Ready', 'Served'].map((label, i) => (
                  <li key={label} aria-current={i === step ? 'step' : undefined}>
                    <span className={cn('block h-1.5 rounded-full transition', i <= step ? 'bg-dine' : 'bg-line')} />
                    <span className={cn('mt-1.5 block text-[11px] font-semibold', i === step ? 'text-dine' : i < step ? 'text-ink-2' : 'text-faint')}>{label}</span>
                  </li>
                ))}
              </ol>
            )}
            <ul className="mt-3.5 space-y-2 border-t border-line pt-3 text-sm">
              {o.lines.map((l, i) => (
                <li key={i} className="flex justify-between gap-3">
                  <span className="min-w-0"><span className="font-medium">{l.quantity} × {l.name}</span>{(l.options || l.note) && <span className="block text-xs text-muted">{[l.options, l.note].filter(Boolean).join(' · ')}</span>}</span>
                  <span className="num shrink-0 text-muted">{ksh(l.unitPrice * l.quantity)}</span>
                </li>
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
      <section ref={payRef} className="rounded-2xl bg-white p-4 ring-1 ring-line">
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
