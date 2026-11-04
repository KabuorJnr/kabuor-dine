import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router';
import { ArrowLeft, Building2, Edit2, Loader2, Phone, Plus, ShoppingBag } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { day, dateTime, ksh, prettyPhone } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Stat, Status, SheetFooter, PageError } from '../components/ui';
import { PurchaseStatusBadge } from './Purchases';

type SupplierRow = {
  id: string;
  number: string;
  name: string;
  phone: string;
  kraPin: string;
  weOwe: number;
  overdue: number;
};

type SupplierDoc = {
  id: string;
  kind: 'Order' | 'Bill';
  number: string;
  supplierRef: string;
  status: string;
  issueDate: string;
  dueDate: string;
  total: number;
  paid: number;
};

type SupplierPayment = {
  id: string;
  method: string;
  amount: number;
  reference: string;
  paidAt: string;
};

function SupplierEditor({
  open,
  onClose,
  initial,
  onSaved,
}: {
  open: boolean;
  onClose: () => void;
  initial?: { id?: string; name: string; phone: string; email: string; kraPin: string; address: string; paymentDetails: string; notes: string };
  onSaved: (s: any) => void;
}) {
  const [f, setF] = useState(
    initial ?? { name: '', phone: '', email: '', kraPin: '', address: '', paymentDetails: '', notes: '' },
  );
  const [state, setState] = useState({ busy: false, error: '' });
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement | HTMLTextAreaElement>) =>
    setF({ ...f, [k]: e.target.value });

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const res = initial?.id
        ? await api(`/suppliers/${initial.id}`, { method: 'PUT', body: f })
        : await api('/suppliers', { body: f });
      onSaved(res);
      onClose();
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };

  return (
    <Sheet open={open} onClose={onClose} title={initial?.id ? 'Edit supplier' : 'New supplier'} description="Their KRA PIN lets you claim the VAT on their bills.">
      <form onSubmit={submit} className="space-y-4">
        <Field label="Supplier name">
          <input className="field" required value={f.name} onChange={set('name')} placeholder="Company or person" />
        </Field>
        <Field label="Phone number" hint="For contact and sending purchase orders on WhatsApp.">
          <input className="field" inputMode="tel" value={f.phone} onChange={set('phone')} placeholder="0712 345 678" />
        </Field>
        <Field label="KRA PIN" hint="Required for KRA eTIMS VAT input tax claims on bills.">
          <input className="field uppercase" value={f.kraPin} onChange={set('kraPin')} placeholder="P051234567X" maxLength={11} />
        </Field>
        <Field label="Email">
          <input className="field" type="email" value={f.email} onChange={set('email')} placeholder="orders@supplier.co.ke" />
        </Field>
        <Field label="Physical address or market location">
          <input className="field" value={f.address} onChange={set('address')} placeholder="e.g. Industrial Area, Enterprise Rd" />
        </Field>
        <Field label="Payment instructions / Account details" hint="e.g. Paybill 123456 Acc: Kabuor, or Bank KCB Acc 11223344">
          <input className="field" value={f.paymentDetails} onChange={set('paymentDetails')} placeholder="Paybill / Till / Bank details" />
        </Field>
        <Field label="Notes">
          <textarea className="field" rows={2} value={f.notes} onChange={set('notes')} placeholder="Terms, contacts, sales rep details" />
        </Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} {initial?.id ? 'Save changes' : 'Add supplier'}
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}

