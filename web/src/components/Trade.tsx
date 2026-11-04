// Trade terms for wholesalers and distributors: the units a product comes in, price lists, a customer's credit
// terms, and stock about to expire. Each shows the consequence next to the choice (what a crate saves over
// singles, how much of a limit is used, the discount a list gives) so nobody has to work it out.
import { useState } from 'react';
import { Link } from 'react-router';
import { Loader2, Plus, Trash2 } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { cn, dayYear, ksh, toCents, CURRENCIES } from '../lib/format';
import { Chips, Empty, ErrorNote, Field, Loading, Sheet, Status, PageError, confirmDialog } from './ui';

export type UnitRow = { name: string; factor: string; price: string };

/** Bigger units for one product, edited inside the product form. */
export function UnitsEditor({ base, basePrice, rows, onChange }: { base: string; basePrice: number; rows: UnitRow[]; onChange: (rows: UnitRow[]) => void }) {
  const set = (i: number, k: keyof UnitRow, v: string) => onChange(rows.map((r, j) => (j === i ? { ...r, [k]: v } : r)));
  return (
    <div className="space-y-2">
      <p className="text-sm font-semibold">Also sold or bought by</p>
      {rows.map((r, i) => {
        const factor = Number(r.factor) || 0;
        const singles = basePrice * factor;
        const own = toCents(r.price);
        return (
          <div key={i} className="space-y-1">
            <div className="grid grid-cols-[1fr_5.5rem_7rem_auto] items-center gap-2">
              <input className="field" aria-label="Unit name" placeholder="Crate, carton, bag…" value={r.name} onChange={(e) => set(i, 'name', e.target.value)} />
              <input className="field num" aria-label={`How many ${base}`} inputMode="decimal" placeholder={`× ${base}`} value={r.factor} onChange={(e) => set(i, 'factor', e.target.value)} />
              <input className="field num" aria-label="Its price" inputMode="decimal" placeholder={singles ? String(singles / 100) : 'Price'} value={r.price} onChange={(e) => set(i, 'price', e.target.value)} />
              <button type="button" className="btn btn-ghost px-2" aria-label="Remove unit" onClick={() => onChange(rows.filter((_, j) => j !== i))}><Trash2 className="h-4 w-4" /></button>
            </div>
            {factor > 1 && basePrice > 0 && (
              <p className="text-xs text-muted">
                1 {r.name || 'unit'} = {factor} {base} · {own > 0 ? <>{ksh(own)}{own < singles && <span className="text-good"> ({ksh(singles - own)} less than {factor} singles)</span>}</> : <>{ksh(singles)} (leave the price blank to charge {factor} × {ksh(basePrice)})</>}
              </p>
            )}
          </div>
        );
      })}
      <button type="button" className="btn btn-ghost text-sm" onClick={() => onChange([...rows, { name: '', factor: '', price: '' }])}><Plus className="h-4 w-4" /> Add a unit</button>
    </div>
  );
}

export const unitsBody = (rows: UnitRow[]) => rows.filter((r) => r.name.trim()).map((r) => ({ name: r.name.trim(), factor: Number(r.factor), price: r.price ? toCents(r.price) : 0 }));

