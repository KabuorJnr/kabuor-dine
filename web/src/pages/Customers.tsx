import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import { ArrowLeft, Loader2, MessageCircle, Phone, Plus } from 'lucide-react';
import { api, can, useApi, useSession } from '../lib/api';
import { CURRENCIES, dateTime, day, ksh, msisdn, prettyPhone } from '../lib/format';
import { useLang } from '../lib/i18n';
import { DocStatus } from './Invoices';
import { CustomerTerms } from '../components/Trade';
import { CustomerJourney, CustomerProfile } from '../components/CustomerIntel';
import DataRights from '../components/DataRights';
import { ActivityList, LogActivity } from './Pipeline';
import { Chips, Empty, ErrorNote, Field, Loading, PageHeader, SearchBox, Sheet, Stat, SheetFooter, PageError } from '../components/ui';

type Row = { id: string; number: string; name: string; phone: string; email: string; owes: number; overdue: number };

function NewCustomer({ open, onClose }: { open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const [f, setF] = useState({ name: '', phone: '', email: '', kraPin: '', address: '', currency: '' });
  const [state, setState] = useState({ busy: false, error: '' });
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF({ ...f, [k]: e.target.value });
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setState({ busy: true, error: '' });
    try {
      const c = await api('/customers', { body: f });
      navigate(`/customers/${c.id}`);
    } catch (err) {
      setState({ busy: false, error: (err as Error).message });
    }
  };
  return (
    <Sheet open={open} onClose={onClose} title="New customer" description="Only the name is needed. Add a phone to send invoices and reminders on WhatsApp.">
      <form onSubmit={submit} className="space-y-4">
        <Field label="Name">
          <input className="field" required value={f.name} onChange={set('name')} placeholder="Business or person" />
        </Field>
        <Field label="Phone" hint="Used to match their M-Pesa payments and to send invoices on WhatsApp.">
          <input className="field" inputMode="tel" value={f.phone} onChange={set('phone')} placeholder="0712 345 678" />
        </Field>
        <Field label="KRA PIN" hint="For businesses that claim VAT: it goes on their eTIMS invoices.">
          <input className="field uppercase" value={f.kraPin} onChange={set('kraPin')} placeholder="P051234567X" maxLength={11} />
        </Field>
        <Field label="Email">
          <input className="field" type="email" value={f.email} onChange={set('email')} />
        </Field>
        <Field label="Address or location">
          <input className="field" value={f.address} onChange={set('address')} placeholder="e.g. Tom Mboya St, Nairobi" />
        </Field>
        <Field label="Invoice them in" hint="For customers abroad. Your books stay in shillings.">
          <select className="field" value={f.currency} onChange={(e) => setF({ ...f, currency: e.target.value })}>
            <option value="">Kenya shillings (KES)</option>
            {CURRENCIES.map((c) => <option key={c} value={c}>{c}</option>)}
          </select>
        </Field>
        {state.error && <ErrorNote>{state.error}</ErrorNote>}
        <SheetFooter>
        <button className="btn btn-primary" disabled={state.busy}>
          {state.busy && <Loader2 className="h-4 w-4 animate-spin" />} Add customer
        </button>
        </SheetFooter>
      </form>
    </Sheet>
  );
}