export function SupplierList() {
  const session = useSession();
  const { t } = useLang();
  const [filter, setFilter] = useState('all');
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  const [adding, setAdding] = useState(false);

  useEffect(() => {
    const id = setTimeout(() => setQuery(q), 250);
    return () => clearTimeout(id);
  }, [q]);

  const { data, error, reload } = useApi<{
    counts: { all: number; owed: number; overdue: number };
    items: SupplierRow[];
  }>(`/suppliers?filter=${filter}&q=${encodeURIComponent(query)}`);

  const rows = data?.items ?? [];
  const weOwe = rows.reduce((s, r) => s + r.weOwe, 0);

  return (
    <>
      <PageHeader print
        title={t('Suppliers')}
        subtitle={data && `${rows.length} shown${weOwe > 0 ? ` · ${ksh(weOwe)} total owed` : ''}`}
        actions={
          can(session, 'buy') && (
            <button className="btn btn-primary" onClick={() => setAdding(true)}>
              <Plus className="h-4 w-4" /> {t('New supplier')}
            </button>
          )
        }
      />
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips
          label="Filter suppliers"
          value={filter}
          onChange={setFilter}
          options={[
            ['all', t('All'), data?.counts.all],
            ['owed', t('We owe'), data?.counts.owed],
            ['overdue', t('Overdue'), data?.counts.overdue],
          ]}
        />
        <SearchBox value={q} onChange={setQ} placeholder="Search supplier name, phone, number or PIN" />
      </div>

      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty
          title={query || filter !== 'all' ? 'No suppliers match' : 'No suppliers added yet'}
          action={
            !query && filter === 'all' && can(session, 'buy') && (
              <button className="btn btn-primary" onClick={() => setAdding(true)}>
                {t('New supplier')}
              </button>
            )
          }
        >
          {query || filter !== 'all'
            ? 'Try another search term or filter.'
            : 'Add the vendors, distributors and manufacturers you buy stock and services from.'}
        </Empty>
      ) : (
        <div className="card overflow-hidden">
          <ul className="divide-y divide-line sm:hidden">
            {rows.map((s) => (
              <li key={s.id}>
                <Link to={`/suppliers/${s.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-paper">
                  <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-paper font-semibold text-muted">
                    <Building2 className="h-5 w-5" />
                  </span>
                  <span className="min-w-0 flex-1">
                    <b className="block truncate">{s.name}</b>
                    <span className="text-xs text-muted">
                      {s.number}
                      {s.phone && ` · ${prettyPhone(s.phone)}`}
                    </span>
                  </span>
                  <span className="text-right">
                    {s.weOwe > 0 ? (
                      <>
                        <b className="num block font-semibold text-ink">{ksh(s.weOwe)}</b>
                        {s.overdue > 0 ? (
                          <Status tone="bad">{ksh(s.overdue)} late</Status>
                        ) : (
                          <span className="text-xs text-muted">owed</span>
                        )}
                      </>
                    ) : (
                      <Status tone="good">Clear</Status>
                    )}
                  </span>
                </Link>
              </li>
            ))}
          </ul>
          <table className="hidden w-full text-sm sm:table">
            <thead className="border-b border-line bg-paper text-left text-xs text-muted">
              <tr>
                <th className="px-4 py-2.5 font-semibold">Number</th>
                <th className="px-4 py-2.5 font-semibold">Supplier</th>
                <th className="px-4 py-2.5 font-semibold">Phone</th>
                <th className="px-4 py-2.5 font-semibold">KRA PIN</th>
                <th className="px-4 py-2.5 text-right font-semibold">We owe</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {rows.map((s) => (
                <tr key={s.id} className="hover:bg-paper">
                  <td className="px-4 py-3 font-mono text-xs text-muted">
                    <Link to={`/suppliers/${s.id}`} className="hover:text-brand hover:underline">
                      {s.number}
                    </Link>
                  </td>
                  <td className="px-4 py-3 font-semibold">
                    <Link to={`/suppliers/${s.id}`} className="hover:text-brand hover:underline">
                      {s.name}
                    </Link>
                  </td>
                  <td className="px-4 py-3 text-muted">{s.phone ? prettyPhone(s.phone) : '—'}</td>
                  <td className="px-4 py-3 font-mono text-xs text-muted">{s.kraPin || '—'}</td>
                  <td className="px-4 py-3 text-right">
                    {s.weOwe > 0 ? (
                      <div>
                        <b className="num">{ksh(s.weOwe)}</b>
                        {s.overdue > 0 && <span className="block text-xs text-bad">{ksh(s.overdue)} late</span>}
                      </div>
                    ) : (
                      <span className="text-faint">—</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <SupplierEditor open={adding} onClose={() => setAdding(false)} onSaved={() => reload()} />
    </>
  );
}

export function SupplierView() {
  const { id } = useParams();
  const session = useSession();
  const { t } = useLang();
  const [editing, setEditing] = useState(false);

  const { data, error, reload } = useApi<{
    supplier: {
      id: string;
      number: string;
      name: string;
      phone: string;
      email: string;
      kraPin: string;
      address: string;
      paymentDetails: string;
      notes: string;
    };
    documents: SupplierDoc[];
    payments: SupplierPayment[];
    weOwe: number;
    overdue: number;
    boughtTotal: number;
  }>(id ? `/suppliers/${id}` : null);

  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;

  const { supplier, documents, payments, weOwe, overdue, boughtTotal } = data;
  const openBills = documents.filter((d) => d.kind === 'Bill' && ['Open', 'PartPaid'].includes(d.status));

  return (
    <div className="space-y-6">
      <div>
        <Link to="/suppliers" className="inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
          <ArrowLeft className="h-4 w-4" /> {t('Suppliers')}
        </Link>
        <PageHeader print
          title={supplier.name}
          subtitle={`${supplier.number} · ${supplier.kraPin ? `KRA PIN ${supplier.kraPin}` : 'No KRA PIN'}`}
          actions={
            <div className="flex gap-2">
              {can(session, 'buy') && (
                <button className="btn btn-ghost" onClick={() => setEditing(true)}>
                  <Edit2 className="h-4 w-4" /> Edit
                </button>
              )}
              {can(session, 'finance') && (
                <Link to={`/purchases/new?kind=Bill&supplierId=${supplier.id}`} className="btn btn-primary">
                  <Plus className="h-4 w-4" /> {t('New bill')}
                </Link>
              )}
              {can(session, 'buy') && (
                <Link to={`/purchases/new?kind=Order&supplierId=${supplier.id}`} className="btn btn-ghost">
                  <ShoppingBag className="h-4 w-4" /> {t('New order')}
                </Link>
              )}
            </div>
          }
        />
      </div>

      <div className="grid gap-3 sm:grid-cols-3">
        <Stat label={t('We owe')} value={ksh(weOwe)}>
          {overdue > 0 ? <span className="text-bad font-semibold">{ksh(overdue)} overdue</span> : 'Up to date'}
        </Stat>
        <Stat label="Total bought" value={ksh(boughtTotal)}>
          {(() => {
            const n = documents.filter((d) => d.kind === 'Bill' && d.status !== 'Draft').length;
            return `Across ${n} bill${n === 1 ? '' : 's'}`;
          })()}
        </Stat>
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted">Payment details</p>
          <p className="mt-1 text-sm font-medium">{supplier.paymentDetails || 'No payment instructions specified'}</p>
          {supplier.phone && (
            <p className="mt-2 text-xs text-muted flex items-center gap-1.5">
              <Phone className="h-3.5 w-3.5" /> {prettyPhone(supplier.phone)}
            </p>
          )}
        </div>
      </div>

      {openBills.length > 0 && (
        <section className="card p-4 sm:p-5">
          <h2 className="text-sm font-semibold text-ink">Unpaid bills ({openBills.length})</h2>
          <ul className="mt-3 divide-y divide-line">
            {openBills.map((b) => (
              <li key={b.id} className="flex items-center justify-between gap-3 py-3">
                <Link to={`/purchases/${b.id}`} className="min-w-0 flex-1 hover:text-brand">
                  <b className="block truncate text-sm">
                    {b.number} {b.supplierRef && <span className="text-muted font-normal">({b.supplierRef})</span>}
                  </b>
                  <span className="text-xs text-muted">
                    Due {day(b.dueDate)} · Total {ksh(b.total)}
                  </span>
                </Link>
                <div className="text-right">
                  <b className="num block font-semibold text-bad">{ksh(b.total - b.paid)}</b>
                  <Link to={`/purchases/${b.id}`} className="text-xs font-semibold text-brand hover:underline">
                    View & Pay →
                  </Link>
                </div>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="card overflow-hidden">
        <div className="border-b border-line px-4 py-3">
          <h2 className="text-sm font-semibold text-ink">Documents ({documents.length})</h2>
        </div>
        {documents.length === 0 ? (
          <p className="px-4 py-8 text-center text-sm text-muted">No orders or bills created for this supplier yet.</p>
        ) : (
          <table className="w-full text-sm">
            <thead className="border-b border-line bg-paper text-left text-xs text-muted">
              <tr>
                <th className="px-4 py-2 font-semibold">Number</th>
                <th className="px-4 py-2 font-semibold">Kind</th>
                <th className="px-4 py-2 font-semibold">Date</th>
                <th className="px-4 py-2 font-semibold">Status</th>
                <th className="px-4 py-2 text-right font-semibold">Total</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {documents.map((d) => (
                <tr key={d.id} className="hover:bg-paper">
                  <td className="px-4 py-2.5 font-mono text-xs">
                    <Link to={`/purchases/${d.id}`} className="hover:text-brand hover:underline">
                      {d.number}
                    </Link>
                  </td>
                  <td className="px-4 py-2.5 text-xs text-muted">{d.kind}</td>
                  <td className="px-4 py-2.5 text-xs text-muted">{day(d.issueDate)}</td>
                  <td className="px-4 py-2.5">
                    <PurchaseStatusBadge d={d} />
                  </td>
                  <td className="num px-4 py-2.5 text-right font-medium">{ksh(d.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      {payments.length > 0 && (
        <section className="card p-4 sm:p-5">
          <h2 className="text-sm font-semibold text-ink">Payments made ({payments.length})</h2>
          <ul className="mt-3 divide-y divide-line">
            {payments.map((p) => (
              <li key={p.id} className="flex items-center justify-between py-2.5 text-sm">
                <div>
                  <b className="block text-ink">{p.method === 'Mpesa' ? 'M-Pesa' : p.method}</b>
                  <span className="text-xs text-muted">
                    {p.reference ? `${p.reference} · ` : ''}
                    {dateTime(p.paidAt)}
                  </span>
                </div>
                <b className="num text-good">{ksh(p.amount)}</b>
              </li>
            ))}
          </ul>
        </section>
      )}

      <SupplierEditor
        open={editing}
        onClose={() => setEditing(false)}
        initial={supplier}
        onSaved={() => reload()}
      />
    </div>
  );
}
