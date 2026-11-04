// The business's shop and customer portal on the internet (/s/:slug). A customer is deciding one of three things:
// "can they supply what I need?" (browse and ask), "what do I owe, and can I pay now?" (sign in, pay by M-Pesa),
// and "can I order again without calling?" (pick products, send). So the page leads with the products and an
// Ask button, and signing in needs only a phone number and a code: no password to forget.
import { useEffect, useMemo, useState } from 'react';
import { useParams, useSearchParams } from 'react-router';
import { 
  Check, 
  ChevronRight, 
  HelpCircle, 
  Loader2, 
  LogOut, 
  MapPin, 
  MessageCircle, 
  Minus, 
  Package, 
  Phone, 
  Plus, 
  Search, 
  ShieldCheck, 
  ShoppingBag, 
  ShoppingCart, 
  Smartphone, 
  Sparkles, 
  Trash2, 
  Truck, 
  User, 
  X 
} from 'lucide-react';
import { cn, day, ksh, prettyPhone } from '../lib/format';

type Item = { 
  id: string; 
  name: string; 
  unit: string; 
  price: number; 
  kind: string; 
  available: boolean; 
  units: { name: string; factor: number }[];
  brand?: string;
};

const TOKEN = (slug: string) => `kerp-portal-${slug}`;

function usePublic(slug: string) {
  const [token, setTokenState] = useState<string | null>(() => { try { return localStorage.getItem(TOKEN(slug)); } catch { return null; } });
  const setToken = (t: string | null) => { try { t ? localStorage.setItem(TOKEN(slug), t) : localStorage.removeItem(TOKEN(slug)); } catch {} setTokenState(t); };
  const call = async <T,>(path: string, opts: { method?: string; body?: unknown } = {}): Promise<T> => {
    const res = await fetch(`/api/public/${slug}${path}`, {
      method: opts.method ?? (opts.body !== undefined ? 'POST' : 'GET'),
      headers: { ...(opts.body !== undefined ? { 'Content-Type': 'application/json' } : {}), ...(token ? { 'X-Portal-Token': token } : {}) },
      body: opts.body !== undefined ? JSON.stringify(opts.body) : undefined,
    });
    const data = await res.json().catch(() => ({}));
    if (res.status === 401 && token) setToken(null);
    if (!res.ok) throw new Error(data.error ?? 'Something went wrong. Try again.');
    return data as T;
  };
  return { token, setToken, call };
}