/** A customer's credit limit and price list. Changing them is the owner's or accountant's call. */
export function CustomerTerms({ customer, owes, onChange }: { customer: any; owes: number; onChange: () => void }) {
  const session = useSession();
  const editable = can(session, 'finance');
  const lists = useApi<any[]>('/price-lists');
  const [limit, setLimit] = useState(customer.creditLimit ? String(customer.creditLimit / 100) : '');
  const [list, setList] = useState<string>(customer.priceListId ?? '');
  const [currency, setCurrency] = useState<string>(customer.currency ?? '');
  const [state, setState] = useState({ busy: false, error: '', saved: false });
  const used = customer.creditLimit > 0 ? Math.min(100, Math.round((owes / customer.creditLimit) * 100)) : 0;
  const dirty = toCents(limit) !== (customer.creditLimit ?? 0) || list !== (customer.priceListId ?? '') || currency !== (customer.currency ?? '');
  const save = async () => {
    setState({ busy: true, error: '', saved: false });
    try {
      await api(`/customers/${customer.id}`, { method: 'PUT', body: { ...customer, creditLimit: toCents(limit), priceListId: list || '00000000-0000-0000-0000-000000000000', currency } });
      setState({ busy: false, error: '', saved: true });
      onChange();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message, saved: false });
    }
  };
  const current = lists.data?.find((l) => l.id === customer.priceListId);
  return (
    <section className="card space-y-3 p-4">
      <h2 className="text-sm font-semibold text-muted">Terms</h2>
      {customer.creditLimit > 0 ? (
        <div>
          <p className="text-sm"><b>{ksh(owes)}</b> of {ksh(customer.creditLimit)} limit used</p>
          <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-paper ring-1 ring-line" role="meter" aria-valuenow={used} aria-valuemin={0} aria-valuemax={100} aria-label="Credit used">
            <div className={cn('h-full rounded-full', used >= 100 ? 'bg-bad' : used >= 80 ? 'bg-warn' : 'bg-good')} style={{ width: `${used}%` }} />
          </div>
          <p className="mt-1 text-xs text-muted">{owes >= customer.creditLimit ? 'New invoices need the owner or accountant to allow them.' : `${ksh(customer.creditLimit - owes)} more can go on credit.`}</p>
        </div>
      ) : (
        <p className="text-sm text-muted">No credit limit: any amount can go on credit.</p>
      )}
      <p className="text-sm">{current ? <>Prices from <b>{current.name}</b></> : 'Shelf prices'}{customer.currency ? <>, invoiced in <b>{customer.currency}</b></> : null}</p>
      {editable && (
        <div className="space-y-3 border-t border-line pt-3">
          <Field label="Credit limit (KSh)" hint="Leave blank for no limit.">
            <input className="field num" inputMode="decimal" value={limit} onChange={(e) => setLimit(e.target.value)} placeholder="No limit" />
          </Field>
          <Field label="Price list">
            <select className="field" value={list} onChange={(e) => setList(e.target.value)}>
              <option value="">Shelf prices</option>
              {lists.data?.map((l) => <option key={l.id} value={l.id}>{l.name}</option>)}
            </select>
          </Field>
          <Field label="Invoice them in" hint="New invoices and quotes for them start in this currency. Your books stay in shillings.">
            <select className="field" value={currency} onChange={(e) => setCurrency(e.target.value)}>
              <option value="">Kenya shillings (KES)</option>
              {CURRENCIES.map((c) => <option key={c} value={c}>{c}</option>)}
            </select>
          </Field>
          {state.error && <ErrorNote>{state.error}</ErrorNote>}
          <button className="btn btn-primary w-full" disabled={!dirty || state.busy} onClick={save}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {state.saved && !dirty ? 'Saved' : 'Save terms'}</button>
        </div>
      )}
    </section>
  );
}