export function CustomerList() {
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
  const { data, error } = useApi<{ counts: { all: number; owing: number; overdue: number }; page: { items: Row[]; total: number } }>(`/customers?filter=${filter}&q=${encodeURIComponent(query)}&take=200`);
  const rows = data?.page.items ?? [];
  const owed = rows.reduce((s, r) => s + r.owes, 0);

  return (
    <>
      <PageHeader print
        title={t('Customers')}
        subtitle={data && `${data.page.total} shown${owed ? ` · ${ksh(owed)} owed to you` : ''}`}
        actions={
          can(session, 'sell') && (
            <button className="btn btn-primary" onClick={() => setAdding(true)}>
              <Plus className="h-4 w-4" /> {t('New customer')}
            </button>
          )
        }
      />
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips label="Filter customers" value={filter} onChange={setFilter} options={[['all', t('All'), data?.counts.all], ['owing', t('Owing'), data?.counts.owing], ['overdue', t('Overdue'), data?.counts.overdue]]} />
        <SearchBox value={q} onChange={setQ} placeholder="Search name, phone, number or KRA PIN" />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {!data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty title={query || filter !== 'all' ? 'Nobody matches' : 'No customers yet'} action={!query && filter === 'all' && can(session, 'sell') && <button className="btn btn-primary" onClick={() => setAdding(true)}>{t('New customer')}</button>}>
          {query || filter !== 'all' ? 'Try another filter or search.' : 'Add the people and businesses you sell to. Their phone number lets M-Pesa payments match by themselves.'}
        </Empty>
      ) : (
        <ul className="card divide-y divide-line">
          {rows.map((c) => (
            <li key={c.id}>
              <Link to={`/customers/${c.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-paper">
                <span className="grid h-10 w-10 shrink-0 place-items-center rounded-full bg-brand-soft font-semibold text-brand">{c.name.slice(0, 1).toUpperCase()}</span>
                <span className="min-w-0 flex-1">
                  <b className="block truncate">{c.name}</b>
                  <span className="text-sm text-muted">
                    {c.number}
                    {c.phone && ` · ${prettyPhone(c.phone)}`}
                  </span>
                </span>
                <span className="text-right">
                  {c.owes > 0 ? <b className="num block">{ksh(c.owes)}</b> : <span className="text-sm text-faint">Nothing owed</span>}
                  {c.overdue > 0 && <span className="num text-xs font-semibold text-bad">{ksh(c.overdue)} late</span>}
                </span>
              </Link>
            </li>
          ))}
        </ul>
      )}
      {adding && <NewCustomer open={adding} onClose={() => setAdding(false)} />}
    </>
  );
}

function Conversation({ customerId }: { customerId: string }) {
  const { data, reload } = useApi<any[]>(`/crm/activities?customerId=${customerId}`);
  return (
    <section className="card p-4 sm:p-5">
      <h2 className="mb-3 text-sm font-semibold text-muted">Conversation</h2>
      <LogActivity customerId={customerId} onDone={reload} />
      <div className="mt-5 border-t border-line pt-4">{data ? <ActivityList items={data} onChange={reload} /> : null}</div>
    </section>
  );
}

function MarketingConsent({ customer, onChange }: { customer: any; onChange: () => void }) {
  const [busy, setBusy] = useState(false);
  const toggle = async () => {
    setBusy(true);
    try {
      await api(`/customers/${customer.id}`, { method: 'PUT', body: { ...customer, marketingOptOut: !customer.marketingOptOut } });
      onChange();
    } finally {
      setBusy(false);
    }
  };
  return (
    <label className="flex cursor-pointer items-start gap-2.5 text-sm">
      <input type="checkbox" className="mt-0.5 h-4 w-4 accent-[var(--color-brand)]" checked={!customer.marketingOptOut} disabled={busy} onChange={toggle} />
      <span>
        <b className="block">OK to send offers</b>
        <span className="text-xs text-muted">{customer.marketingOptOut ? 'They asked not to get marketing messages. Campaigns skip them.' : 'Untick if they ask to stop. Invoices and receipts still go to them.'}</span>
      </span>
    </label>
  );
}

export function CustomerView() {
  const { id } = useParams();
  const session = useSession();
  const { data, error, reload } = useApi<any>(`/customers/${id}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.customer;
  const wa = msisdn(c.phone);
  const statement = `Hello ${c.name}, your balance with us is ${ksh(data.owes)}${data.overdue ? `, of which ${ksh(data.overdue)} is overdue` : ''}. Thank you for your business.`;

  return (
    <>
      <Link to="/customers" className="mb-3 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink">
        <ArrowLeft className="h-4 w-4" /> Customers
      </Link>
      <PageHeader print
        title={c.name}
        subtitle={[c.number, c.phone && prettyPhone(c.phone), c.kraPin && `PIN ${c.kraPin}`].filter(Boolean).join(' · ')}
        actions={
          <>
            {c.phone && (
              <a className="btn btn-ghost" href={`tel:+${wa ?? c.phone}`}>
                <Phone className="h-4 w-4" /> Call
              </a>
            )}
            {wa && data.owes > 0 && (
              <a className="btn btn-mpesa" href={`https://wa.me/${wa}?text=${encodeURIComponent(statement)}`} target="_blank" rel="noreferrer">
                <MessageCircle className="h-4 w-4" /> Send balance
              </a>
            )}
          </>
        }
      />
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <Stat label="Owes you" value={ksh(data.owes)} tone={data.overdue > 0 ? 'bad' : undefined}>
          {data.overdue > 0 ? <span className="text-bad">{ksh(data.overdue)} overdue</span> : 'Nothing overdue'}
        </Stat>
        <Stat label="Paid but not matched" value={ksh(data.credit)}>
          {data.credit > 0 ? 'Will apply to their next invoice' : 'No spare money'}
        </Stat>
        <Stat label="Bought in total" value={ksh(data.lifetime)}>
          {data.documents.filter((d: any) => d.kind === 'Invoice').length} invoices
        </Stat>
        <Stat label="Customer since" value={day(c.createdAt)} />
      </div>

      <div className="mt-5 grid gap-5 lg:grid-cols-[1fr_20rem]">
        <section className="card overflow-hidden">
          <h2 className="border-b border-line px-4 py-3 text-sm font-semibold text-muted">Invoices and quotes</h2>
          {data.documents.length ? (
            <ul className="divide-y divide-line">
              {data.documents.map((d: any) => (
                <li key={d.id}>
                  <Link to={`/sales/${d.id}`} className="flex items-center gap-3 px-4 py-3 hover:bg-paper">
                    <span className="min-w-0 flex-1">
                      <b className="block">{d.number}</b>
                      <span className="text-sm text-muted">{day(d.issueDate)}</span>
                    </span>
                    <span className="text-right">
                      <b className="num block">{ksh(d.total)}</b>
                      <DocStatus d={d} />
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          ) : (
            <p className="p-4 text-sm text-muted">No invoices yet.</p>
          )}
        </section>
        <div className="space-y-5">
        <CustomerProfile id={c.id} />
        <CustomerTerms customer={c} owes={data.owes} onChange={reload} />
        <CustomerJourney id={c.id} />
        <section className="card h-fit p-4">
          <h2 className="text-sm font-semibold text-muted">Payments</h2>
          {data.payments.length ? (
            <ul className="mt-2 space-y-2.5 text-sm">
              {data.payments.map((p: any) => (
                <li key={p.id} className="flex justify-between gap-2">
                  <span>
                    <b>{p.method === 'Mpesa' ? 'M-Pesa' : p.method}</b>
                    <span className="block text-xs text-muted">
                      {p.reference && `${p.reference} · `}
                      {dateTime(p.receivedAt)}
                    </span>
                  </span>
                  <span className="num font-semibold">{ksh(p.amount)}</span>
                </li>
              ))}
            </ul>
          ) : (
            <p className="mt-2 text-sm text-muted">No payments yet.</p>
          )}
        </section>
        </div>
      </div>
      {can(session, 'crm') && (
      <div className="mt-5 grid gap-5 lg:grid-cols-[1fr_20rem]">
        <Conversation customerId={c.id} />
        <aside className="card h-fit p-4">
          <MarketingConsent customer={c} onChange={reload} />
          {can(session, 'admin') && <DataRights customer={c} onErased={reload} />}
        </aside>
      </div>
      )}
    </>
  );
}