export default function Shop() {
  const { slug = '' } = useParams();
  const [params] = useSearchParams();
  const ref = params.get('ref') ?? (document.cookie.match(/(?:^|; )kref=([^;]+)/)?.[1] ?? null);
  const { token, setToken, call } = usePublic(slug);
  const [shop, setShop] = useState<any>(null);
  const [error, setError] = useState('');
  const [view, setView] = useState<'shop' | 'account' | 'signin'>('shop');
  const [asking, setAsking] = useState<Item | 'general' | null>(params.get('ask') ? 'general' : null);
  const [cart, setCart] = useState<Record<string, number>>({});
  const [isCheckoutOpen, setIsCheckoutOpen] = useState(false);
  const [placedOrder, setPlacedOrder] = useState<any>(null);
  const [q, setQ] = useState('');
  const [kindFilter, setKindFilter] = useState<'all' | 'Product' | 'Service'>('all');

  const load = () => call<any>('').then(setShop).catch((e) => setError(e.message));
  useEffect(() => { load(); }, [token]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (shop?.business?.name) document.title = `${shop.business.name} | Online Store`; }, [shop]);
  useEffect(() => {
    const item = params.get('item');
    if (item && shop) { const it = shop.items.find((i: Item) => i.id === item); if (it) setAsking(it); }
  }, [shop]); // eslint-disable-line react-hooks/exhaustive-deps

  const items: Item[] = shop?.items ?? [];
  const shown = useMemo(() => {
    return items.filter((i: Item) => {
      const matchesQ = !q.trim() || i.name.toLowerCase().includes(q.trim().toLowerCase());
      const matchesKind = kindFilter === 'all' || i.kind === kindFilter;
      return matchesQ && matchesKind;
    });
  }, [items, q, kindFilter]);

  const cartLines = Object.entries(cart).filter(([, n]) => n > 0);
  const cartItemCount = cartLines.reduce((s, [, n]) => s + n, 0);
  const cartTotal = cartLines.reduce((s, [id, n]) => s + n * (items.find((i) => i.id === id)?.price ?? 0), 0);

  const updateCartQty = (id: string, delta: number) => {
    setCart((prev) => {
      const next = Math.max(0, (prev[id] ?? 0) + delta);
      if (next === 0) {
        const copy = { ...prev };
        delete copy[id];
        return copy;
      }
      return { ...prev, [id]: next };
    });
  };

  const setCartExact = (id: string, qty: number) => {
    setCart((prev) => {
      const next = Math.max(0, qty);
      if (next === 0) {
        const copy = { ...prev };
        delete copy[id];
        return copy;
      }
      return { ...prev, [id]: next };
    });
  };

  if (error) {
    return (
      <div className="mx-auto flex min-h-svh max-w-md flex-col items-center justify-center p-6 text-center">
        <div className="mb-4 rounded-full bg-bad-soft p-4 text-bad">
          <HelpCircle className="h-8 w-8" />
        </div>
        <h1 className="mb-2 text-xl font-bold">Shop Unavailable</h1>
        <p className="mb-6 text-sm text-muted">{error}</p>
        <button className="btn btn-ghost" onClick={() => window.location.reload()}>Retry</button>
      </div>
    );
  }

  if (!shop) {
    return (
      <div className="grid min-h-svh place-items-center bg-paper">
        <div className="flex flex-col items-center gap-3">
          <Loader2 className="h-8 w-8 animate-spin text-brand" />
          <p className="text-sm font-semibold text-muted">Opening shop...</p>
        </div>
      </div>
    );
  }

  const b = shop.business;
  const whatsappUrl = b.phone 
    ? `https://wa.me/254${b.phone.replace(/^0/, '')}?text=${encodeURIComponent(`Hello ${b.name}, I'm browsing your online store.`)}`
    : null;

  return (
    <div className="min-h-svh bg-paper pb-24 text-ink">
      {/* Top Banner & Header */}
      <header className="sticky top-0 z-30 border-b border-line bg-white/95 backdrop-blur">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-4 py-3 sm:px-6">
          <button onClick={() => setView('shop')} className="group flex min-w-0 items-center gap-3 text-left">
            <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-brand text-white font-bold shadow-sm group-hover:scale-105 transition-transform">
              {b.name.charAt(0)}
            </div>
            <div className="min-w-0">
              <b className="block truncate font-display text-base font-semibold sm:text-lg">{b.name}</b>
              <div className="flex items-center gap-2 text-xs text-muted">
                {b.county && <span className="flex items-center gap-0.5"><MapPin className="h-3 w-3" />{b.county}</span>}
                {b.paybill && <span className="rounded bg-brand-soft px-1.5 py-0.5 text-2xs font-semibold text-brand">Paybill {b.paybill}</span>}
              </div>
            </div>
          </button>

          <div className="flex items-center gap-2">
            {whatsappUrl && (
              <a 
                href={whatsappUrl} 
                target="_blank" 
                rel="noreferrer" 
                className="hidden sm:inline-flex items-center gap-1.5 rounded-md bg-[#25D366]/10 px-3 py-2 text-xs font-semibold text-[#128C7E] hover:bg-[#25D366]/20 transition"
                title="Chat on WhatsApp"
              >
                <MessageCircle className="h-3.5 w-3.5" /> WhatsApp
              </a>
            )}

            <button className="btn btn-ghost py-2 text-xs sm:text-sm" onClick={() => setAsking('general')}>
              <HelpCircle className="h-4 w-4" /> <span className="hidden sm:inline">Ask question</span>
            </button>

            {cartItemCount > 0 && view === 'shop' && (
              <button 
                onClick={() => setIsCheckoutOpen(true)}
                className="relative inline-flex items-center gap-1.5 rounded-md bg-brand px-3 py-2 text-xs font-semibold text-white shadow-sm hover:bg-brand-hover transition sm:text-sm"
              >
                <ShoppingCart className="h-4 w-4" />
                <span className="font-bold">{cartItemCount}</span>
                <span className="hidden md:inline">· {ksh(cartTotal)}</span>
              </button>
            )}

            {token ? (
              <button className="btn btn-ghost py-2 text-xs sm:text-sm" onClick={() => setView('account')}>
                <User className="h-4 w-4 text-brand" /> <span className="hidden sm:inline">Account</span>
              </button>
            ) : (
              <button className="btn btn-ghost py-2 text-xs sm:text-sm" onClick={() => setView('signin')}>
                <User className="h-4 w-4" /> <span className="hidden sm:inline">Sign in</span>
              </button>
            )}
          </div>
        </div>
      </header>

      {/* Main Body */}
      <main className="mx-auto max-w-6xl px-4 py-6 sm:px-6">
        {view === 'signin' && (
          <SignIn 
            call={call} 
            ref_={ref} 
            onDone={(t) => { setToken(t); setView('account'); }} 
            onBack={() => setView('shop')}
          />
        )}

        {view === 'account' && token && (
          <Account 
            call={call} 
            paybill={b.paybill} 
            onSignOut={() => { setToken(null); setView('shop'); }} 
            onBackToShop={() => setView('shop')}
          />
        )}

        {view === 'shop' && (
          <>
            {/* Storefront Hero Pitch */}
            <div className="relative mb-8 overflow-hidden rounded-2xl bg-gradient-to-r from-ink via-indigo to-brand p-6 text-white shadow-md sm:p-8">
              <div className="relative z-10 max-w-xl">
                <span className="mb-2 inline-flex items-center gap-1.5 rounded-full bg-white/15 px-3 py-1 text-xs font-semibold text-white backdrop-blur">
                  <Sparkles className="h-3.5 w-3.5 text-gold" /> Small Business Online Store
                </span>
                <h1 className="font-display text-2xl font-semibold tracking-tight sm:text-3xl">
                  {b.name}
                </h1>
                <p className="mt-2 text-sm text-white/80 sm:text-base leading-relaxed">
                  Browse verified stock, order directly with M-Pesa or cash on delivery, and follow up fast with our dispatch team.
                </p>

                <div className="mt-4 flex flex-wrap gap-4 text-xs text-white/90">
                  <span className="flex items-center gap-1.5">
                    <Truck className="h-4 w-4 text-gold" /> Same-day delivery available
                  </span>
                  <span className="flex items-center gap-1.5">
                    <ShieldCheck className="h-4 w-4 text-good" /> Official business guarantee
                  </span>
                  {b.phone && (
                    <span className="flex items-center gap-1.5">
                      <Phone className="h-4 w-4" /> {prettyPhone(b.phone)}
                    </span>
                  )}
                </div>
              </div>
            </div>

            {/* Filter & Search Bar */}
            <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-center sm:justify-between">
              <div className="flex flex-wrap items-center gap-2">
                <button 
                  onClick={() => setKindFilter('all')}
                  className={cn(
                    'rounded-lg px-3.5 py-1.5 text-xs font-semibold transition',
                    kindFilter === 'all' ? 'bg-ink text-white shadow-sm' : 'bg-white text-muted ring-1 ring-line hover:bg-paper'
                  )}
                >
                  All Items ({items.length})
                </button>
                <button 
                  onClick={() => setKindFilter('Product')}
                  className={cn(
                    'rounded-lg px-3.5 py-1.5 text-xs font-semibold transition',
                    kindFilter === 'Product' ? 'bg-ink text-white shadow-sm' : 'bg-white text-muted ring-1 ring-line hover:bg-paper'
                  )}
                >
                  Products ({items.filter(i => i.kind === 'Product').length})
                </button>
                {items.some(i => i.kind === 'Service') && (
                  <button 
                    onClick={() => setKindFilter('Service')}
                    className={cn(
                      'rounded-lg px-3.5 py-1.5 text-xs font-semibold transition',
                      kindFilter === 'Service' ? 'bg-ink text-white shadow-sm' : 'bg-white text-muted ring-1 ring-line hover:bg-paper'
                    )}
                  >
                    Services
                  </button>
                )}
              </div>

              <div className="relative w-full sm:w-72">
                <Search className="pointer-events-none absolute top-2.5 left-3 h-4 w-4 text-faint" />
                <input 
                  className="field pl-9 text-sm" 
                  value={q} 
                  onChange={(e) => setQ(e.target.value)} 
                  placeholder="Search catalog by name..." 
                />
              </div>
            </div>

            {/* Personalized customer alert if signed in */}
            {shop.signedIn && (
              <div className="mb-4 flex items-center justify-between rounded-lg bg-brand-soft px-4 py-2.5 text-xs text-brand sm:text-sm">
                <span>Welcome back, <b>{shop.customer.name}</b>! Showing your custom prices.</span>
                <button onClick={() => setView('account')} className="font-semibold underline">View Account</button>
              </div>
            )}

            {/* Product Grid */}
            {shown.length === 0 ? (
              <div className="card p-12 text-center">
                <Package className="mx-auto h-10 w-10 text-faint" />
                <h3 className="mt-3 text-base font-bold">No products found</h3>
                <p className="mt-1 text-sm text-muted">Try adjusting your search terms or filter.</p>
                <button className="btn btn-ghost mt-4" onClick={() => { setQ(''); setKindFilter('all'); }}>Clear search</button>
              </div>
            ) : (
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {shown.map((i: Item) => {
                  const inCart = cart[i.id] ?? 0;
                  return (
                    <div 
                      key={i.id} 
                      className="card group flex flex-col overflow-hidden transition-all hover:shadow-md hover:ring-brand/30"
                    >
                      {/* Product Visual Header Card */}
                      <div className="relative flex h-32 w-full items-center justify-center bg-gradient-to-br from-paper to-line/60 p-4">
                        <div className="flex h-14 w-14 items-center justify-center rounded-2xl bg-white shadow-sm ring-1 ring-line/50 group-hover:scale-110 transition-transform">
                          <Package className="h-7 w-7 text-brand" />
                        </div>
                        <span className={cn(
                          'absolute top-3 right-3 rounded-full px-2 py-0.5 text-2xs font-bold uppercase tracking-wider',
                          i.available ? 'bg-good-soft text-good' : 'bg-warn-soft text-warn'
                        )}>
                          {i.available ? 'In Stock' : 'Order On Request'}
                        </span>
                        {i.brand && (
                          <span className="absolute top-3 left-3 rounded-md bg-white/90 px-2 py-0.5 text-2xs font-semibold text-muted backdrop-blur shadow-2xs">
                            {i.brand}
                          </span>
                        )}
                      </div>

                      {/* Product Info */}
                      <div className="flex flex-1 flex-col p-4">
                        <b className="text-base font-semibold text-ink line-clamp-2 leading-snug group-hover:text-brand transition-colors">
                          {i.name}
                        </b>

                        <div className="mt-2 mb-3">
                          <div className="flex items-baseline gap-1.5">
                            <span className="font-display text-xl font-semibold text-ink num">
                              {ksh(i.price)}
                            </span>
                            <span className="text-xs text-muted">/ {i.unit}</span>
                          </div>
                          {i.units && i.units.length > 0 && (
                            <p className="mt-0.5 text-2xs text-muted">
                              Also available: {i.units.map(u => `${u.name} (×${u.factor})`).join(', ')}
                            </p>
                          )}
                        </div>

                        {/* Interactive E-commerce Actions */}
                        <div className="mt-auto flex items-center justify-between gap-2 pt-2 border-t border-line/60">
                          {inCart > 0 ? (
                            <div className="flex items-center gap-1.5 rounded-lg border border-line bg-paper p-1">
                              <button 
                                className="flex h-8 w-8 items-center justify-center rounded-md bg-white shadow-2xs hover:bg-paper text-ink transition active:scale-95" 
                                aria-label="Decrease quantity" 
                                onClick={() => updateCartQty(i.id, -1)}
                              >
                                {inCart === 1 ? <Trash2 className="h-3.5 w-3.5 text-bad" /> : <Minus className="h-3.5 w-3.5" />}
                              </button>
                              <input 
                                className="w-10 bg-transparent text-center font-bold text-sm num outline-none" 
                                inputMode="numeric" 
                                value={inCart} 
                                onChange={(e) => setCartExact(i.id, Number(e.target.value.replace(/\D/g, '')) || 0)} 
                                aria-label={`${i.name} quantity`} 
                              />
                              <button 
                                className="flex h-8 w-8 items-center justify-center rounded-md bg-white shadow-2xs hover:bg-paper text-ink transition active:scale-95" 
                                aria-label="Increase quantity" 
                                onClick={() => updateCartQty(i.id, 1)}
                              >
                                <Plus className="h-3.5 w-3.5 text-good" />
                              </button>
                            </div>
                          ) : (
                            <button 
                              className="btn btn-primary flex-1 py-2 text-xs font-bold shadow-xs active:scale-98" 
                              onClick={() => {
                                updateCartQty(i.id, 1);
                              }}
                            >
                              <Plus className="h-4 w-4" /> Add to cart
                            </button>
                          )}

                          <button 
                            className="btn btn-ghost py-2 px-3 text-xs text-muted hover:text-ink" 
                            title="Ask question about this product"
                            onClick={() => setAsking(i)}
                          >
                            <MessageCircle className="h-4 w-4" />
                          </button>
                        </div>
                      </div>
                    </div>
                  );
                })}
              </div>
            )}

            {/* Bottom Footer Callout for Returning Customers */}
            {!shop.signedIn && (
              <div className="mt-12 rounded-xl border border-line bg-white p-6 text-center shadow-xs">
                <h4 className="font-semibold text-sm">Have an active account with {b.name}?</h4>
                <p className="mt-1 text-xs text-muted">
                  Sign in with your mobile phone number to view invoices, check running balance, or pay bills with M-Pesa.
                </p>
                <button 
                  className="btn btn-ghost mt-3 py-2 text-xs font-semibold text-brand" 
                  onClick={() => setView('signin')}
                >
                  Sign in with phone
                </button>
              </div>
            )}
          </>
        )}
      </main>

      {/* Floating Bottom Cart Bar (Sticky on Mobile & Desktop) */}
      {view === 'shop' && cartItemCount > 0 && (
        <aside aria-label="Shopping Cart Summary" className="fixed bottom-3 inset-x-0 z-20 px-4">
          <div className="mx-auto flex max-w-2xl items-center justify-between gap-4 rounded-2xl bg-ink p-3.5 text-white shadow-2xl ring-1 ring-white/10 backdrop-blur-md">
            <div className="flex items-center gap-3">
              <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand text-white font-bold">
                <ShoppingBag className="h-5 w-5" />
              </div>
              <div>
                <p className="text-xs text-white/70">
                  <b>{cartItemCount} item{cartItemCount === 1 ? '' : 's'}</b> in your bag
                </p>
                <p className="font-display text-lg font-semibold num text-white">
                  {ksh(cartTotal)}
                </p>
              </div>
            </div>

            <button 
              className="inline-flex items-center gap-2 rounded-xl bg-brand px-5 py-2.5 text-sm font-bold text-white shadow-md hover:bg-brand-hover transition active:scale-95"
              onClick={() => setIsCheckoutOpen(true)}
            >
              Checkout <ChevronRight className="h-4 w-4" />
            </button>
          </div>
        </aside>
      )}

      {/* Checkout Drawer / Modal */}
      {isCheckoutOpen && (
        <CheckoutDrawer 
          call={call}
          cart={cart}
          items={items}
          total={cartTotal}
          business={b}
          ref_={ref}
          signedIn={shop.signedIn}
          customer={shop.customer}
          updateCartQty={updateCartQty}
          onClose={() => setIsCheckoutOpen(false)}
          onSuccess={(result) => {
            if (result.token) setToken(result.token);
            setCart({});
            setIsCheckoutOpen(false);
            setPlacedOrder(result);
          }}
        />
      )}

      {/* Order Success Screen */}
      {placedOrder && (
        <OrderSuccessModal 
          order={placedOrder}
          business={b}
          onClose={() => setPlacedOrder(null)}
          onViewAccount={() => {
            setPlacedOrder(null);
            setView('account');
          }}
        />
      )}

      {/* Ask Question Modal */}
      {asking && (
        <Ask 
          call={call} 
          item={asking === 'general' ? null : asking} 
          ref_={ref} 
          signedIn={shop.signedIn} 
          onClose={() => setAsking(null)} 
        />
      )}
    </div>
  );
}

