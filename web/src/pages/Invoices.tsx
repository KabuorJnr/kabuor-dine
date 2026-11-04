import { useEffect, useState } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { Plus } from 'lucide-react';
import { can, useApi, useSession } from '../lib/api';
import { daysBetween, day, ksh, todayIso, money } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Chips, Empty, ErrorNote, Loading, PageHeader, SearchBox, Status } from '../components/ui';

export type DocRow = { id: string; kind: 'Invoice' | 'Quote'; number: string; status: string; issueDate: string; dueDate: string; total: number; paid: number; currency?: string; fxTotal?: number; fxPaid?: number; etimsStatus: string; customer: string; customerId: string };
type ListData = { counts: Record<string, number>; page: { items: DocRow[]; total: number } };

/** Status in plain words, with how late an unpaid invoice is. */
export function DocStatus({ d }: { d: Pick<DocRow, 'kind' | 'status' | 'dueDate' | 'total' | 'paid'> }) {
  const late = daysBetween(d.dueDate, todayIso());
  if (d.status === 'Draft') return <Status tone="plain">Draft</Status>;
  if (d.status === 'Void') return <Status tone="plain">Void</Status>;
  if (d.kind === 'Quote') return d.status === 'Accepted' ? <Status tone="good">Accepted</Status> : <Status tone="info">Sent</Status>;
  if (d.status === 'Paid') return <Status tone="good">Paid</Status>;
  if (late > 0) return <Status tone="bad">{late} day{late === 1 ? '' : 's'} late</Status>;
  return <Status tone={d.status === 'PartPaid' ? 'warn' : 'info'}>{d.status === 'PartPaid' ? 'Part paid' : late === 0 ? 'Due today' : `Due in ${-late} d`}</Status>;
}

export function InvoiceList({ kind }: { kind: 'Invoice' | 'Quote' }) {
  const session = useSession();
  const { t } = useLang();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const status = params.get('status') ?? 'all';
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');
  useEffect(() => {
    const id = setTimeout(() => setQuery(q), 250);
    return () => clearTimeout(id);
  }, [q]);
  const { data, error, loading } = useApi<ListData>(`/sales?kind=${kind}&status=${status}&q=${encodeURIComponent(query)}&take=100`);
  const invoice = kind === 'Invoice';
  const newPath = invoice ? '/invoices/new' : '/quotes/new';
  const c = data?.counts ?? {};
  const chips: [string, string, number?][] = invoice
    ? [['all', t('All'), c.all], ['unpaid', t('Unpaid'), c.unpaid], ['overdue', t('Overdue'), c.overdue], ['paid', t('Paid'), c.paid], ['draft', t('Draft'), c.draft]]
    : [['all', t('All'), c.all], ['draft', t('Draft'), c.draft]];
  const rows = data?.page.items ?? [];
  const owed = rows.filter((r) => r.status === 'Sent' || r.status === 'PartPaid').reduce((s, r) => s + r.total - r.paid, 0);

  return (
    <>
      <PageHeader print
        title={invoice ? t('Invoices') : t('Quotes')}
        subtitle={data && (invoice ? `${data.page.total} shown${owed ? ` · ${ksh(owed)} still to collect` : ''}` : `${data.page.total} shown`)}
        actions={
          can(session, 'sell') && (
            <Link to={newPath} className="btn btn-primary">
              <Plus className="h-4 w-4" /> {invoice ? t('New invoice') : t('New quote')}
            </Link>
          )
        }
      />
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips label="Filter" value={status} onChange={(v) => setParams(v === 'all' ? {} : { status: v })} options={chips} />
        <SearchBox value={q} onChange={setQ} placeholder={`Search ${invoice ? 'invoice' : 'quote'} no., customer or phone`} />
      </div>
      {error && <ErrorNote>{error}</ErrorNote>}
      {loading && !data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty
          title={query || status !== 'all' ? 'Nothing matches' : invoice ? 'No invoices yet' : 'No quotes yet'}
          action={!query && status === 'all' && can(session, 'sell') && <Link to={newPath} className="btn btn-primary">{invoice ? t('New invoice') : t('New quote')}</Link>}
        >
          {query || status !== 'all' ? 'Try another filter or search.' : invoice ? 'Make your first invoice: it goes to KRA eTIMS and your customer can pay it by M-Pesa.' : 'Send a quote; when the customer agrees, turn it into an invoice with one tap.'}
        </Empty>
      ) : (
        <div className="card overflow-hidden">
          {/* Phone: one card per row. Computer: a table. Same facts in the same places. */}
          <ul className="divide-y divide-line sm:hidden">
            {rows.map((r) => (
              <li key={r.id}>
                <Link to={`/sales/${r.id}`} className="flex items-start gap-3 px-4 py-3">
                  <span className="min-w-0 flex-1">
                    <b className="block truncate">{r.customer}</b>
                    <span className="text-sm text-muted">
                      {r.number} · {day(r.issueDate)}
                    </span>
                  </span>
                  <span className="text-right">
                    <b className="num block">{r.currency ? money(r.status === 'Paid' || !invoice ? r.fxTotal : r.fxTotal! - r.fxPaid!, r.currency) : ksh(r.status === 'Paid' || !invoice ? r.total : r.total - r.paid)}</b>
                    <DocStatus d={r} />
                  </span>
                </Link>
              </li>
            ))}
          </ul>
          <table className="hidden w-full text-sm sm:table">
            <thead className="border-b border-line bg-paper text-left text-xs text-muted">
              <tr>
                <th className="px-4 py-2.5 font-semibold">Number</th>
                <th className="px-4 py-2.5 font-semibold">Customer</th>
                <th className="px-4 py-2.5 font-semibold">Date</th>
                <th className="px-4 py-2.5 font-semibold">Status</th>
                <th className="px-4 py-2.5 text-right font-semibold">Total</th>
                {invoice && <th className="px-4 py-2.5 text-right font-semibold">Still to pay</th>}
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {rows.map((r) => (
                <tr key={r.id} onClick={() => navigate(`/sales/${r.id}`)} className="cursor-pointer hover:bg-paper">
                  <td className="px-4 py-3 font-semibold">
                    <Link to={`/sales/${r.id}`} onClick={(e) => e.stopPropagation()} className="hover:text-brand">
                      {r.number}
                    </Link>
                    {invoice && r.etimsStatus === 'Failed' && <span className="ml-2 text-xs font-semibold text-warn" title="KRA hasn't signed this invoice yet; open it to send it again">eTIMS failed</span>}
                  </td>
                  <td className="max-w-[16rem] truncate px-4 py-3">{r.customer}</td>
                  <td className="px-4 py-3 text-muted">{day(r.issueDate)}</td>
                  <td className="px-4 py-3">
                    <DocStatus d={r} />
                  </td>
                  <td className="num px-4 py-3 text-right">{money(r.currency ? r.fxTotal : r.total, r.currency)}</td>
                  {/* Only what is actually owed stands out; paid, void and draft rows show a quiet dash, the same way. */}
                  {invoice && <td className="num px-4 py-3 text-right font-semibold">{r.status === 'Void' || r.status === 'Draft' || r.total - r.paid <= 0 ? <span className="font-normal text-faint">—</span> : money(r.currency ? r.fxTotal! - r.fxPaid! : r.total - r.paid, r.currency)}</td>}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
