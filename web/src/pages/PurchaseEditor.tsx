import { useEffect, useMemo, useState } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';
import { ArrowLeft, Loader2, Plus, Trash2, UserPlus } from 'lucide-react';
import { api, useApi } from '../lib/api';
import { ksh, prettyPhone, toCents, todayIso } from '../lib/format';
import { ErrorNote, Field, PageHeader } from '../components/ui';

type Item = { id: string; name: string; sku: string; price: number; taxType: string; unit: string; trackStock: boolean; trackBatches?: boolean };
type ExpenseAccount = { id: string; code: string; name: string };
type Supplier = { id: string; number: string; name: string; phone: string; kraPin: string; weOwe?: number };
type Location = { id: string; name: string; isDefault: boolean };

type Line = {
  key: number;
  type: 'item' | 'expense';
  itemId: string | null;
  accountId: string | null;
  description: string;
  quantity: string;
  unitCost: string; // in KSh string
  taxType: string;
  unit?: string;
  batchNo?: string;
  expiry?: string;
};

const TAX: [string, string, number][] = [
  ['B_Standard16', 'VAT 16%', 1600],
  ['E_Reduced8', 'VAT 8%', 800],
  ['C_ZeroRated', 'Zero-rated', 0],
  ['A_Exempt', 'Exempt', 0],
  ['D_NonVat', 'Non-VAT', 0],
];
const bps = (t: string) => TAX.find((x) => x[0] === t)?.[2] ?? 0;
let nextKey = 1;
const blank = (): Line => ({
  key: nextKey++,
  type: 'item',
  itemId: null,
  accountId: null,
  description: '',
  quantity: '1',
  unitCost: '',
  taxType: 'B_Standard16',
});