function Panel({ title, children, onClose }: { title: string; children: React.ReactNode; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 grid items-end bg-ink/60 backdrop-blur-xs sm:place-items-center" onClick={onClose}>
      <div 
        className="max-h-[92svh] w-full overflow-y-auto rounded-t-2xl bg-white p-5 shadow-2xl sm:max-w-md sm:rounded-2xl" 
        onClick={(e) => e.stopPropagation()} 
        role="dialog" 
        aria-label={title}
      >
        <div className="mb-4 flex items-center justify-between border-b border-line pb-3">
          <h2 className="font-display text-lg font-semibold text-ink">{title}</h2>
          <button className="rounded-full p-1 text-muted hover:bg-paper hover:text-ink transition" onClick={onClose}>
            <X className="h-5 w-5" />
          </button>
        </div>
        {children}
      </div>
    </div>
  );
}

function CheckoutDrawer({
  call,
  cart,
  items,
  total,
  business,
  ref_,
  signedIn,
  customer,
  updateCartQty,
  onClose,
  onSuccess
}: {
  call: any;
  cart: Record<string, number>;
  items: Item[];
  total: number;
  business: any;
  ref_: string | null;
  signedIn: boolean;
  customer: any;
  updateCartQty: (id: string, delta: number) => void;
  onClose: () => void;
  onSuccess: (result: any) => void;
}) {
  const [name, setName] = useState(customer?.name ?? '');
  const [phone, setPhone] = useState(customer?.phone ?? '');
  const [deliveryAddress, setDeliveryAddress] = useState('');
  const [note, setNote] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });

  const lines = Object.entries(cart).filter(([, n]) => n > 0);

  const placeOrder = async () => {
    setState({ busy: true, error: '' });
    try {
      const orderLines = lines.map(([itemId, quantity]) => ({ itemId, quantity }));
      const payload: any = {
        lines: orderLines,
        note: note.trim() || undefined,
        deliveryAddress: deliveryAddress.trim() || undefined,
        ref: ref_ || undefined,
      };

      if (!signedIn) {
        payload.name = name.trim();
        payload.phone = phone.trim();
      }

      const res = await call('/orders', { body: payload });
      onSuccess({
        ...res,
        total,
        lines: lines.map(([id, qty]) => ({
          name: items.find(i => i.id === id)?.name ?? 'Product',
          qty,
          price: (items.find(i => i.id === id)?.price ?? 0) * qty,
        })),
        deliveryAddress,
      });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex justify-end bg-ink/60 backdrop-blur-xs" onClick={onClose}>
      <div 
        className="flex h-full w-full max-w-lg flex-col bg-white shadow-2xl transition-all"
        onClick={(e) => e.stopPropagation()}
      >
        {/* Drawer Header */}
        <div className="flex items-center justify-between border-b border-line px-5 py-4">
          <div className="flex items-center gap-2">
            <ShoppingBag className="h-5 w-5 text-brand" />
            <h2 className="font-display text-lg font-semibold">Shopping Cart ({lines.length})</h2>
          </div>
          <button className="rounded-full p-1.5 text-muted hover:bg-paper hover:text-ink" onClick={onClose}>
            <X className="h-5 w-5" />
          </button>
        </div>

        {/* Drawer Content */}
        <div className="flex-1 overflow-y-auto p-5 space-y-6">
          {/* Cart Item List */}
          <div>
            <h3 className="mb-2 text-xs font-bold uppercase tracking-wider text-muted">Selected Items</h3>
            <ul className="divide-y divide-line rounded-xl border border-line bg-paper/50">
              {lines.map(([id, qty]) => {
                const item = items.find((i) => i.id === id);
                if (!item) return null;
                return (
                  <li key={id} className="flex items-center justify-between gap-3 p-3 text-sm">
                    <div className="min-w-0 flex-1">
                      <b className="block truncate font-semibold">{item.name}</b>
                      <span className="text-xs text-muted num">{ksh(item.price)} each</span>
                    </div>

                    <div className="flex items-center gap-2">
                      <div className="flex items-center gap-1 rounded-md border border-line bg-white p-0.5">
                        <button 
                          className="p-1 hover:bg-paper rounded text-muted hover:text-ink"
                          onClick={() => updateCartQty(id, -1)}
                        >
                          <Minus className="h-3 w-3" />
                        </button>
                        <span className="w-6 text-center font-bold text-xs num">{qty}</span>
                        <button 
                          className="p-1 hover:bg-paper rounded text-muted hover:text-ink"
                          onClick={() => updateCartQty(id, 1)}
                        >
                          <Plus className="h-3 w-3" />
                        </button>
                      </div>
                      <span className="font-bold num text-ink w-20 text-right">
                        {ksh(item.price * qty)}
                      </span>
                    </div>
                  </li>
                );
              })}
            </ul>
          </div>

          {/* Delivery & Customer Info */}
          <div className="space-y-3">
            <h3 className="text-xs font-bold uppercase tracking-wider text-muted">Customer & Delivery Info</h3>
            
            {!signedIn ? (
              <>
                <div>
                  <label className="block text-xs font-semibold text-ink mb-1">Your Name or Business *</label>
                  <input 
                    className="field text-sm" 
                    placeholder="e.g. Grace Wambui / Mama Joy Duka" 
                    value={name} 
                    onChange={(e) => setName(e.target.value)} 
                  />
                </div>
                <div>
                  <label className="block text-xs font-semibold text-ink mb-1">M-Pesa Mobile Number *</label>
                  <input 
                    className="field text-sm" 
                    inputMode="tel" 
                    placeholder="e.g. 0712 345 678" 
                    value={phone} 
                    onChange={(e) => setPhone(e.target.value)} 
                  />
                  <p className="mt-1 text-2xs text-muted">Used for delivery updates and M-Pesa payment confirmation.</p>
                </div>
              </>
            ) : (
              <div className="rounded-lg bg-brand-soft p-3 text-xs text-brand">
                Ordering as <b>{customer.name}</b> ({prettyPhone(customer.phone)}).
              </div>
            )}

            <div>
              <label className="block text-xs font-semibold text-ink mb-1">Delivery Address / Pickup Point</label>
              <input 
                className="field text-sm" 
                placeholder="e.g. Ngara Market Stall 42, Nairobi" 
                value={deliveryAddress} 
                onChange={(e) => setDeliveryAddress(e.target.value)} 
              />
            </div>

            <div>
              <label className="block text-xs font-semibold text-ink mb-1">Order Notes / Delivery Time</label>
              <textarea 
                className="field min-h-16 text-sm" 
                placeholder="Any special requests or instructions..." 
                value={note} 
                onChange={(e) => setNote(e.target.value)} 
              />
            </div>
          </div>

          {/* Order Summary Box */}
          <div className="rounded-xl border border-line bg-paper p-4 space-y-2">
            <div className="flex justify-between text-xs text-muted">
              <span>Subtotal</span>
              <span className="num font-semibold text-ink">{ksh(total)}</span>
            </div>
            <div className="flex justify-between text-xs text-muted">
              <span>Estimated Delivery</span>
              <span className="font-semibold text-good">Standard Carrier / Collect</span>
            </div>
            {business.paybill && (
              <div className="flex justify-between text-xs text-muted">
                <span>Payment Method</span>
                <span className="font-semibold text-brand">M-Pesa (Paybill {business.paybill})</span>
              </div>
            )}
            <div className="border-t border-line pt-2 flex justify-between font-bold text-base">
              <span>Total Amount</span>
              <span className="num text-brand font-display text-lg">{ksh(total)}</span>
            </div>
          </div>

          {state.error && (
            <div className="rounded-lg bg-bad-soft p-3 text-xs font-semibold text-bad">
              {state.error}
            </div>
          )}
        </div>

        {/* Drawer Footer CTA */}
        <div className="border-t border-line p-4 bg-white">
          <button 
            className="btn btn-primary w-full py-3.5 text-base font-bold shadow-md active:scale-98"
            disabled={state.busy || lines.length === 0 || (!signedIn && (name.trim().length < 2 || phone.replace(/\D/g, '').length < 9))}
            onClick={placeOrder}
          >
            {state.busy ? <Loader2 className="h-5 w-5 animate-spin" /> : <Check className="h-5 w-5" />}
            Confirm & Place Order ({ksh(total)})
          </button>
        </div>
      </div>
    </div>
  );
}

