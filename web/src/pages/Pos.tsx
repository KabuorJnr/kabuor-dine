// The till. A cashier decides one thing over and over: "what is this customer buying, and have they paid?"
// So the screen answers, in order: is my till open, what's on this ticket, what's the total, how are they paying,
// how much change. Products stay one tap away; the ticket is always visible (a bar on phones); paying never
// leaves the screen. M-Pesa payments Safaricom already sent are listed to tap, so nobody types a code twice.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  Banknote, CheckCircle2, CloudOff, RefreshCw, ChevronRight, Clock, CreditCard, Download, LayoutGrid, List, Lock, MapPin, Minus, Plus, Printer, Search, Share2, ShoppingCart, Smartphone, Trash2, Unlock, User, X,
} from 'lucide-react';
import { api, useApi, useSession } from '../lib/api';
import { enqueue, lastCatalogue, newRef, queued, saveCatalogue, sync, unreachable, discard, type Queued } from '../lib/offline';
import { cn, ksh, msisdn, prettyPhone, toCents } from '../lib/format';
import { ErrorNote, Loading, Sheet, SheetFooter, PageError, noticeDialog } from '../components/ui';
import { ScanButton } from '../components/Scanner';

type TaxType = 'A_Exempt' | 'B_Standard16' | 'C_ZeroRated' | 'D_NonVat' | 'E_Reduced8';
type CatalogItem = { id: string; name: string; code: string; barcode?: string; unit: string; price: number; taxType: TaxType; trackStock: boolean; onHand: number };
type Customer = { id?: string; name: string; phone: string; number?: string };
type Line = { item: CatalogItem; quantity: number };
type Till = { id: string; sessionNumber: string; openingFloat: number; cashSales: number; mpesaSales: number; bankSales: number; openedAt: string };
type Shift = { count: number; total: number; recent: { id: string; number: string; total: number; customer: string; createdAt: string }[] } | null;
type Waiting = { id: string; reference: string; payerName: string; payerPhone: string; left: number; receivedAt: string };
type Method = 'Cash' | 'Mpesa' | 'Bank';
type Receipt = {
  invoiceId: string; receiptNumber: string; date: string; time: string; cashier: string;
  company: { name: string; kraPin: string; phone: string; address: string };
  customer: { name: string; phone: string };
  lines: { description: string; quantity: number; unitPrice: number; total: number }[];
  net: number; tax: number; total: number; amountPaid: number; changeGiven: number; paymentMethod: string; reference: string;
  etims: { status: string; invoiceNo: string; controlCode: string };
};

const WALK_IN: Customer = { name: 'Walk-in customer', phone: '' };
const VAT_RATE: Partial<Record<TaxType, number>> = { B_Standard16: 16, E_Reduced8: 8 };
const METHOD_LABEL: Record<string, string> = { Cash: 'Cash', Mpesa: 'M-Pesa', Bank: 'Card or bank' };
const TICKET_KEY = 'kerp-pos-ticket';

/** Prices are VAT-inclusive; the VAT inside a line depends on its tax type (16%, 8% or none). */
const vatIn = (lines: Line[]) => lines.reduce((sum, l) => {
  const rate = VAT_RATE[l.item.taxType] ?? 0;
  const total = l.quantity * l.item.price;
  return sum + Math.round((total * rate) / (100 + rate));
}, 0);

/** What a customer is likely to hand over: the exact amount, then the next round notes above it. */
function cashSuggestions(total: number) {
  const out = [total];
  for (const step of [10000, 50000, 100000]) {
    const v = Math.ceil(total / step) * step;
    if (v > total && !out.includes(v)) out.push(v);
  }
  if (total < 500000 && !out.includes(500000)) out.push(500000); // the KSh 5,000 handed over for a small sale
  return out.slice(0, 4);
}

const ago = (iso: string) => {
  const m = Math.max(0, Math.round((Date.now() - Date.parse(iso)) / 60000));
  return m < 1 ? 'just now' : m === 1 ? '1 min ago' : `${m} min ago`;
};
const timeOf = (iso: string) => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });
const titleCase = (s: string) => s.toLowerCase().replace(/\b\w/g, (c) => c.toUpperCase());

function loadTicket(): { lines: Line[]; customer: Customer } {
  try {
    const t = JSON.parse(sessionStorage.getItem(TICKET_KEY) ?? 'null');
    if (t && Array.isArray(t.lines)) return t;
  } catch {}
  return { lines: [], customer: WALK_IN };
}