function SupplierPicker({ value, onChange }: { value: Supplier | null; onChange: (s: Supplier | null) => void }) {
  const [q, setQ] = useState('');
  const [open, setOpen] = useState(false);
  const [adding, setAdding] = useState(false);
  const [phone, setPhone] = useState('');
  const [kraPin, setKraPin] = useState('');
  const [error, setError] = useState('');
  const { data } = useApi<{ items: Supplier[] }>(open ? `/suppliers?q=${encodeURIComponent(q)}` : null);

  if (value)
    return (
      <div className="flex items-center justify-between gap-3 rounded-md bg-white px-3 py-2.5 ring-1 ring-line">
        <span className="min-w-0">
          <b className="block truncate">{value.name}</b>
          <span className="text-xs text-muted">
            {value.number}
            {value.phone && ` · ${prettyPhone(value.phone)}`}
            {value.kraPin && ` · PIN ${value.kraPin}`}
          </span>
        </span>
        <button type="button" className="text-sm font-semibold text-brand" onClick={() => onChange(null)}>
          Change
        </button>
      </div>
    );

  const add = async () => {
    setError('');
    try {
      const res = await api<Supplier>('/suppliers', { body: { name: q, phone, kraPin } });
      onChange(res);
      setAdding(false);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <div className="relative">
      <input
        className="field"
        value={q}
        placeholder="Search supplier name, phone, number or PIN"
        onFocus={() => setOpen(true)}
        onChange={(e) => {
          setQ(e.target.value);
          setOpen(true);
        }}
        aria-label="Supplier"
      />
      {open && !adding && (
        <div className="absolute inset-x-0 top-full z-20 mt-1 max-h-60 overflow-y-auto rounded-md bg-white shadow-lg ring-1 ring-line">
          {(data?.items ?? []).map((s) => (
            <button
              key={s.id}
              type="button"
              className="flex w-full items-center justify-between gap-3 px-3 py-2.5 text-left hover:bg-paper"
              onClick={() => {
                onChange(s);
                setOpen(false);
              }}
            >
              <span className="min-w-0">
                <b className="block truncate text-sm">{s.name}</b>
                <span className="text-xs text-muted">
                  {s.number}
                  {s.phone && ` · ${prettyPhone(s.phone)}`}
                </span>
              </span>
              {s.weOwe ? <span className="num text-xs text-warn">we owe {ksh(s.weOwe)}</span> : null}
            </button>
          ))}
          <button
            type="button"
            className="flex w-full items-center gap-2 border-t border-line px-3 py-2.5 text-sm font-semibold text-brand hover:bg-paper"
            onClick={() => setAdding(true)}
          >
            <UserPlus className="h-4 w-4" /> {q.trim() ? `Add “${q.trim()}” as new supplier` : 'Add new supplier'}
          </button>
        </div>
      )}
      {adding && (
        <div className="mt-2 space-y-2 rounded-md bg-brand-soft/50 p-3 ring-1 ring-brand/20">
          <input
            className="field"
            value={q}
            onChange={(e) => setQ(e.target.value)}
            placeholder="Supplier business name"
            aria-label="Supplier name"
          />
          <input
            className="field"
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
            placeholder="Phone number"
            inputMode="tel"
          />
          <input
            className="field uppercase"
            value={kraPin}
            onChange={(e) => setKraPin(e.target.value)}
            placeholder="KRA PIN (optional)"
            maxLength={11}
          />
          {error && <ErrorNote>{error}</ErrorNote>}
          <div className="flex gap-2">
            <button type="button" className="btn btn-primary py-2" onClick={add}>
              Add supplier
            </button>
            <button type="button" className="btn btn-ghost py-2" onClick={() => setAdding(false)}>
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

export default function PurchaseEditor({ kind: defaultKind }: { kind?: 'Order' | 'Bill' }) {
  const { id } = useParams();
  const [params] = useSearchParams();
  const navigate = useNavigate();

  const kind = (params.get('kind') as 'Order' | 'Bill') || defaultKind || 'Bill';
  const isBill = kind === 'Bill';

  const [supplier, setSupplier] = useState<Supplier | null>(null);
  const [issueDate, setIssueDate] = useState(todayIso());
  const [dueInDays, setDueInDays] = useState('30');
  const [supplierRef, setSupplierRef] = useState('');
  const [locationId, setLocationId] = useState<string>('');
  const [notes, setNotes] = useState('');
  const [lines, setLines] = useState<Line[]>([blank()]);
  const [state, setState] = useState({ busy: false, error: '' });

  const { data: existing } = useApi<{ document: any }>(id ? `/purchases/${id}` : null);
  const { data: items } = useApi<Item[]>('/items');
  const { data: units } = useApi<{ itemId: string; name: string; factor: number }[]>('/items/units');
  const { data: expenseAccounts } = useApi<ExpenseAccount[]>('/expense-accounts');
  const { data: locations } = useApi<Location[]>('/locations');

  // Prepopulate if supplierId given in query param
  const preSupplierId = params.get('supplierId');
  const { data: preSupplier } = useApi<any>(preSupplierId && !supplier ? `/suppliers/${preSupplierId}` : null);
  useEffect(() => {
    if (preSupplier?.supplier) setSupplier(preSupplier.supplier);
  }, [preSupplier]);

  useEffect(() => {
    if (locations?.length && !locationId) {
      const def = locations.find((l) => l.isDefault) ?? locations[0];
      if (def) setLocationId(def.id);
    }
  }, [locations, locationId]);

  useEffect(() => {
    if (existing?.document) {
      const d = existing.document;
      setSupplier(d.supplier);
      setIssueDate(d.issueDate);
      const days = Math.round((new Date(d.dueDate).getTime() - new Date(d.issueDate).getTime()) / 86400000);
      setDueInDays(days >= 0 ? days.toString() : '30');
      setSupplierRef(d.supplierRef || '');
      if (d.locationId) setLocationId(d.locationId);
      setNotes(d.notes || '');
      if (d.lines?.length) {
        setLines(
          d.lines.map((l: any) => ({
            key: nextKey++,
            type: l.itemId ? 'item' : 'expense',
            itemId: l.itemId,
            accountId: l.accountId,
            description: l.description || '',
            quantity: l.quantity.toString(),
            unitCost: (l.unitCost / 100).toString(),
            taxType: l.taxType,
            unit: l.unit || undefined,
            batchNo: l.batchNo || undefined,
            expiry: l.expiry || undefined,
          })),
        );
      }
    }
  }, [existing]);

  const updateLine = (key: number, patch: Partial<Line>) => {
    setLines((ls) => ls.map((l) => (l.key === key ? { ...l, ...patch } : l)));
  };

  const removeLine = (key: number) => {
    setLines((ls) => (ls.length > 1 ? ls.filter((l) => l.key !== key) : ls));
  };

  const calc = useMemo(() => {
    let subtotalNet = 0;
    let vat = 0;
    let total = 0;
    for (const l of lines) {
      const q = parseFloat(l.quantity) || 0;
      const c = toCents(l.unitCost);
      const lineTotal = Math.round(q * c);
      const rate = bps(l.taxType);
      const tax = rate > 0 ? Math.round(lineTotal - (lineTotal * 10000) / (10000 + rate)) : 0;
      const net = lineTotal - tax;
      subtotalNet += net;
      vat += tax;
      total += lineTotal;
    }
    return { subtotalNet, vat, total };
  }, [lines]);

  const submit = async (postDirectly: boolean) => {
    if (!supplier) {
      setState({ busy: false, error: 'Select a supplier.' });
      return;
    }
    const validLines = lines.filter((l) => (l.itemId || l.accountId || l.description.trim()) && parseFloat(l.quantity) > 0);
    if (!validLines.length) {
      setState({ busy: false, error: 'Add at least one line with quantity and cost.' });
      return;
    }

    setState({ busy: true, error: '' });
    const payload = {
      kind,
      supplierId: supplier.id,
      issueDate,
      dueInDays: parseInt(dueInDays) || 30,
      supplierRef,
      locationId: locationId || undefined,
      notes,
      lines: validLines.map((l) => ({
        itemId: l.type === 'item' ? l.itemId : null,
        accountId: l.type === 'expense' ? l.accountId : null,
        description: l.description.trim() || undefined,
        quantity: parseFloat(l.quantity) || 1,
        unitCost: toCents(l.unitCost),
        taxType: l.taxType,
        unit: l.type === 'item' ? l.unit : undefined,
        batchNo: l.type === 'item' ? l.batchNo : undefined,
        expiry: l.type === 'item' && l.expiry ? l.expiry : undefined,
      })),
    };

    try {
      const doc = id
        ? await api(`/purchases/${id}`, { method: 'PUT', body: payload })
        : await api('/purchases', { body: payload });

      if (postDirectly && isBill) {
        await api(`/purchases/${doc.id}/post`, { method: 'POST' });
      }
      navigate(`/purchases/${doc.id}`);
    } catch (e) {
      setState({ busy: false, error: (e as Error).message });
    }
  };

  return (
    <div className="space-y-6">
      <PageHeader
        title={id ? `Edit ${isBill ? 'bill' : 'order'}` : `New ${isBill ? 'bill' : 'purchase order'}`}
        subtitle={isBill ? 'Record goods received or supplier expenses' : 'Raise an order to send to a supplier'}
        actions={
          <button type="button" className="btn btn-ghost" onClick={() => navigate(-1)}>
            <ArrowLeft className="h-4 w-4" /> Cancel
          </button>
        }
      />

      {state.error && <ErrorNote>{state.error}</ErrorNote>}

      {/* Details beside the lines only on very wide screens; otherwise they sit above in two columns, so neither side is squeezed. */}
      <div className="grid gap-4 2xl:grid-cols-3">
        <div className="card grid content-start gap-4 p-4 sm:grid-cols-2 2xl:col-span-1 2xl:grid-cols-1">
          <Field label="Supplier" hint="Who are you buying from?">
            <SupplierPicker value={supplier} onChange={setSupplier} />
          </Field>
          <Field label={isBill ? 'Supplier invoice / receipt no.' : 'Reference'} hint="Their invoice reference, e.g. KB-1049">
            <input
              className="field"
              value={supplierRef}
              onChange={(e) => setSupplierRef(e.target.value)}
              placeholder="e.g. INV-9901"
            />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Date">
              <input
                className="field"
                type="date"
                value={issueDate}
                onChange={(e) => setIssueDate(e.target.value)}
              />
            </Field>
            <Field label="Payment terms">
              <select
                className="field"
                value={dueInDays}
                onChange={(e) => setDueInDays(e.target.value)}
              >
                <option value="0">Due immediately</option>
                <option value="7">7 days</option>
                <option value="14">14 days</option>
                <option value="30">30 days (net 30)</option>
                <option value="60">60 days</option>
              </select>
            </Field>
          </div>
          {locations && locations.length > 0 && (
            <Field label="Destination store / warehouse">
              <select
                className="field"
                value={locationId}
                onChange={(e) => setLocationId(e.target.value)}
              >
                {locations.map((loc) => (
                  <option key={loc.id} value={loc.id}>
                    {loc.name} {loc.isDefault ? '(Default)' : ''}
                  </option>
                ))}
              </select>
            </Field>
          )}
          <Field label="Internal notes">
            <textarea
              className="field"
              rows={2}
              value={notes}
              onChange={(e) => setNotes(e.target.value)}
              placeholder="Delivery instructions, agreement details..."
            />
          </Field>
        </div>

        <div className="card p-4 space-y-4 2xl:col-span-2">
          <div className="flex items-center justify-between">
            <h2 className="text-sm font-semibold text-ink">Line items</h2>
            <button
              type="button"
              className="text-xs font-semibold text-brand hover:underline flex items-center gap-1"
              onClick={() => setLines([...lines, blank()])}
            >
              <Plus className="h-3.5 w-3.5" /> Add line
            </button>
          </div>

          <div className="space-y-3">
            {lines.map((l) => (
              <div key={l.key} className="rounded-lg bg-paper p-3 ring-1 ring-line space-y-2.5">
                <div className="flex items-center justify-between gap-2">
                  <div className="inline-flex rounded bg-white p-0.5 text-xs font-semibold ring-1 ring-line">
                    <button
                      type="button"
                      className={`px-2 py-0.5 rounded ${l.type === 'item' ? 'bg-brand-soft text-brand' : 'text-muted'}`}
                      onClick={() => updateLine(l.key, { type: 'item', accountId: null })}
                    >
                      Stock item
                    </button>
                    <button
                      type="button"
                      className={`px-2 py-0.5 rounded ${l.type === 'expense' ? 'bg-brand-soft text-brand' : 'text-muted'}`}
                      onClick={() => updateLine(l.key, { type: 'expense', itemId: null })}
                    >
                      Expense account
                    </button>
                  </div>
                  {lines.length > 1 && (
                    <button
                      type="button"
                      onClick={() => removeLine(l.key)}
                      className="text-muted hover:text-bad p-1"
                      title="Remove line"
                    >
                      <Trash2 className="h-4 w-4" />
                    </button>
                  )}
                </div>

                <div className="grid gap-2 sm:grid-cols-12">
                  <div className="sm:col-span-5">
                    {l.type === 'item' ? (
                      <select
                        className="field text-sm"
                        value={l.itemId ?? ''}
                        onChange={(e) => {
                          const it = items?.find((x) => x.id === e.target.value);
                          updateLine(l.key, {
                            itemId: e.target.value || null,
                            description: it ? it.name : l.description,
                            taxType: it?.taxType || l.taxType,
                          });
                        }}
                      >
                        <option value="">Select item...</option>
                        {items?.map((it) => (
                          <option key={it.id} value={it.id}>
                            {it.name} {it.sku ? `(${it.sku})` : ''}
                          </option>
                        ))}
                      </select>
                    ) : (
                      <select
                        className="field text-sm"
                        value={l.accountId ?? ''}
                        onChange={(e) => {
                          const acc = expenseAccounts?.find((x) => x.id === e.target.value);
                          updateLine(l.key, {
                            accountId: e.target.value || null,
                            description: acc ? acc.name : l.description,
                          });
                        }}
                      >
                        <option value="">Select expense category...</option>
                        {expenseAccounts?.map((acc) => (
                          <option key={acc.id} value={acc.id}>
                            {acc.name} ({acc.code})
                          </option>
                        ))}
                      </select>
                    )}
                  </div>
                  <div className="sm:col-span-7">
                    <input
                      className="field text-sm"
                      value={l.description}
                      onChange={(e) => updateLine(l.key, { description: e.target.value })}
                      placeholder="Description on bill"
                    />
                  </div>
                </div>

                <div className="grid grid-cols-3 gap-2 sm:grid-cols-12 items-center">
                  <div className="sm:col-span-3">
                    <label className="text-2xs font-semibold text-muted block mb-1">Quantity</label>
                    <input
                      className="field text-sm"
                      type="number"
                      step="any"
                      min="0"
                      value={l.quantity}
                      onChange={(e) => updateLine(l.key, { quantity: e.target.value })}
                    />
                  </div>
                  <div className="sm:col-span-4">
                    <label className="text-2xs font-semibold text-muted block mb-1">Unit cost (KSh, incl. VAT)</label>
                    <input
                      className="field text-sm"
                      type="number"
                      step="any"
                      min="0"
                      value={l.unitCost}
                      onChange={(e) => updateLine(l.key, { unitCost: e.target.value })}
                      placeholder="0.00"
                    />
                  </div>
                  <div className="sm:col-span-3">
                    <label className="text-2xs font-semibold text-muted block mb-1">Tax</label>
                    <select
                      className="field text-sm"
                      value={l.taxType}
                      onChange={(e) => updateLine(l.key, { taxType: e.target.value })}
                    >
                      {TAX.map(([val, label]) => (
                        <option key={val} value={val}>
                          {label}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="sm:col-span-2 text-right pt-4">
                    <span className="text-xs text-muted block">Line total</span>
                    <b className="num text-sm font-semibold">
                      {ksh(Math.round((parseFloat(l.quantity) || 0) * toCents(l.unitCost)))}
                    </b>
                  </div>
                </div>
                {(() => {
                  const it = l.type === 'item' ? items?.find((x) => x.id === l.itemId) : undefined;
                  const its = units?.filter((u) => u.itemId === l.itemId) ?? [];
                  if (!it || (!its.length && !it.trackBatches)) return null;
                  const u = its.find((x) => x.name === l.unit);
                  return (
                    <div className="grid grid-cols-3 gap-2 sm:grid-cols-12 items-end">
                      {its.length > 0 && (
                        <div className="sm:col-span-3">
                          <label className="text-2xs font-semibold text-muted block mb-1">Bought by the</label>
                          <select className="field text-sm" value={l.unit ?? ''} onChange={(e) => updateLine(l.key, { unit: e.target.value || undefined })}>
                            <option value="">{it.unit}</option>
                            {its.map((x) => <option key={x.name} value={x.name}>{x.name} ({x.factor} {it.unit})</option>)}
                          </select>
                        </div>
                      )}
                      {it.trackBatches && (
                        <>
                          <div className="sm:col-span-4">
                            <label className="text-2xs font-semibold text-muted block mb-1">Batch / lot number</label>
                            <input className="field text-sm" required value={l.batchNo ?? ''} onChange={(e) => updateLine(l.key, { batchNo: e.target.value })} placeholder="From the packaging" />
                          </div>
                          <div className="sm:col-span-3">
                            <label className="text-2xs font-semibold text-muted block mb-1">Expiry</label>
                            <input className="field text-sm" type="date" value={l.expiry ?? ''} onChange={(e) => updateLine(l.key, { expiry: e.target.value })} />
                          </div>
                        </>
                      )}
                      {u && <p className="col-span-3 text-xs text-muted sm:col-span-12">{(parseFloat(l.quantity) || 0) * u.factor} {it.unit} go into stock.</p>}
                    </div>
                  );
                })()}
              </div>
            ))}
          </div>

          <div className="mt-4 border-t border-line pt-4 space-y-1.5 text-sm">
            <div className="flex justify-between text-muted">
              <span>Net amount</span>
              <span className="num">{ksh(calc.subtotalNet)}</span>
            </div>
            <div className="flex justify-between text-muted">
              <span>VAT included (Input tax)</span>
              <span className="num">{ksh(calc.vat)}</span>
            </div>
            <div className="flex justify-between text-base font-bold text-ink border-t border-line pt-2">
              <span>Total</span>
              <span className="num text-lg">{ksh(calc.total)}</span>
            </div>
          </div>

          <div className="pt-4 flex flex-wrap gap-2 justify-end">
            <button
              type="button"
              className="btn btn-ghost"
              disabled={state.busy}
              onClick={() => submit(false)}
            >
              {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Save Draft
            </button>
            {isBill ? (
              <button
                type="button"
                className="btn btn-primary"
                disabled={state.busy}
                onClick={() => submit(true)}
              >
                {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Post Bill
              </button>
            ) : (
              <button
                type="button"
                className="btn btn-primary"
                disabled={state.busy}
                onClick={() => submit(false)}
              >
                {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Create Order
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  );
}