function OrderSuccessModal({ order, business, onClose, onViewAccount }: { order: any; business: any; onClose: () => void; onViewAccount: () => void }) {
  const quote = order.quote;
  const inquiry = order.inquiry;

  // Build preformatted WhatsApp message
  const summaryLines = (order.lines ?? []).map((l: any) => `• ${l.name} × ${l.qty}`).join('\n');
  const waText = encodeURIComponent(
    `Hello ${business.name}! I just placed order #${quote?.number ?? 'NEW'} on your online store.\n\n` +
    `Items:\n${summaryLines}\n\n` +
    `Total: KSh ${(order.total / 100).toLocaleString('en-KE')}\n` +
    (order.deliveryAddress ? `Delivery to: ${order.deliveryAddress}\n` : '') +
    `Please confirm receipt and dispatch details.`
  );
  const waLink = business.phone ? `https://wa.me/254${business.phone.replace(/^0/, '')}?text=${waText}` : null;

  return (
    <Panel title="Order Placed Successfully! 🎉" onClose={onClose}>
      <div className="space-y-4 text-center">
        <div className="mx-auto flex h-14 w-14 items-center justify-center rounded-full bg-good-soft text-good">
          <Check className="h-8 w-8" />
        </div>

        <div>
          <h3 className="font-display text-xl font-semibold text-ink">We have received your order!</h3>
          <p className="mt-1 text-xs text-muted">
            Order Reference: <b className="text-ink font-mono">{quote?.number ?? inquiry?.number}</b>
          </p>
        </div>

        <div className="rounded-xl border border-line bg-paper p-4 text-left text-xs space-y-2">
          <div className="flex justify-between">
            <span className="text-muted">Total Order Value:</span>
            <b className="text-sm font-bold num text-brand">{ksh(order.total)}</b>
          </div>
          {business.paybill ? (
            <div className="mt-2 border-t border-line/60 pt-2">
              <span className="font-semibold text-ink block">M-Pesa Payment Instructions:</span>
              <p className="mt-0.5 text-muted">
                Go to M-Pesa &gt; Lipa na M-Pesa &gt; Paybill &gt; Business No: <b className="text-ink">{business.paybill}</b>, Account: <b className="text-ink">{quote?.number ?? 'Your Phone'}</b>
              </p>
            </div>
          ) : (
            <p className="text-muted">The shop dispatch team will call your phone to verify delivery.</p>
          )}
        </div>

        {waLink && (
          <a 
            href={waLink} 
            target="_blank" 
            rel="noreferrer" 
            className="flex items-center justify-center gap-2 rounded-xl bg-[#25D366] px-4 py-3 text-sm font-bold text-white shadow-sm hover:brightness-105 transition"
          >
            <MessageCircle className="h-4 w-4" /> Message Shop on WhatsApp
          </a>
        )}

        <div className="flex gap-2 pt-2">
          <button className="btn btn-ghost flex-1 py-2.5 text-xs font-semibold" onClick={onClose}>
            Continue Shopping
          </button>
          <button className="btn btn-primary flex-1 py-2.5 text-xs font-semibold" onClick={onViewAccount}>
            View Account & Orders
          </button>
        </div>
      </div>
    </Panel>
  );
}