/** Settings → Price lists: each list as its prices against the shelf price, with the discount it gives. */
export function PriceLists() {
  const { data, error, reload } = useApi<any[]>('/price-lists');
  const items = useApi<any[]>('/items');
  const [edit, setEdit] = useState<{ id?: string; name: string; prices: Record<string, string> } | null>(null);
  const [q, setQ] = useState('');
  const [state, setState] = useState({ busy: false, error: '' });
  if (error) return <PageError>{error}</PageError>;
  if (!data || !items.data) return <Loading />;
  const open = (l?: any) => setEdit({ id: l?.id, name: l?.name ?? '', prices: Object.fromEntries((l?.prices ?? []).map((p: any) => [p.itemId, String(p.price / 100)])) });
  const save = async () => {
    if (!edit) return;
    setState({ busy: true, error: '' });
    try {
      const body = { name: edit.name, prices: Object.entries(edit.prices).filter(([, v]) => v.trim()).map(([itemId, v]) => ({ itemId, price: toCents(v) })) };
      await (edit.id ? api(`/price-lists/${edit.id}`, { method: 'PUT', body }) : api('/price-lists', { body }));
      setEdit(null);
      setState({ busy: false, error: '' });
      reload();
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };
  const remove = async () => {
    if (!edit?.id || !(await confirmDialog({ title: `Delete “${edit.name}”?`, body: 'Its customers go back to shelf prices.', confirm: 'Delete', danger: true }))) return;
    await api(`/price-lists/${edit.id}`, { method: 'DELETE' });
    setEdit(null);
    reload();
  };
  const shown = items.data.filter((i) => !q.trim() || i.name.toLowerCase().includes(q.trim().toLowerCase()));
  return (
    <>
      <div className="mb-3 flex items-center justify-between gap-3">
        <p className="text-sm text-muted">Give a group of customers their own prices (wholesale, hotels, a distributor). Anything not on the list sells at its shelf price.</p>
        <button className="btn btn-primary shrink-0" onClick={() => open()}><Plus className="h-4 w-4" /> New price list</button>
      </div>
      {data.length ? (
        <ul className="card divide-y divide-line">
          {data.map((l) => (
            <li key={l.id}>
              <button className="flex w-full items-center gap-3 px-4 py-3 text-left hover:bg-paper" onClick={() => open(l)}>
                <b className="min-w-0 flex-1 truncate">{l.name}</b>
                <span className="text-sm text-muted">{l.prices.length} prices · {l.customers} customer{l.customers === 1 ? '' : 's'}</span>
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <Empty title="No price lists yet">Most wholesalers start with one called “Wholesale”. Then pick it on each trade customer.</Empty>
      )}
      <Sheet open={Boolean(edit)} onClose={() => setEdit(null)} title={edit?.id ? edit.name : 'New price list'}>
        {edit && (
          <div className="space-y-4">
            <Field label="Name"><input className="field" value={edit.name} onChange={(e) => setEdit({ ...edit, name: e.target.value })} placeholder="Wholesale" /></Field>
            <input className="field" placeholder="Find a product" value={q} onChange={(e) => setQ(e.target.value)} />
            <ul className="max-h-[50vh] divide-y divide-line overflow-y-auto rounded-md ring-1 ring-line">
              {shown.map((i) => {
                const v = edit.prices[i.id] ?? '';
                const off = v ? Math.round((1 - toCents(v) / i.price) * 100) : 0;
                return (
                  <li key={i.id} className="flex items-center gap-3 px-3 py-2">
                    <span className="min-w-0 flex-1">
                      <span className="block truncate text-sm font-medium">{i.name}</span>
                      <span className="text-xs text-muted">Shelf {ksh(i.price)} / {i.unit}{v && off !== 0 && <b className={off > 0 ? ' text-good' : ' text-warn'}> · {off > 0 ? `${off}% off` : `${-off}% above`}</b>}</span>
                    </span>
                    <input className="field num w-28" inputMode="decimal" aria-label={`${i.name} price`} placeholder="Shelf price" value={v} onChange={(e) => setEdit({ ...edit, prices: { ...edit.prices, [i.id]: e.target.value } })} />
                  </li>
                );
              })}
            </ul>
            {state.error && <ErrorNote>{state.error}</ErrorNote>}
            <div className="flex gap-2">
              <button className="btn btn-primary flex-1 py-3" disabled={state.busy} onClick={save}>{state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save</button>
              {edit.id && <button className="btn btn-ghost text-bad" onClick={remove}><Trash2 className="h-4 w-4" /> Delete</button>}
            </div>
          </div>
        )}
      </Sheet>
    </>
  );
}

/** Stock → Expiring: batches by expiry, with what's at stake in shillings. */
export function Expiring() {
  const session = useSession();
  const [days, setDays] = useState('60');
  const { data, error, reload } = useApi<any>(`/stock/expiring?days=${days}`);
  const [busy, setBusy] = useState('');
  const writeOff = async (r: any) => {
    if (!(await confirmDialog({ title: `Write off batch ${r.batchNo}?`, body: `${r.quantity} ${r.unit} of ${r.item} is thrown away, and ${ksh(r.value)} is booked as a stock loss.`, confirm: 'Write off', danger: true }))) return;
    setBusy(r.id);
    try {
      await api(`/stock/batches/${r.id}/write-off`, { body: {} });
      reload();
    } finally {
      setBusy('');
    }
  };
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-center gap-3">
        <Chips label="Expiring within" value={days} onChange={setDays} options={[['30', '30 days'], ['60', '60 days'], ['90', '90 days'], ['180', '6 months']]} />
        <p className="text-sm text-muted sm:ml-auto">
          {data.expired > 0 && <b className="text-bad">{ksh(data.expiredValue)} already expired · </b>}
          {ksh(data.soonValue)} expiring soon (at cost)
        </p>
      </div>
      {data.rows.length ? (
        <ul className="card divide-y divide-line">
          {data.rows.map((r: any) => (
            <li key={r.id} className="flex items-center gap-3 px-4 py-3">
              <span className="min-w-0 flex-1">
                <b className="block truncate">{r.item}</b>
                <span className="text-sm text-muted">Batch {r.batchNo} · {r.location} · {r.quantity} {r.unit}</span>
              </span>
              <span className="text-right">
                <span className="num block font-semibold">{ksh(r.value)}</span>
                <Status tone={r.daysLeft < 0 ? 'bad' : r.daysLeft <= 14 ? 'warn' : 'plain'}>{r.daysLeft < 0 ? `Expired ${dayYear(r.expiry)}` : r.daysLeft === 0 ? 'Expires today' : `${r.daysLeft} days · ${dayYear(r.expiry)}`}</Status>
              </span>
              {r.daysLeft < 0 && can(session, 'stock') && (
                <button className="btn btn-ghost shrink-0 text-bad" disabled={busy === r.id} onClick={() => writeOff(r)}>{busy === r.id ? <Loader2 className="h-4 w-4 animate-spin" /> : <Trash2 className="h-4 w-4" />} Write off</button>
              )}
            </li>
          ))}
        </ul>
      ) : (
        <Empty title="Nothing expiring">No batches expire in the next {days} days. Turn on “Kept by batch” for a product in <Link className="underline" to="/settings?tab=items">Products</Link> to track expiry.</Empty>
      )}
      {data.expired > 0 && <p className="text-xs text-muted">Expired stock can’t be sold. Write it off when it’s thrown away or returned.</p>}
    </section>
  );
}