export default function Pos() {
  const session = useSession();
  const [locationId, setLocationId] = useState('');
  const live = useApi<any>(locationId ? `/pos/data?locationId=${locationId}` : '/pos/data');
  // Offline: keep selling from the last catalogue this device saw.
  useEffect(() => {
    if (live.data) saveCatalogue(live.data);
  }, [live.data]);
  const cached = live.error && !live.data ? lastCatalogue() : null;
  const data = live.data ?? cached?.data ?? null;
  const error = data ? null : live.error;
  const reload = live.reload;
  const offline = Boolean(cached);
  const [waiting, setWaiting] = useState<Queued[]>(queued);
  const [syncing, setSyncing] = useState(false);
  const flush = useCallback(async () => {
    if (!queued().length) return;
    setSyncing(true);
    const r = await sync().finally(() => setSyncing(false));
    if (r.sent) (reload(), setNotice(`${r.sent} offline sale${r.sent === 1 ? '' : 's'} sent.`));
  }, [reload]);
  useEffect(() => {
    const onQueue = () => setWaiting(queued());
    window.addEventListener('kerp-pos-queue', onQueue);
    window.addEventListener('online', flush);
    const t = setInterval(flush, 30000);
    flush();
    return () => (window.removeEventListener('kerp-pos-queue', onQueue), window.removeEventListener('online', flush), clearInterval(t));
  }, [flush]);
  const shift = useApi<Shift>('/pos/shift');
  const [ticket, setTicket] = useState(loadTicket);
  const [q, setQ] = useState('');
  const [kind, setKind] = useState<'all' | 'goods' | 'services'>('all');
  const [view, setView] = useState<'grid' | 'list'>(() => {
    try { return localStorage.getItem('kerp-pos-view') === 'list' ? 'list' : 'grid'; } catch { return 'grid'; }
  });
  const [notice, setNotice] = useState('');
  const [sheet, setSheet] = useState<null | 'pay' | 'customer' | 'open' | 'close' | 'ticket'>(null);
  const [receipt, setReceipt] = useState<Receipt | null>(null);
  const search = useRef<HTMLInputElement>(null);
  const [installPrompt, setInstallPrompt] = useState<any>(null);
  const [isStandalone, setIsStandalone] = useState(false);

  useEffect(() => {
    const handler = (e: any) => {
      e.preventDefault();
      setInstallPrompt(e);
    };
    window.addEventListener('beforeinstallprompt', handler);
    if (window.matchMedia('(display-mode: standalone)').matches || (window.navigator as any).standalone) {
      setIsStandalone(true);
    }
    return () => window.removeEventListener('beforeinstallprompt', handler);
  }, []);

  const triggerInstall = async () => {
    if (!installPrompt) {
      await noticeDialog({ title: 'Install the till on this device', body: 'Open your browser’s menu (⋮ or Share) and choose “Install app” or “Add to Home Screen”. The till then opens like an app and keeps selling when the internet drops.' });
      return;
    }
    installPrompt.prompt();
    const outcome = await installPrompt.userChoice;
    if (outcome?.outcome === 'accepted') {
      setInstallPrompt(null);
    }
  };

  useEffect(() => {
    try { sessionStorage.setItem(TICKET_KEY, JSON.stringify(ticket)); } catch {}
  }, [ticket]);
  useEffect(() => {
    try { localStorage.setItem('kerp-pos-view', view); } catch {}
  }, [view]);
  useEffect(() => {
    if (!notice) return;
    const t = setTimeout(() => setNotice(''), 2600);
    return () => clearTimeout(t);
  }, [notice]);

  const till: Till | null = data?.session ?? null;
  // Only things with a selling price: ingredients and packaging bought for production stay off the till.
  const items: CatalogItem[] = useMemo(() => (data?.items ?? []).filter((i: CatalogItem) => i.price > 0), [data]);
  const lines = ticket.lines;
  const total = lines.reduce((s, l) => s + l.quantity * l.item.price, 0);
  const count = lines.reduce((s, l) => s + l.quantity, 0);

  // Keep ticket prices and stock in step with the catalog (after a refresh or a store change).
  useEffect(() => {
    if (!items.length) return;
    setTicket((t) => ({ ...t, lines: t.lines.map((l) => ({ ...l, item: items.find((i) => i.id === l.item.id) ?? l.item })) }));
  }, [data]); // eslint-disable-line react-hooks/exhaustive-deps

  const add = useCallback((item: CatalogItem, by = 1) => {
    setTicket((t) => {
      const have = t.lines.find((l) => l.item.id === item.id)?.quantity ?? 0;
      if (item.trackStock && have + by > item.onHand) {
        setNotice(item.onHand <= 0 ? `${item.name} is out of stock here.` : `Only ${item.onHand} ${item.name} in stock here.`);
        return t;
      }
      const next = have ? t.lines.map((l) => (l.item.id === item.id ? { ...l, quantity: l.quantity + by } : l)) : [...t.lines, { item, quantity: by }];
      return { ...t, lines: next };
    });
  }, []);
  const setQty = (id: string, qty: number) =>
    setTicket((t) => ({
      ...t,
      lines: t.lines
        .map((l) => {
          if (l.item.id !== id) return l;
          const capped = l.item.trackStock ? Math.min(qty, l.item.onHand) : qty;
          if (capped < qty) setNotice(`Only ${l.item.onHand} ${l.item.name} in stock here.`);
          return { ...l, quantity: Math.max(0, capped) };
        })
        .filter((l) => l.quantity > 0),
    }));
  const clear = () => setTicket({ lines: [], customer: WALK_IN });

  // F2 pays, from anywhere on the till.
  useEffect(() => {
    const on = (e: KeyboardEvent) => {
      if (e.key === 'F2' && lines.length && till) {
        e.preventDefault();
        setSheet('pay');
      }
    };
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, [lines.length, till]);

  const shown = useMemo(() => {
    const term = q.trim().toLowerCase();
    return items.filter((i) => (kind === 'all' || (kind === 'goods') === i.trackStock) && (!term || i.name.toLowerCase().includes(term) || i.code?.toLowerCase().includes(term)));
  }, [items, q, kind]);
  const counts = { all: items.length, goods: items.filter((i) => i.trackStock).length, services: items.filter((i) => !i.trackStock).length };

  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  // A barcode scanner types the code and presses Enter: add the exact match (or the only result) and clear.
  const byCode = (raw: string) => {
    const term = raw.trim().toLowerCase();
    return items.find((i) => (i.barcode && i.barcode.toLowerCase() === term) || (i.code && i.code.toLowerCase() === term));
  };
  const onSearchKey = (e: React.KeyboardEvent) => {
    if (e.key !== 'Enter') return;
    const hit = byCode(q) ?? (shown.length === 1 ? shown[0] : null);
    if (hit) {
      add(hit);
      setQ('');
    }
  };
  // The camera: add the product if the code is known, else leave the code in the search box.
  const onCameraCode = (code: string) => {
    const hit = byCode(code);
    if (hit) add(hit);
    else setQ(code);
  };

  const done = (r: Receipt) => {
    setReceipt(r);
    setSheet(null);
    clear();
    reload();
    shift.reload();
  };

  const ticketPanel = (
    <TicketPanel
      lines={lines}
      customer={ticket.customer}
      total={total}
      count={count}
      vat={data.company?.vatRegistered ? vatIn(lines) : 0}
      tillOpen={Boolean(till)}
      onQty={setQty}
      onClear={clear}
      onCustomer={() => setSheet('customer')}
      onPay={() => setSheet('pay')}
      onOpenTill={() => setSheet('open')}
    />
  );

  return (
    <>
    <div className="-mx-4 -mt-5 -mb-28 flex h-[calc(100svh-61px-3.6rem)] flex-col sm:-mx-6 lg:-mx-8 lg:-mt-6 lg:-mb-12 lg:h-[calc(100svh-3.5rem)] print:hidden">
      {(offline || waiting.length > 0) && (
        <div className={cn('flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2 text-sm sm:px-6', offline ? 'bg-warn-soft text-warn' : 'bg-brand-soft text-brand')} role="status">
          {offline && <><CloudOff className="h-4 w-4" /><b>No internet.</b> Selling from the price list saved {ago(lastCatalogue()?.at ?? new Date().toISOString())}; sales are kept on this device.</>}
          {waiting.filter((w) => !w.error).length > 0 && <span>{waiting.filter((w) => !w.error).length} sale{waiting.filter((w) => !w.error).length === 1 ? '' : 's'} ({ksh(waiting.filter((w) => !w.error).reduce((a, w) => a + w.total, 0))}) waiting to send.</span>}
          {waiting.some((w) => w.error) && <span className="text-bad">{waiting.filter((w) => w.error).length} offline sale{waiting.filter((w) => w.error).length === 1 ? ' was' : 's were'} refused: {waiting.find((w) => w.error)!.error} <button className="underline" onClick={() => discard(waiting.find((w) => w.error)!.clientRef)}>Dismiss</button></span>}
          {waiting.length > 0 && (
            <button className="ml-auto inline-flex items-center gap-1 font-semibold underline" disabled={syncing} onClick={flush}>
              <RefreshCw className={cn('h-3.5 w-3.5', syncing && 'animate-spin')} /> Send now
            </button>
          )}
        </div>
      )}
      {/* Till bar: which store, is the drawer open, how the shift is going */}
      <header className="flex flex-wrap items-center gap-x-4 gap-y-2 border-b border-line bg-white px-4 py-2.5 sm:px-6">
        <label className="flex items-center gap-1.5 text-sm font-semibold">
          <MapPin className="h-4 w-4 text-muted" />
          {data.locations?.length > 1 ? (
            <select value={locationId || data.location?.id} onChange={(e) => setLocationId(e.target.value)} className="rounded-md bg-paper px-2 py-1 ring-1 ring-line" aria-label="Store">
              {data.locations.map((l: any) => (
                <option key={l.id} value={l.id}>{l.name}</option>
              ))}
            </select>
          ) : (
            <span>{data.location?.name ?? 'Main store'}</span>
          )}
        </label>
        {till ? (
          <>
            <span className="order-last flex basis-full items-center gap-1.5 text-sm text-muted sm:order-none sm:basis-auto">
              <span className="h-2 w-2 rounded-full bg-good" /> Till open since {timeOf(till.openedAt)}
              {shift.data && (
                <span className="num">
                  {' '}·{' '}
                  {shift.data.count ? (
                    <>
                      <b className="text-ink">{shift.data.count}</b> sale{shift.data.count === 1 ? '' : 's'}, <b className="text-ink">{ksh(shift.data.total)}</b>
                    </>
                  ) : (
                    'no sales yet'
                  )}
                </span>
              )}
            </span>
            <div className="ml-auto flex items-center gap-2">
              {!isStandalone ? (
                <button
                  type="button"
                  onClick={triggerInstall}
                  title="Install offline POS app on this device for kiosks and supermarkets"
                  className="btn btn-ghost py-1 text-xs"
                >
                  <Download className="h-3.5 w-3.5 text-brand" /> Install App
                </button>
              ) : (
                <span className="inline-flex items-center gap-1 rounded-full bg-good-soft px-2 py-0.5 text-2xs font-semibold text-good">
                  <span className="h-1.5 w-1.5 rounded-full bg-good" /> Kiosk App
                </span>
              )}
              <button onClick={() => setSheet('close')} className="btn btn-ghost py-1.5">
                <Lock className="h-4 w-4" /> Close till
              </button>
            </div>
          </>
        ) : (
          <>
            <span className="flex items-center gap-1.5 text-sm font-semibold text-warn">
              <span className="h-2 w-2 rounded-full bg-gold" /> Till closed
            </span>
            <div className="ml-auto flex items-center gap-2">
              {!isStandalone && (
                <button
                  type="button"
                  onClick={triggerInstall}
                  title="Install offline POS app on this device for kiosks and supermarkets"
                  className="btn btn-ghost py-1 text-xs"
                >
                  <Download className="h-3.5 w-3.5 text-brand" /> Install App
                </button>
              )}
              <button onClick={() => setSheet('open')} className="btn btn-primary py-1.5">
                <Unlock className="h-4 w-4" /> Open till
              </button>
            </div>
          </>
        )}
      </header>

      <div className="flex min-h-0 flex-1">
        {/* Products */}
        <section className="flex min-w-0 flex-1 flex-col">
          <div className="space-y-2.5 border-b border-line bg-white px-4 py-3 sm:px-6">
            <div className="flex gap-2">
            <label className="relative block flex-1">
              <Search className="pointer-events-none absolute top-1/2 left-3 h-4 w-4 -translate-y-1/2 text-faint" />
              <input
                ref={search}
                autoFocus
                className="field pl-9"
                value={q}
                onChange={(e) => setQ(e.target.value)}
                onKeyDown={onSearchKey}
                placeholder="Scan a barcode, or type a product name or code"
                aria-label="Find a product"
              />
              {q && (
                <button onClick={() => setQ('')} className="absolute top-1/2 right-2 grid h-7 w-7 -translate-y-1/2 place-items-center rounded-full text-muted hover:bg-paper" aria-label="Clear search">
                  <X className="h-4 w-4" />
                </button>
              )}
            </label>
            <ScanButton onCode={onCameraCode} title="Scan a product" />
            </div>
            <div className="flex items-center gap-1.5">
              {(
                [
                  ['all', 'All'],
                  ['goods', 'Goods'],
                  ['services', 'Services'],
                ] as const
              ).map(([id, label]) => (
                <button key={id} aria-pressed={kind === id} onClick={() => setKind(id)} className={cn('flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-semibold ring-1 transition', kind === id ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white text-ink-2 ring-line hover:ring-faint')}>
                  {label} <span className={cn('num text-xs', kind === id ? 'text-white/60' : 'text-faint')}>{counts[id]}</span>
                </button>
              ))}
              <div className="ml-auto flex rounded-md ring-1 ring-line" role="group" aria-label="Layout">
                <button aria-pressed={view === 'grid'} onClick={() => setView('grid')} className={cn('grid h-8 w-8 place-items-center rounded-l-md', view === 'grid' ? 'bg-brand-soft text-brand' : 'bg-white text-muted')} aria-label="Tiles">
                  <LayoutGrid className="h-4 w-4" />
                </button>
                <button aria-pressed={view === 'list'} onClick={() => setView('list')} className={cn('grid h-8 w-8 place-items-center rounded-r-md', view === 'list' ? 'bg-brand-soft text-brand' : 'bg-white text-muted')} aria-label="List">
                  <List className="h-4 w-4" />
                </button>
              </div>
            </div>
          </div>

          <div className="relative min-h-0 flex-1 overflow-y-auto bg-paper p-3 pb-24 sm:p-4 lg:pb-4">
            {!till && (
              <div className="mb-3 flex flex-wrap items-center gap-3 rounded-md bg-warn-soft px-4 py-3 text-sm text-warn">
                <span className="flex-1 font-medium">The till is closed. Count the float in the drawer and open the till to start selling.</span>
                <button onClick={() => setSheet('open')} className="btn btn-primary py-1.5">Open till</button>
              </div>
            )}
            {shown.length === 0 ? (
              <div className="py-16 text-center">
                <p className="font-semibold">{items.length ? `Nothing matches “${q}”` : 'No products yet'}</p>
                <p className="mt-1 text-sm text-muted">{items.length ? 'Try part of the name, or the code on the label.' : 'The owner adds products in Settings → Products.'}</p>
              </div>
            ) : view === 'grid' ? (
              <div className="grid grid-cols-2 gap-2 sm:grid-cols-3 xl:grid-cols-4">
                {shown.map((i) => (
                  <ProductTile key={i.id} item={i} inTicket={lines.find((l) => l.item.id === i.id)?.quantity ?? 0} onAdd={() => add(i)} />
                ))}
              </div>
            ) : (
              <ul className="divide-y divide-line overflow-hidden rounded-md bg-white ring-1 ring-line">
                {shown.map((i) => (
                  <ProductRow key={i.id} item={i} inTicket={lines.find((l) => l.item.id === i.id)?.quantity ?? 0} onAdd={() => add(i)} />
                ))}
              </ul>
            )}
          </div>
        </section>

        {/* Ticket, always visible on a computer */}
        <aside className="hidden w-[24rem] shrink-0 flex-col border-l border-line bg-white lg:flex xl:w-[26rem]">{ticketPanel}</aside>
      </div>

      {/* Phone: the ticket as a bar above the tabs */}
      {lines.length > 0 && (
        <button onClick={() => setSheet('ticket')} className="fixed inset-x-3 bottom-[calc(4.1rem+env(safe-area-inset-bottom))] z-20 flex items-center gap-3 rounded-lg bg-ink px-4 py-3 text-white shadow-xl lg:hidden">
          <span className="grid h-8 min-w-8 place-items-center rounded-full bg-white/15 px-2 text-sm font-bold num">{count}</span>
          <span className="flex-1 text-left text-sm font-semibold">View ticket</span>
          <span className="num font-display text-lg font-semibold">{ksh(total)}</span>
          <ChevronRight className="h-5 w-5 text-white/60" />
        </button>
      )}

      {notice && (
        <p role="status" className="fixed top-20 left-1/2 z-50 -translate-x-1/2 rounded-full bg-ink px-4 py-2 text-sm font-semibold text-white shadow-lg">
          {notice}
        </p>
      )}

      <Sheet open={sheet === 'ticket'} onClose={() => setSheet(null)} title={`Ticket · ${count} item${count === 1 ? '' : 's'}`}>
        <div className="-m-5 flex h-[calc(100%+2.5rem)] flex-col">{ticketPanel}</div>
      </Sheet>
      <Sheet open={sheet === 'pay'} onClose={() => setSheet(null)} title={`Take payment · ${ksh(total)}`}>
        {sheet === 'pay' && <PayForm total={total} lines={lines} customer={ticket.customer} locationId={locationId || data.location?.id} shortcode={data.company?.mpesaShortcode} onDone={done} />}
      </Sheet>
      <Sheet open={sheet === 'customer'} onClose={() => setSheet(null)} title="Who is buying?">
        <CustomerPicker
          customers={data.customers ?? []}
          current={ticket.customer}
          onPick={(c) => {
            setTicket((t) => ({ ...t, customer: c }));
            setSheet(null);
          }}
        />
      </Sheet>
      <Sheet open={sheet === 'open'} onClose={() => setSheet(null)} title="Open the till" description="Count the cash in the drawer before the first sale.">
        <OpenTill onDone={() => (setSheet(null), reload(), shift.reload(), search.current?.focus())} />
      </Sheet>
      <Sheet open={sheet === 'close'} onClose={() => setSheet(null)} title="Close the till" description="Count the cash in the drawer. Any difference is recorded.">
        {till && <CloseTill till={till} shift={shift.data} onDone={() => (setSheet(null), reload(), shift.reload())} />}
      </Sheet>

    </div>
    {/* Outside the till's print:hidden wrapper, so Print gives just the receipt. */}
    {receipt && <ReceiptView receipt={receipt} cashier={session?.user.name ?? ''} onNext={() => (setReceipt(null), search.current?.focus())} />}
    </>
  );
}

function Stock({ item }: { item: CatalogItem }) {
  if (!item.trackStock) return <span className="text-xs text-faint">Service</span>;
  if (item.onHand <= 0) return <span className="text-xs font-semibold text-bad">Out of stock</span>;
  return <span className={cn('num text-xs', item.onHand <= 5 ? 'font-semibold text-warn' : 'text-muted')}>{item.onHand} left</span>;
}

function ProductTile({ item, inTicket, onAdd }: { item: CatalogItem; inTicket: number; onAdd: () => void }) {
  const out = item.trackStock && item.onHand <= 0;
  return (
    <button
      onClick={onAdd}
      aria-label={`Add ${item.name}, ${ksh(item.price)}`}
      title={item.name}
      className={cn('relative flex min-h-[8rem] flex-col rounded-md bg-white p-3 text-left ring-1 transition active:scale-[0.98]', inTicket ? 'ring-2 ring-brand' : 'ring-line hover:ring-faint', out && 'opacity-60')}
    >
      {inTicket > 0 && <span className="num absolute top-2 right-2 grid h-6 min-w-6 place-items-center rounded-full bg-brand px-1.5 text-xs font-bold text-white">{inTicket}</span>}
      {/* Three lines: the size or colour that tells two products apart is usually at the end of the name. */}
      <span className="line-clamp-3 pr-6 text-sm leading-snug font-semibold">{item.name}</span>
      <span className="mt-1 flex min-w-0 items-center gap-1.5">
        <Stock item={item} />
        {item.code && <span className="truncate font-mono text-2xs text-faint">· {item.code}</span>}
      </span>
      <span className="num mt-auto pt-2 font-display text-lg font-semibold">{ksh(item.price)}</span>
    </button>
  );
}

function ProductRow({ item, inTicket, onAdd }: { item: CatalogItem; inTicket: number; onAdd: () => void }) {
  const out = item.trackStock && item.onHand <= 0;
  return (
    <li>
      <button onClick={onAdd} className={cn('flex w-full items-center gap-3 px-4 py-2.5 text-left hover:bg-paper', out && 'opacity-60')}>
        <span className="min-w-0 flex-1">
          <span className="line-clamp-2 font-semibold">{item.name}</span>
          <span className="text-xs text-faint">{item.code || item.unit}</span>
        </span>
        <Stock item={item} />
        <span className="num w-24 text-right font-bold">{ksh(item.price)}</span>
        <span className={cn('num grid h-7 w-7 place-items-center rounded-full text-xs font-bold', inTicket ? 'bg-brand text-white' : 'bg-paper text-muted ring-1 ring-line')}>{inTicket || <Plus className="h-3.5 w-3.5" />}</span>
      </button>
    </li>
  );
}

function TicketPanel(p: {
  lines: Line[]; customer: Customer; total: number; count: number; vat: number; tillOpen: boolean;
  onQty: (id: string, q: number) => void; onClear: () => void; onCustomer: () => void; onPay: () => void; onOpenTill: () => void;
}) {
  return (
    <>
      <div className="flex items-center gap-2 border-b border-line px-4 py-3">
        <button onClick={p.onCustomer} className="flex min-w-0 flex-1 items-center gap-2.5 rounded-md px-2 py-1.5 text-left hover:bg-paper">
          <span className="grid h-8 w-8 shrink-0 place-items-center rounded-full bg-paper text-muted ring-1 ring-line">
            <User className="h-4 w-4" />
          </span>
          <span className="min-w-0">
            <b className="block truncate text-sm">{p.customer.name}</b>
            <span className="block truncate text-xs text-muted">{p.customer.phone ? prettyPhone(p.customer.phone) : 'Add a name or phone for the receipt'}</span>
          </span>
        </button>
        {p.lines.length > 0 && (
          <button onClick={p.onClear} className="grid h-9 w-9 place-items-center rounded-full text-muted hover:bg-bad-soft hover:text-bad" aria-label="Clear the ticket" title="Clear the ticket">
            <Trash2 className="h-4 w-4" />
          </button>
        )}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {p.lines.length === 0 ? (
          <div className="grid h-full place-items-center px-6 py-12 text-center">
            <div>
              <ShoppingCart className="mx-auto h-9 w-9 text-line" />
              <p className="mt-2 font-semibold">Nothing on this ticket yet</p>
              <p className="mt-1 text-sm text-muted">Tap a product, or scan its barcode.</p>
            </div>
          </div>
        ) : (
          <ul className="divide-y divide-line">
            {p.lines.map(({ item, quantity }) => (
              <li key={item.id} className="flex items-center gap-3 px-4 py-3">
                <div className="min-w-0 flex-1">
                  <p className="line-clamp-2 text-sm font-semibold">{item.name}</p>
                  <p className="num text-xs text-muted">{ksh(item.price)} each</p>
                </div>
                <div className="flex items-center rounded-md ring-1 ring-line">
                  <button onClick={() => p.onQty(item.id, quantity - 1)} className="grid h-8 w-8 place-items-center rounded-l-md hover:bg-paper" aria-label={`One less ${item.name}`}>
                    {quantity === 1 ? <Trash2 className="h-3.5 w-3.5 text-bad" /> : <Minus className="h-3.5 w-3.5" />}
                  </button>
                  <input
                    className="num h-8 w-10 border-x border-line text-center text-sm font-bold focus:outline-none"
                    inputMode="decimal"
                    value={quantity}
                    onChange={(e) => {
                      const v = parseFloat(e.target.value);
                      if (!Number.isNaN(v)) p.onQty(item.id, v);
                    }}
                    aria-label={`How many ${item.name}`}
                  />
                  <button onClick={() => p.onQty(item.id, quantity + 1)} className="grid h-8 w-8 place-items-center rounded-r-md hover:bg-paper" aria-label={`One more ${item.name}`}>
                    <Plus className="h-3.5 w-3.5" />
                  </button>
                </div>
                <span className="num w-20 text-right text-sm font-bold">{ksh(quantity * item.price)}</span>
              </li>
            ))}
          </ul>
        )}
      </div>

      <div className="border-t border-line bg-paper px-4 py-4">
        <div className="flex items-baseline justify-between">
          <span className="text-sm text-muted">
            {p.count} item{p.count === 1 ? '' : 's'}
            {p.vat > 0 && <> · includes VAT {ksh(p.vat)}</>}
          </span>
          <span className="num font-display text-3xl font-semibold tracking-tight">{ksh(p.total)}</span>
        </div>
        {p.tillOpen ? (
          <button onClick={p.onPay} disabled={!p.lines.length} className="btn btn-primary mt-3 w-full py-3.5 text-base">
            {p.lines.length ? <>Charge {ksh(p.total)}</> : 'Add products to charge'}
            <kbd className="ml-1 hidden rounded bg-white/20 px-1.5 text-2xs lg:inline">F2</kbd>
          </button>
        ) : (
          <button onClick={p.onOpenTill} className="btn btn-ghost mt-3 w-full py-3.5 text-base">
            <Unlock className="h-4 w-4" /> Open the till to charge
          </button>
        )}
      </div>
    </>
  );
}

function PayForm({ total, lines, customer, locationId, shortcode, onDone }: { total: number; lines: Line[]; customer: Customer; locationId?: string; shortcode?: string; onDone: (r: Receipt) => void }) {
  const [method, setMethod] = useState<Method>('Cash');
  const [given, setGiven] = useState(String(total / 100));
  const [code, setCode] = useState('');
  const [ref, setRef] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const [waiting, setWaiting] = useState<Waiting[] | null>(null);

  // While M-Pesa is chosen, look every few seconds for payments Safaricom has sent but nothing has used.
  useEffect(() => {
    if (method !== 'Mpesa') return;
    let live = true;
    const load = () => api<Waiting[]>('/pos/mpesa-waiting').then((w) => live && setWaiting(w)).catch(() => {});
    load();
    const t = setInterval(load, 4000);
    return () => {
      live = false;
      clearInterval(t);
    };
  }, [method]);

  const givenCents = toCents(given);
  const change = givenCents - total;
  const codeOk = /^[A-Z0-9]{10}$/.test(code);
  const blocked = method === 'Cash' ? (givenCents < total ? `${ksh(total - givenCents)} more needed` : '') : method === 'Mpesa' && !codeOk ? 'Choose the payment, or type its 10-character code' : '';

  const session = useSession();
  const submit = async () => {
    setState({ busy: true, error: '' });
    const clientRef = newRef();
    const body = {
          customerId: customer.id,
          customerName: customer.id ? undefined : customer.name === WALK_IN.name ? undefined : customer.name,
          customerPhone: customer.phone || undefined,
          locationId,
          paymentMethod: method,
          amountPaid: method === 'Cash' ? givenCents : total,
          changeGiven: method === 'Cash' ? Math.max(0, change) : 0,
          reference: method === 'Mpesa' ? code : ref,
          lines: lines.map((l) => ({ itemId: l.item.id, description: l.item.name, quantity: l.quantity, unitPrice: l.item.price, taxType: l.item.taxType })),
    };
    try {
      onDone(await api<Receipt>('/pos/checkout', { body: { ...body, clientRef } }));
    } catch (e) {
      if (!unreachable(e)) return setState({ busy: false, error: (e as Error).message });
      // No connection: keep the sale here and give the customer a receipt now; it's sent when the internet is back.
      const at = new Date();
      enqueue({ clientRef, body, at: at.toISOString(), total });
      const kenya = new Date(at.getTime() + 3 * 3600e3).toISOString();
      onDone({
        invoiceId: '', receiptNumber: `OFF-${clientRef.slice(-6).toUpperCase()}`, date: kenya.slice(0, 10), time: kenya.slice(11, 16), cashier: session?.user.name ?? '',
        company: { name: session?.company.name ?? '', kraPin: session?.company.kraPin ?? '', phone: '', address: '' },
        customer: { name: customer.name, phone: customer.phone },
        lines: lines.map((l) => ({ description: l.item.name, quantity: l.quantity, unitPrice: l.item.price, total: l.quantity * l.item.price })),
        net: total - (session?.company.vatRegistered ? vatIn(lines) : 0), tax: session?.company.vatRegistered ? vatIn(lines) : 0, total, amountPaid: body.amountPaid, changeGiven: body.changeGiven, paymentMethod: method, reference: body.reference ?? '',
        etims: { status: 'Offline', invoiceNo: '', controlCode: '' },
      });
    }
  };

  const matches = (waiting ?? []).filter((w) => w.left >= total);
  return (
    <div className="space-y-5">
      <div className="grid grid-cols-3 gap-2" role="radiogroup" aria-label="How is the customer paying?">
        {(
          [
            ['Cash', Banknote],
            ['Mpesa', Smartphone],
            ['Bank', CreditCard],
          ] as const
        ).map(([m, Icon]) => (
          <button key={m} role="radio" aria-checked={method === m} onClick={() => setMethod(m)} className={cn('flex flex-col items-center gap-1.5 rounded-md py-3 text-sm font-semibold ring-1 transition', method === m ? 'bg-brand-soft text-brand ring-2 ring-brand' : 'bg-white text-ink-2 ring-line hover:ring-faint')}>
            <Icon className="h-5 w-5" /> {METHOD_LABEL[m]}
          </button>
        ))}
      </div>

      {method === 'Cash' && (
        <div className="space-y-3">
          <label className="block">
            <span className="mb-1.5 block text-sm font-semibold">Cash given</span>
            <input autoFocus className="field num font-display text-2xl font-semibold" inputMode="decimal" value={given} onChange={(e) => setGiven(e.target.value)} onFocus={(e) => e.target.select()} />
          </label>
          <div className="flex flex-wrap gap-2">
            {cashSuggestions(total).map((v, i) => (
              <button key={v} onClick={() => setGiven(String(v / 100))} className={cn('rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1', givenCents === v ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line hover:ring-faint')}>
                {i === 0 ? 'Exact' : ksh(v)}
              </button>
            ))}
          </div>
          <div className={cn('flex items-baseline justify-between rounded-md px-4 py-3', change >= 0 ? 'bg-good-soft' : 'bg-bad-soft')}>
            <span className={cn('text-sm font-semibold', change >= 0 ? 'text-good' : 'text-bad')}>{change >= 0 ? 'Change to give' : 'Still to pay'}</span>
            <span className={cn('num font-display text-2xl font-semibold', change >= 0 ? 'text-good' : 'text-bad')}>{ksh(Math.abs(change))}</span>
          </div>
        </div>
      )}

      {method === 'Mpesa' && (
        <div className="space-y-4">
          <p className="text-sm text-muted">
            {shortcode ? <>Customer pays <b className="text-ink">Till {shortcode}</b> for {ksh(total)}. </> : <>Customer pays {ksh(total)} by M-Pesa. </>}
            Their payment shows up here; tap it.
          </p>
          <div>
            <p className="mb-2 flex items-center gap-2 text-xs font-semibold tracking-wider text-faint uppercase">
              <span className="h-1.5 w-1.5 animate-pulse rounded-full bg-mpesa" /> M-Pesa received in the last hour
            </p>
            {waiting === null ? (
              <Loading className="py-6" />
            ) : waiting.length === 0 ? (
              <p className="rounded-md bg-paper px-4 py-4 text-sm text-muted">Nothing yet. It usually arrives a few seconds after the customer gets their M-Pesa message.</p>
            ) : (
              <ul className="divide-y divide-line overflow-hidden rounded-md ring-1 ring-line">
                {[...matches, ...waiting.filter((w) => w.left < total)].map((w) => {
                  const fits = w.left >= total;
                  return (
                    <li key={w.id}>
                      <button disabled={!fits} onClick={() => setCode(w.reference)} className={cn('flex w-full items-center gap-3 px-4 py-3 text-left', code === w.reference ? 'bg-brand-soft' : 'bg-white hover:bg-paper', !fits && 'opacity-50')}>
                        <span className="min-w-0 flex-1">
                          <b className="block truncate text-sm">{w.payerName ? titleCase(w.payerName) : prettyPhone(w.payerPhone)}</b>
                          <span className="text-xs text-muted">
                            <span className="font-mono">{w.reference}</span> · {ago(w.receivedAt)}
                          </span>
                        </span>
                        <span className="text-right">
                          <b className="num block">{ksh(w.left)}</b>
                          <span className={cn('text-xs', fits ? (w.left === total ? 'font-semibold text-good' : 'text-muted') : 'text-bad')}>{fits ? (w.left === total ? 'Exact amount' : `${ksh(w.left - total)} extra`) : 'Not enough'}</span>
                        </span>
                        {code === w.reference && <CheckCircle2 className="h-5 w-5 shrink-0 text-brand" />}
                      </button>
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
          <label className="block">
            <span className="mb-1.5 block text-sm font-semibold">Or type the M-Pesa code</span>
            <input className="field font-mono uppercase" value={code} maxLength={10} onChange={(e) => setCode(e.target.value.toUpperCase().replace(/[^A-Z0-9]/g, ''))} placeholder="e.g. TKA1B2C3D4" />
            <span className="mt-1 block text-xs text-muted">The 10 letters and numbers at the start of the customer’s M-Pesa message.</span>
          </label>
        </div>
      )}

      {method === 'Bank' && (
        <label className="block">
          <span className="mb-1.5 block text-sm font-semibold">Card slip or bank reference (optional)</span>
          <input autoFocus className="field" value={ref} onChange={(e) => setRef(e.target.value)} placeholder="The approval number on the card machine slip" />
        </label>
      )}

      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button onClick={submit} disabled={state.busy || Boolean(blocked)} className="btn btn-primary">
        {state.busy ? 'Recording the sale…' : blocked || <>Complete sale · {ksh(total)}</>}
      </button>
      </SheetFooter>
    </div>
  );
}

function CustomerPicker({ customers, current, onPick }: { customers: { id: string; number: string; name: string; phone: string }[]; current: Customer; onPick: (c: Customer) => void }) {
  const [q, setQ] = useState('');
  const [fresh, setFresh] = useState({ name: '', phone: '' });
  const term = q.trim().toLowerCase();
  const digits = term.replace(/\D/g, '');
  const shown = customers.filter((c) => c.name !== WALK_IN.name && c.name !== 'Walk-in Customer').filter((c) => !term || c.name.toLowerCase().includes(term) || c.number.toLowerCase().includes(term) || (digits.length >= 3 && c.phone.includes(digits.replace(/^0/, ''))));
  return (
    <div className="space-y-5">
      <button onClick={() => onPick(WALK_IN)} className={cn('flex w-full items-center gap-3 rounded-md px-4 py-3 text-left ring-1', current.name === WALK_IN.name ? 'bg-brand-soft ring-brand' : 'bg-white ring-line hover:ring-faint')}>
        <User className="h-5 w-5 text-muted" />
        <span>
          <b className="block text-sm">Walk-in customer</b>
          <span className="text-xs text-muted">No name on the receipt</span>
        </span>
      </button>

      <div>
        <input autoFocus className="field" value={q} onChange={(e) => setQ(e.target.value)} placeholder="Search by name, phone or customer number" aria-label="Search customers" />
        <ul className="mt-2 max-h-72 divide-y divide-line overflow-y-auto rounded-md ring-1 ring-line">
          {shown.slice(0, 30).map((c) => (
            <li key={c.id}>
              <button onClick={() => onPick({ id: c.id, name: c.name, phone: c.phone, number: c.number })} className="flex w-full items-center justify-between gap-3 bg-white px-4 py-2.5 text-left hover:bg-paper">
                <span className="min-w-0">
                  <b className="block truncate text-sm">{c.name}</b>
                  <span className="text-xs text-muted">{c.phone ? prettyPhone(c.phone) : 'No phone'} · {c.number}</span>
                </span>
                {current.id === c.id && <CheckCircle2 className="h-5 w-5 text-brand" />}
              </button>
            </li>
          ))}
          {shown.length === 0 && <li className="px-4 py-4 text-sm text-muted">No customer matches. Add them below.</li>}
        </ul>
      </div>

      <form
        className="space-y-3 rounded-md bg-paper p-4"
        onSubmit={(e) => {
          e.preventDefault();
          onPick({ name: fresh.name.trim() || 'Walk-in customer', phone: fresh.phone.trim() });
        }}
      >
        <p className="text-sm font-semibold">New customer</p>
        <input className="field" value={fresh.name} onChange={(e) => setFresh({ ...fresh, name: e.target.value })} placeholder="Name" aria-label="New customer name" />
        <input className="field" inputMode="tel" value={fresh.phone} onChange={(e) => setFresh({ ...fresh, phone: e.target.value })} placeholder="Phone, to send the receipt on WhatsApp" aria-label="New customer phone" />
        <button className="btn btn-ghost w-full" disabled={!fresh.name.trim() && !fresh.phone.trim()}>
          Use for this sale
        </button>
        <p className="text-xs text-muted">Saved with the sale. If the phone is already a customer, the sale goes to them.</p>
      </form>
    </div>
  );
}

function OpenTill({ onDone }: { onDone: () => void }) {
  const [float, setFloat] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/pos/session/open', { body: { openingFloat: toCents(float), notes: '' } });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <form onSubmit={submit} className="space-y-4">
      <p className="text-sm text-muted">Count the cash in the drawer before the first sale. At closing, you’ll count again and see if anything is missing.</p>
      <label className="block">
        <span className="mb-1.5 block text-sm font-semibold">Cash in the drawer now</span>
        <input autoFocus required className="field num font-display text-2xl font-semibold" inputMode="decimal" value={float} onChange={(e) => setFloat(e.target.value)} placeholder="0" />
      </label>
      <div className="flex flex-wrap gap-2">
        {[0, 1000, 2000, 5000].map((v) => (
          <button type="button" key={v} onClick={() => setFloat(String(v))} className={cn('rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1', float === String(v) ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white ring-line')}>
            {v === 0 ? 'Empty' : ksh(v * 100)}
          </button>
        ))}
      </div>
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || float === ''}>
        <Unlock className="h-4 w-4" /> Open the till with {ksh(toCents(float))}
      </button>
      </SheetFooter>
    </form>
  );
}

function CloseTill({ till, shift, onDone }: { till: Till; shift: Shift; onDone: () => void }) {
  const [counted, setCounted] = useState('');
  const [note, setNote] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  const expected = till.openingFloat + till.cashSales;
  const diff = toCents(counted) - expected;
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      await api('/pos/session/close', { body: { closingCount: toCents(counted), notes: note } });
      onDone();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  const row = (label: string, value: number, strong?: boolean) => (
    <div className={cn('flex justify-between py-1.5', strong && 'border-t border-line pt-2.5 font-semibold text-ink')}>
      <span>{label}</span>
      <span className="num">{ksh(value)}</span>
    </div>
  );
  return (
    <form onSubmit={submit} className="space-y-5">
      <div className="rounded-md bg-paper px-4 py-3 text-sm text-muted">
        <p className="mb-1 flex items-center gap-1.5 text-xs font-semibold tracking-wider text-faint uppercase">
          <Clock className="h-3.5 w-3.5" /> Since {timeOf(till.openedAt)}{shift ? ` · ${shift.count} sale${shift.count === 1 ? '' : 's'}` : ''}
        </p>
        {row('Float at opening', till.openingFloat)}
        {row('Cash sales', till.cashSales)}
        {row('Should be in the drawer', expected, true)}
        <p className="mt-2 text-xs">Not in the drawer: M-Pesa {ksh(till.mpesaSales)} · Card or bank {ksh(till.bankSales)}</p>
      </div>
      <label className="block">
        <span className="mb-1.5 block text-sm font-semibold">Cash you counted</span>
        <input autoFocus required className="field num font-display text-2xl font-semibold" inputMode="decimal" value={counted} onChange={(e) => setCounted(e.target.value)} placeholder="Count the notes and coins" />
      </label>
      {counted !== '' && (
        <p className={cn('rounded-md px-4 py-3 text-sm font-semibold', diff === 0 ? 'bg-good-soft text-good' : diff < 0 ? 'bg-bad-soft text-bad' : 'bg-warn-soft text-warn')}>
          {diff === 0 ? 'The drawer matches exactly.' : diff < 0 ? `The drawer is short by ${ksh(-diff)}.` : `The drawer has ${ksh(diff)} more than expected.`}
        </p>
      )}
      {counted !== '' && diff !== 0 && (
        <label className="block">
          <span className="mb-1.5 block text-sm font-semibold">What happened? (kept with the count)</span>
          <input className="field" value={note} onChange={(e) => setNote(e.target.value)} placeholder="e.g. paid the delivery rider KSh 200 from the drawer" />
        </label>
      )}
      {state.error && <ErrorNote>{state.error}</ErrorNote>}
      <SheetFooter>
      <button className="btn btn-primary" disabled={state.busy || counted === ''}>
        <Lock className="h-4 w-4" /> Close the till
      </button>
      </SheetFooter>
    </form>
  );
}

function ReceiptView({ receipt: r, cashier, onNext }: { receipt: Receipt; cashier: string; onNext: () => void }) {
  const wa = msisdn(r.customer.phone);
  const walkIn = /^walk-in customer$/i.test(r.customer.name);
  const etims = r.etims?.status === 'Offline' ? 'Sold offline: kept on this till and sent to your books and KRA eTIMS when the internet is back.' : r.etims?.status === 'Signed' ? `Signed with KRA eTIMS${r.etims.controlCode ? ` · ${r.etims.controlCode}` : ''}` : r.etims?.status === 'NotRequired' ? '' : 'Waiting for KRA eTIMS; it signs automatically once KRA replies.';
  useEffect(() => {
    const on = (e: KeyboardEvent) => e.key === 'Enter' && onNext();
    window.addEventListener('keydown', on);
    return () => window.removeEventListener('keydown', on);
  }, [onNext]);
  // Thermal printers: the paper width is a property of this till's printer, so it's remembered on the device.
  const [paper, setPaper] = useState<'58' | '80' | 'a4'>(() => {
    try { return (localStorage.getItem('kerp-receipt-paper') as '58' | '80' | 'a4') ?? '80'; } catch { return '80'; }
  });
  useEffect(() => {
    try { localStorage.setItem('kerp-receipt-paper', paper); } catch {}
    const style = document.createElement('style');
    style.textContent = paper === 'a4' ? '' : `@page { size: ${paper}mm auto; margin: 0 } @media print { .receipt-paper { width: ${paper === '58' ? 48 : 72}mm; font-size: ${paper === '58' ? 10 : 11}px; padding: 2mm 0 6mm; color: #000 } }`;
    document.head.appendChild(style);
    return () => style.remove();
  }, [paper]);
  const text = `Hello ${walkIn ? '' : r.customer.name}, thank you for shopping at ${r.company.name}. Receipt ${r.receiptNumber}: ${ksh(r.total)} paid by ${METHOD_LABEL[r.paymentMethod] ?? r.paymentMethod}.`;
  return (
    <div className="fixed inset-0 z-50 grid place-items-center bg-ink/50 p-4 print:static print:block print:bg-transparent print:p-0">
      <div className="flex max-h-[92svh] w-full max-w-sm flex-col overflow-hidden rounded-lg bg-white shadow-2xl print:max-h-none print:shadow-none">
        <div className="bg-good-soft px-5 py-4 text-center print:hidden">
          <CheckCircle2 className="mx-auto h-8 w-8 text-good" />
          <p className="mt-1 font-display text-lg font-semibold text-good">Paid · {ksh(r.total)}</p>
          {r.changeGiven > 0 && <p className="num font-display text-2xl font-semibold text-ink">Give change: {ksh(r.changeGiven)}</p>}
        </div>
        <div className="receipt-paper flex-1 overflow-y-auto px-5 py-4 font-mono text-2xs leading-relaxed print:mx-auto print:overflow-visible print:px-0">
          <div className="text-center">
            <b className="block text-sm">{r.company.name}</b>
            {r.company.kraPin && <p>PIN {r.company.kraPin}</p>}
            {r.company.phone && <p>Tel {prettyPhone(r.company.phone)}</p>}
            <p className="mt-2 font-bold">RECEIPT {r.receiptNumber}</p>
            <p>{r.date} {r.time} · {cashier || r.cashier}</p>
            {!walkIn && <p>{r.customer.name}</p>}
          </div>
          <div className="my-3 border-t border-dashed border-line" />
          {r.lines.map((l, i) => (
            <div key={i} className="mb-1">
              <p>{l.description}</p>
              <p className="flex justify-between text-muted">
                <span>{l.quantity} × {ksh(l.unitPrice)}</span>
                <span className="text-ink">{ksh(l.total)}</span>
              </p>
            </div>
          ))}
          <div className="my-3 border-t border-dashed border-line" />
          <p className="flex justify-between text-sm font-bold"><span>TOTAL</span><span>{ksh(r.total)}</span></p>
          {r.tax > 0 && <p className="flex justify-between text-muted"><span>Includes VAT</span><span>{ksh(r.tax)}</span></p>}
          <p className="flex justify-between"><span>{METHOD_LABEL[r.paymentMethod] ?? r.paymentMethod}{r.paymentMethod === 'Mpesa' && r.reference ? ` ${r.reference}` : ''}</span><span>{ksh(r.amountPaid)}</span></p>
          {r.changeGiven > 0 && <p className="flex justify-between"><span>Change</span><span>{ksh(r.changeGiven)}</span></p>}
          {etims && <p className="mt-3 text-center text-2xs text-muted">{etims}</p>}
          <p className="mt-2 text-center">Asante! Karibu tena.</p>
        </div>
        <div className="flex gap-2 border-t border-line bg-paper p-3 print:hidden">
          <div className="flex flex-1">
            <button onClick={() => window.print()} className="btn btn-ghost flex-1 rounded-r-none">
              <Printer className="h-4 w-4" /> Print
            </button>
            <select value={paper} onChange={(e) => setPaper(e.target.value as '58' | '80' | 'a4')} className="rounded-r-md bg-white px-1 text-xs ring-1 ring-line" aria-label="Receipt paper" title="Receipt paper width">
              <option value="80">80 mm</option>
              <option value="58">58 mm</option>
              <option value="a4">A4</option>
            </select>
          </div>
          {wa && (
            <a href={`https://wa.me/${wa}?text=${encodeURIComponent(text)}`} target="_blank" rel="noreferrer" className="btn btn-mpesa flex-1">
              <Share2 className="h-4 w-4" /> WhatsApp
            </a>
          )}
          <button autoFocus onClick={onNext} className="btn btn-primary flex-1">
            Next sale
          </button>
        </div>
      </div>
    </div>
  );
}