function Ask({ call, item, ref_, signedIn, onClose }: { call: any; item: Item | null; ref_: string | null; signedIn: boolean; onClose: () => void }) {
  const [f, setF] = useState({ name: '', phone: '', message: item ? `I'd like to know about ${item.name}: ` : '' });
  const [state, setState] = useState({ busy: false, error: '', done: false });
  const send = async () => {
    setState({ busy: true, error: '', done: false });
    try {
      await call('/inquiries', { body: { ...f, itemId: item?.id, ref: ref_ } });
      setState({ busy: false, error: '', done: true });
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, done: false });
    }
  };
  return (
    <Panel title={item ? `Ask about ${item.name}` : 'Ask a question'} onClose={onClose}>
      {state.done ? (
        <div className="space-y-3 text-center">
          <Check className="mx-auto h-8 w-8 text-good" />
          <p className="font-semibold text-sm">Sent! The shop will respond directly to your phone.</p>
          <button className="btn btn-primary w-full" onClick={onClose}>Done</button>
        </div>
      ) : (
        <div className="space-y-3">
          {!signedIn && (
            <>
              <input className="field text-sm" placeholder="Your name or business" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} />
              <input className="field text-sm" inputMode="tel" placeholder="Phone, e.g. 0712 345 678" value={f.phone} onChange={(e) => setF({ ...f, phone: e.target.value })} />
            </>
          )}
          <textarea className="field min-h-28 text-sm" placeholder="What would you like to know? Quantities, delivery, prices…" value={f.message} onChange={(e) => setF({ ...f, message: e.target.value })} />
          {state.error && <p className="text-sm text-bad">{state.error}</p>}
          <button className="btn btn-primary w-full py-3" disabled={state.busy || f.message.trim().length < 3 || (!signedIn && f.phone.replace(/\D/g, '').length < 9)} onClick={send}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Send question
          </button>
          <p className="text-xs text-muted">Your phone number is used only to answer you.</p>
        </div>
      )}
    </Panel>
  );
}

