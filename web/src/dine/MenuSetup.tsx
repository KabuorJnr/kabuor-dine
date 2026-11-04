// The menu: what guests see on the QR menu, section by section, and which screen makes each dish. Anyone on the
// team can mark a dish finished for the day; the owner and accountant add dishes, set prices, choices ("Spice: Mild |
// Medium | Hot") and what to suggest with it. Every dish is an item for sale in the books, so stock and sales follow.
import { useMemo, useState } from 'react';
import { Eye, EyeOff, Flame, Loader2, Pencil, Plus, Sparkles } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, fromCents, ksh, toCents } from '../lib/format';
import { ErrorNote, Field, Loading, PageError, PageHeader, SearchBox, Sheet, SheetFooter, Status } from '../components/ui';
import type { Dish } from './lib';

type MenuData = { stations: string[]; items: Dish[] };

export default function MenuSetup() {
  const s = useSession();
  const manage = can(s, 'dine.manage');
  const { data, error, reload } = useApi<MenuData>('/dine/menu');
  const [q, setQ] = useState('');
  const [section, setSection] = useState('');
  const [editing, setEditing] = useState<Dish | 'new' | null>(null);
  const [busy, setBusy] = useState('');
  const [problem, setProblem] = useState('');

  const on = useMemo(() => (data?.items ?? []).filter((d) => d.onMenu && (!q || d.name.toLowerCase().includes(q.toLowerCase()))), [data, q]);
  const off = useMemo(() => (data?.items ?? []).filter((d) => !d.onMenu && (!q || d.name.toLowerCase().includes(q.toLowerCase()))), [data, q]);
  const categories = [...new Set(on.map((d) => d.category || 'Menu'))];

  if (error && !data) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const act = async (key: string, fn: () => Promise<unknown>) => {
    setBusy(key); setProblem('');
    try { await fn(); reload(); } catch (e) { setProblem((e as Error).message); }
    setBusy('');
  };

  return (
    <div className="space-y-5">
      <PageHeader title="Menu" subtitle={`${on.length} dishes and drinks on the QR menu · made at ${data.stations.join(', ')}`}
        actions={manage && <>
          {data.items.filter((d) => d.onMenu).length === 0 && (
            <button className="btn btn-ghost" disabled={busy !== ''} onClick={() => act('sample', () => api('/dine/menu/sample', { body: {} }))}>
              {busy === 'sample' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Sparkles className="h-4 w-4" />} Start with a sample Kenyan menu
            </button>
          )}
          <button className="btn bg-dine text-white hover:bg-dine-hover" onClick={() => setEditing('new')}><Plus className="h-4 w-4" /> Add a dish</button>
        </>} />
      {problem && <ErrorNote>{problem}</ErrorNote>}
      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <div className="sm:w-72"><SearchBox value={q} onChange={setQ} placeholder="Find a dish or drink" /></div>
        {categories.length > 1 && (
          <div className="flex flex-wrap gap-1.5" role="tablist" aria-label="Menu sections">
            {['', ...categories].map((c) => (
              <button key={c || 'all'} role="tab" aria-selected={section === c} onClick={() => setSection(c)}
                className={cn('shrink-0 rounded-full px-3.5 py-1.5 text-sm font-semibold ring-1 transition', section === c ? 'bg-ink text-white ring-ink' : 'bg-white text-ink-2 ring-line hover:bg-paper')}>
                {c || 'All'} <span className={cn('ml-0.5 text-xs', section === c ? 'text-white/70' : 'text-faint')}>{c ? on.filter((d) => (d.category || 'Menu') === c).length : on.length}</span>
              </button>
            ))}
          </div>
        )}
      </div>

      {categories.filter((c) => !section || c === section).map((c) => (
        <section key={c} className="card overflow-hidden">
          <h2 className="flex items-center justify-between border-b border-line bg-paper px-4 py-2.5 text-sm font-bold">{c}<span className="text-xs font-medium text-faint">{on.filter((d) => (d.category || 'Menu') === c).length} items</span></h2>
          <ul className="divide-y divide-line">
            {on.filter((d) => (d.category || 'Menu') === c).map((d) => (
              <li key={d.id} className={cn('flex items-start gap-3 px-4 py-3', d.hidden && 'opacity-60')}>
                <div className="min-w-0 flex-1">
                  <p className="flex flex-wrap items-center gap-x-2 gap-y-1 leading-snug font-semibold">
                    {d.name}
                    {d.popular && <Flame className="h-4 w-4 text-dine" aria-label="Popular" />}
                    {d.hidden && <Status tone="plain">Hidden from guests</Status>}
                    {d.soldOut && <Status tone="bad">{d.markedSoldOut ? 'Finished' : 'Out of stock'}</Status>}
                  </p>
                  <p className="mt-1 text-sm leading-snug text-muted">
                    {d.station}{d.options.length > 0 && ` · choices: ${d.options.map((o) => o.name).join(', ')}`}{d.suggest && ` · suggests ${d.suggest.name}`}{d.trackStock && ` · ${d.onHand ?? 0} in stock`}
                  </p>
                </div>
                <div className="flex shrink-0 flex-col items-end gap-1.5 sm:flex-row sm:items-center sm:gap-4">
                <span className="num font-semibold sm:w-24 sm:text-right">{ksh(d.price)}</span>
                <div className="flex items-center gap-1.5">
                  <label className="flex cursor-pointer items-center gap-2 text-xs font-semibold text-muted select-none">
                    <span className="hidden sm:inline">{d.markedSoldOut ? 'Finished' : 'Available'}</span>
                    <button type="button" role="switch" aria-checked={!d.markedSoldOut} aria-label={`${d.name} available`} disabled={busy !== ''}
                      onClick={() => act(d.id, () => api(`/dine/menu/${d.id}/sold-out`, { body: { soldOut: !d.markedSoldOut } }))}
                      className={cn('relative h-6 w-11 shrink-0 rounded-full transition', d.markedSoldOut ? 'bg-line' : 'bg-good')}>
                      <span className={cn('absolute top-0.5 h-5 w-5 rounded-full bg-white shadow transition-all', d.markedSoldOut ? 'left-0.5' : 'left-[22px]')} />
                    </button>
                  </label>
                  {manage && <button className="grid h-9 w-9 place-items-center rounded-md text-muted hover:bg-paper" aria-label={`Edit ${d.name}`} onClick={() => setEditing(d)}><Pencil className="h-4 w-4" /></button>}
                </div>
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}

      {on.length === 0 && (
        <div className="card px-6 py-12 text-center">
          <p className="font-semibold">{q ? 'Nothing matches' : 'The menu is empty'}</p>
          {!q && <p className="mt-1 text-sm text-muted">{manage ? 'Add your dishes, or start with the sample menu and change it.' : 'Ask the owner to add the menu.'}</p>}
        </div>
      )}

      {manage && off.length > 0 && (
        <section className="card overflow-hidden">
          <h2 className="border-b border-line bg-paper px-4 py-2 text-sm font-bold">For sale, but not on the menu</h2>
          <p className="px-4 pt-2 text-sm text-muted">Items from your stock list. Put the ones guests can order on the menu.</p>
          <ul className="divide-y divide-line">
            {off.slice(0, 50).map((d) => (
              <li key={d.id} className="flex items-center gap-3 px-4 py-2.5">
                <span className="min-w-0 flex-1 truncate">{d.name}</span>
                <span className="num text-sm text-muted">{ksh(d.price)}</span>
                <button className="btn btn-ghost px-3 py-1.5" onClick={() => setEditing(d)}><Eye className="h-4 w-4" /> Put on menu</button>
              </li>
            ))}
          </ul>
        </section>
      )}

      {editing && <DishSheet dish={editing === 'new' ? null : editing} data={data} onClose={() => setEditing(null)} onSaved={() => { setEditing(null); reload(); }} />}
    </div>
  );
}

function DishSheet({ dish, data, onClose, onSaved }: { dish: Dish | null; data: MenuData; onClose: () => void; onSaved: () => void }) {
  const categories = [...new Set(data.items.filter((d) => d.onMenu).map((d) => d.category).filter(Boolean))];
  const [f, setF] = useState({
    name: dish?.name ?? '', price: dish ? fromCents(dish.price) : '', category: dish?.category || categories[0] || 'Mains',
    station: dish?.station || data.stations[0], description: dish?.description ?? '', options: dish?.optionsText ?? '',
    popular: dish?.popular ?? false, hidden: dish ? dish.hidden : false, suggest: dish?.suggest?.id ?? '', sort: dish?.sort ?? 0,
  });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setBusy(true); setError('');
    const body = {
      name: f.name, price: toCents(f.price), category: f.category, station: f.station, description: f.description, options: f.options,
      popular: f.popular, hidden: f.hidden, sort: Number(f.sort) || 0, suggestItemId: f.suggest || null, clearSuggestion: !f.suggest,
    };
    try {
      await api(dish ? `/dine/menu/${dish.id}` : '/dine/menu', { method: dish ? 'PUT' : 'POST', body });
      onSaved();
    } catch (err) { setError((err as Error).message); setBusy(false); }
  };
  return (
    <Sheet open onClose={onClose} title={dish ? dish.onMenu ? `Edit ${dish.name}` : `Put ${dish.name} on the menu` : 'Add a dish'} description="Guests see the name, description, price and choices on their phone.">
      <form onSubmit={submit} className="space-y-4">
        <div className="grid gap-4 sm:grid-cols-[1fr_9rem]">
          <Field label="Name"><input className="field" value={f.name} onChange={(e) => setF({ ...f, name: e.target.value })} required maxLength={120} placeholder="e.g. Nyama choma (½ kg)" /></Field>
          <Field label="Price (KSh, with VAT)"><input className="field num" inputMode="decimal" value={f.price} onChange={(e) => setF({ ...f, price: e.target.value })} required placeholder="950" /></Field>
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <Field label="Menu section">
            <input className="field" list="dine-sections" value={f.category} onChange={(e) => setF({ ...f, category: e.target.value })} maxLength={60} />
            <datalist id="dine-sections">{[...new Set([...categories, 'Starters', 'Mains', 'Grill', 'Sides', 'Snacks', 'Desserts', 'Drinks'])].map((c) => <option key={c} value={c} />)}</datalist>
          </Field>
          <Field label="Made at" hint="The screen the order goes to.">
            <select className="field" value={f.station} onChange={(e) => setF({ ...f, station: e.target.value })}>{data.stations.map((st) => <option key={st}>{st}</option>)}</select>
          </Field>
        </div>
        <Field label="Description"><textarea className="field" rows={2} value={f.description} onChange={(e) => setF({ ...f, description: e.target.value })} maxLength={400} placeholder="What it is, how it's made, what comes with it" /></Field>
        <Field label="Choices guests make" hint={<>One per line, as <b>Name: option | option</b>. For example “Spice: Mild | Medium | Hot” or “Side: Ugali | Chips | Rice”.</>}>
          <textarea className="field font-mono text-sm" rows={3} value={f.options} onChange={(e) => setF({ ...f, options: e.target.value })} maxLength={1000} placeholder={'Doneness: Medium | Well done\nSide: Ugali | Chips'} />
        </Field>
        <Field label="Goes well with" hint="Suggested to the guest when they add this dish.">
          <select className="field" value={f.suggest} onChange={(e) => setF({ ...f, suggest: e.target.value })}>
            <option value="">Nothing</option>
            {data.items.filter((d) => d.onMenu && d.id !== dish?.id).map((d) => <option key={d.id} value={d.id}>{d.name}</option>)}
          </select>
        </Field>
        <div className="flex flex-wrap gap-x-6 gap-y-2">
          <label className="flex items-center gap-2 text-sm font-medium"><input type="checkbox" checked={f.popular} onChange={(e) => setF({ ...f, popular: e.target.checked })} /> <Flame className="h-4 w-4 text-dine" /> Show under “Guests love”</label>
          <label className="flex items-center gap-2 text-sm font-medium"><input type="checkbox" checked={f.hidden} onChange={(e) => setF({ ...f, hidden: e.target.checked })} /> <EyeOff className="h-4 w-4 text-muted" /> Hide from guests (staff can still order it)</label>
        </div>
        <Field label="Position in its section" hint="Lower numbers come first."><input type="number" className="field w-28" value={f.sort} onChange={(e) => setF({ ...f, sort: Number(e.target.value) })} min={0} max={9999} /></Field>
        {error && <ErrorNote>{error}</ErrorNote>}
        <SheetFooter>
          <button className="btn bg-dine text-white hover:bg-dine-hover" disabled={busy}>{busy && <Loader2 className="h-4 w-4 animate-spin" />} {dish?.onMenu ? 'Save' : 'Put on the menu'}</button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}