function SignIn({ call, ref_, onDone, onBack }: { call: any; ref_: string | null; onDone: (token: string) => void; onBack: () => void }) {
  const [phone, setPhone] = useState('');
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [sent, setSent] = useState<{ devCode?: string } | null>(null);
  const [needName, setNeedName] = useState(false);
  const [state, setState] = useState({ busy: false, error: '' });
  const ask = async () => {
    setState({ busy: true, error: '' });
    try { setSent(await call('/auth/code', { body: { phone } })); setState({ busy: false, error: '' }); } catch (e) { setState({ busy: false, error: (e as Error).message }); }
  };
  const verify = async () => {
    setState({ busy: true, error: '' });
    try {
      const r = await call('/auth/verify', { body: { phone, code, name: name || undefined, ref: ref_ } });
      onDone(r.token);
    } catch (e) {
      const m = (e as Error).message;
      if (/your name/i.test(m)) setNeedName(true);
      setState({ busy: false, error: m });
    }
  };
  return (
    <section className="card mx-auto max-w-sm space-y-4 p-6 shadow-sm">
      <div className="flex items-center justify-between">
        <h1 className="text-lg font-bold">Sign in with phone</h1>
        <button className="text-xs text-muted hover:underline" onClick={onBack}>Back to shop</button>
      </div>
      {!sent ? (
        <>
          <p className="text-xs text-muted">We’ll send a verification code to your phone. No password needed.</p>
          <input className="field text-sm" inputMode="tel" autoComplete="tel" placeholder="0712 345 678" value={phone} onChange={(e) => setPhone(e.target.value)} />
          {state.error && <p className="text-xs text-bad">{state.error}</p>}
          <button className="btn btn-primary w-full py-3" disabled={state.busy || phone.replace(/\D/g, '').length < 9} onClick={ask}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Send sign-in code
          </button>
        </>
      ) : (
        <>
          <p className="text-xs text-muted">Enter the 6-digit code sent to {phone}.{sent.devCode && <b className="block text-ink mt-1 font-mono">Test server: your code is {sent.devCode}</b>}</p>
          <input className="field num text-center text-lg tracking-[0.4em]" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))} />
          {needName && <input className="field text-sm" placeholder="Your name or business (new account)" value={name} onChange={(e) => setName(e.target.value)} />}
          {state.error && <p className="text-xs text-bad">{state.error}</p>}
          <button className="btn btn-primary w-full py-3" disabled={state.busy || code.length !== 6 || (needName && name.trim().length < 2)} onClick={verify}>
            {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Verify & Sign in
          </button>
          <button className="w-full text-xs text-muted underline" onClick={() => { setSent(null); setCode(''); }}>Use another phone number</button>
        </>
      )}
    </section>
  );
}

function Account({ call, paybill, onSignOut, onBackToShop }: { call: any; paybill: string; onSignOut: () => void; onBackToShop: () => void }) {
  const [me, setMe] = useState<any>(null);
  const [error, setError] = useState('');
  const [tab, setTab] = useState<'owe' | 'questions' | 'details'>('owe');
  const [paying, setPaying] = useState<string | null>(null);
  const [notice, setNotice] = useState('');
  const load = () => call('/me').then(setMe).catch((e: Error) => setError(e.message));
  useEffect(() => { load(); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  if (error) return <p className="text-sm text-bad p-4">{error}</p>;
  if (!me) return <div className="p-8 text-center"><Loader2 className="mx-auto h-6 w-6 animate-spin text-brand" /></div>;
  const pay = async (id: string) => {
    setPaying(id);
    setNotice('');
    try { await call(`/invoices/${id}/pay`, { body: {} }); setNotice(`Check ${prettyPhone(me.customer.phone)} for the M-Pesa prompt and enter your PIN.`); } catch (e) { setNotice((e as Error).message); }
    setPaying(null);
  };
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0 flex-1">
          <button onClick={onBackToShop} className="text-xs text-brand font-semibold hover:underline mb-1 inline-block">← Back to shopping</button>
          <h1 className="text-xl font-bold">Hello, {me.customer.name}</h1>
          <p className="text-xs text-muted">{me.customer.number} · {prettyPhone(me.customer.phone)}</p>
        </div>
        <button className="btn btn-ghost py-2 text-xs" onClick={onSignOut}><LogOut className="h-4 w-4" /> Sign out</button>
      </div>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted uppercase">Outstanding Balance</p>
          <p className="num text-2xl font-bold">{ksh(me.owes)}</p>
          {me.overdue > 0 && <p className="text-xs font-semibold text-bad">{ksh(me.overdue)} is overdue</p>}
        </div>
        <div className="card p-4 text-xs space-y-1">
          <p className="font-semibold text-muted uppercase">Payment Info</p>
          {me.payTo.prompt ? <p>Tap <b>Pay</b> on an invoice below to trigger an instant M-Pesa prompt on your phone.</p> : paybill ? <p>M-Pesa Paybill: <b>{paybill}</b>, Account: Your invoice number.</p> : <p>Contact the store to confirm payment details.</p>}
        </div>
      </div>

      <div className="flex gap-2">
        {([['owe', 'Invoices & Orders'], ['questions', `Inquiries (${me.inquiries.length})`], ['details', 'Profile']] as const).map(([id, l]) => (
          <button key={id} onClick={() => setTab(id)} className={cn('rounded-full px-3.5 py-1.5 text-xs font-semibold ring-1 transition', tab === id ? 'bg-ink text-white ring-ink' : 'bg-white text-muted ring-line')}>{l}</button>
        ))}
      </div>

      {notice && <p className="rounded-md bg-brand-soft px-3 py-2 text-xs font-semibold text-brand">{notice}</p>}

      {tab === 'owe' && (
        me.invoices.length ? (
          <ul className="card divide-y divide-line">
            {me.invoices.map((d: any) => (
              <li key={d.id} className="flex items-center gap-3 px-4 py-3 text-sm">
                <span className="min-w-0 flex-1">
                  <b>{d.number}</b>
                  <span className="block text-2xs text-muted">{day(d.issueDate)} · due {day(d.dueDate)}</span>
                </span>
                <span className="text-right">
                  <b className="num block">{ksh(d.balance > 0 ? d.balance : d.total)}</b>
                  <span className={cn('text-2xs font-semibold', d.balance === 0 ? 'text-good' : d.overdue ? 'text-bad' : 'text-muted')}>
                    {d.balance === 0 ? 'Paid' : d.overdue ? 'Overdue' : 'Due'}
                  </span>
                </span>
                {d.balance > 0 && me.payTo.prompt && (
                  <button className="btn btn-mpesa py-1.5 px-3 text-xs" disabled={paying === d.id} onClick={() => pay(d.id)}>
                    {paying === d.id ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <Smartphone className="h-3.5 w-3.5" />} Pay
                  </button>
                )}
              </li>
            ))}
          </ul>
        ) : (
          <div className="card p-6 text-center text-sm text-muted">
            <p>No active invoices yet.</p>
            <button className="btn btn-primary mt-3 py-2 text-xs" onClick={onBackToShop}>Start Shopping</button>
          </div>
        )
      )}

      {tab === 'questions' && <Questions call={call} items={me.inquiries} onChange={load} />}
      {tab === 'details' && <Details call={call} me={me.customer} onChange={load} />}
    </div>
  );
}

function Questions({ call, items, onChange }: { call: any; items: any[]; onChange: () => void }) {
  const [reply, setReply] = useState<Record<string, string>>({});
  if (!items.length) return <p className="card p-4 text-xs text-muted">No questions yet. Click 'Ask question' at the top to contact the shop.</p>;
  return (
    <ul className="space-y-3">
      {items.map((i) => (
        <li key={i.id} className="card space-y-2 p-4 text-xs">
          <p className="flex justify-between font-semibold">
            <span>{i.number}</span>
            <span className="text-muted">{i.status === 'Won' ? 'Completed' : i.status === 'Lost' ? 'Closed' : i.status === 'Open' ? 'Awaiting reply' : 'Answered'}</span>
          </p>
          <div className="space-y-1.5">
            {i.messages.map((m: any, k: number) => (
              <div key={k} className={cn('rounded-lg p-2.5', m.from === 'customer' ? 'bg-paper text-ink' : 'bg-brand-soft text-ink')}>
                <span className="block text-2xs font-semibold text-muted mb-0.5">{m.from === 'customer' ? 'You' : m.author} · {day(m.at)}</span>
                <p className="text-sm">{m.text}</p>
              </div>
            ))}
          </div>
          <div className="flex gap-2 pt-1">
            <input className="field text-xs" placeholder="Type your reply..." value={reply[i.id] ?? ''} onChange={(e) => setReply({ ...reply, [i.id]: e.target.value })} />
            <button className="btn btn-primary py-2 px-3 text-xs" disabled={!(reply[i.id] ?? '').trim()} onClick={() => call(`/inquiries/${i.id}/messages`, { body: { text: reply[i.id] } }).then(() => { setReply({ ...reply, [i.id]: '' }); onChange(); })}>
              Send
            </button>
          </div>
        </li>
      ))}
    </ul>
  );
}

function Details({ call, me, onChange }: { call: any; me: any; onChange: () => void }) {
  const [f, setF] = useState({ name: me.name, email: me.email ?? '', marketingOptOut: me.marketingOptOut });
  const [saved, setSaved] = useState(false);
  return (
    <section className="card max-w-md space-y-3 p-4 text-xs">
      <label className="block"><span className="mb-1 block font-semibold">Name</span><input className="field text-sm" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} /></label>
      <label className="block"><span className="mb-1 block font-semibold">Email</span><input className="field text-sm" type="email" value={f.email} onChange={(e) => setF({ ...f, email: e.target.value })} /></label>
      <p className="text-muted">Phone: {prettyPhone(me.phone)}</p>
      <label className="flex items-start gap-2 pt-1"><input type="checkbox" className="mt-0.5 h-4 w-4 accent-[var(--color-brand)]" checked={!f.marketingOptOut} onChange={(e) => setF({ ...f, marketingOptOut: !e.target.checked })} /><span><b className="block">Send me special store offers & deals</b><span className="text-2xs text-muted">Untick to unsubscribe from promotional updates.</span></span></label>
      <button className="btn btn-primary mt-2 py-2 text-xs" onClick={() => call('/me', { method: 'PUT', body: f }).then(() => { setSaved(true); onChange(); })}>{saved ? 'Saved!' : 'Save changes'}</button>
    </section>
  );
}
