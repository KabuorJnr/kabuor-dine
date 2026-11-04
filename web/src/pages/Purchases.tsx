import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { Plus } from 'lucide-react';
import { can, useApi, useSession } from '../lib/api';
import { day, daysBetween, ksh, todayIso } from '../lib/format';
import { useLang } from '../lib/i18n';
import { Chips, Empty, ErrorNote, Loading, PageHeader, SearchBox, Status } from '../components/ui';

export type PurchaseDocRow = {
  id: string;
  kind: 'Order' | 'Bill';
  number: string;
  supplierRef: string;
  status: string;
  issueDate: string;
  dueDate: string;
  total: number;
  paid: number;
  supplier: string;
  supplierId: string;
};

type ListData = {
  counts: Record<string, number>;
  page: { items: PurchaseDocRow[]; total: number };
};

export function PurchaseStatusBadge({ d }: { d: Pick<PurchaseDocRow, 'kind' | 'status' | 'dueDate' | 'total' | 'paid'> }) {
  if (d.status === 'Draft') return <Status tone="plain">Draft</Status>;
  if (d.status === 'Void') return <Status tone="plain">Void</Status>;
  if (d.kind === 'Order') {
    if (d.status === 'Received') return <Status tone="good">Received</Status>;
    return <Status tone="info">Sent</Status>;
  }
  // For Bills:
  if (d.status === 'Paid') return <Status tone="good">Paid</Status>;
  const late = daysBetween(d.dueDate, todayIso());
  if (late > 0) return <Status tone="bad">{late} d late</Status>;
  return (
    <Status tone={d.status === 'PartPaid' ? 'warn' : 'info'}>
      {d.status === 'PartPaid' ? 'Part paid' : late === 0 ? 'Due today' : `Due in ${-late} d`}
    </Status>
  );
}

export function PurchaseList() {
  const session = useSession();
  const { t } = useLang();
  const [params, setParams] = useSearchParams();
  const kind = (params.get('kind') as 'Bill' | 'Order') || 'Bill';
  const status = params.get('status') ?? 'all';
  const [q, setQ] = useState('');
  const [query, setQuery] = useState('');

  useEffect(() => {
    const id = setTimeout(() => setQuery(q), 250);
    return () => clearTimeout(id);
  }, [q]);

  const isBill = kind === 'Bill';
  const { data, error, loading } = useApi<ListData>(
    `/purchases?kind=${kind}&status=${status}&q=${encodeURIComponent(query)}&take=100`,
  );

  const c = data?.counts ?? {};
  const chips: [string, string, number?][] = isBill
    ? [
        ['all', t('All'), c.all],
        ['unpaid', t('Unpaid'), c.unpaid],
        ['overdue', t('Overdue'), c.overdue],
        ['paid', t('Paid'), c.paid],
        ['draft', t('Draft'), c.draft],
      ]
    : [
        ['all', t('All'), c.all],
        ['sent', 'Sent', c.sent],
        ['received', 'Received', c.received],
        ['draft', t('Draft'), c.draft],
      ];

  const rows = data?.page.items ?? [];
  const weOwe = rows.filter((r) => ['Open', 'PartPaid'].includes(r.status)).reduce((s, r) => s + r.total - r.paid, 0);

  return (
    <>
      <PageHeader print
        title={isBill ? t('Bills') : t('Purchase orders')}
        subtitle={
          data &&
          (isBill
            ? `${data.page.total} shown${weOwe > 0 ? ` · ${ksh(weOwe)} still to pay` : ''}`
            : `${data.page.total} shown`)
        }
        actions={
          <div className="flex gap-2">
            <div className="inline-flex rounded-lg bg-paper p-0.5 ring-1 ring-line">
              <button
                type="button"
                onClick={() => setParams({ kind: 'Bill' })}
                className={`rounded-md px-3 py-1.5 text-xs font-semibold ${
                  isBill ? 'bg-white shadow-sm ring-1 ring-line font-bold' : 'text-muted'
                }`}
              >
                {t('Bills')}
              </button>
              <button
                type="button"
                onClick={() => setParams({ kind: 'Order' })}
                className={`rounded-md px-3 py-1.5 text-xs font-semibold ${
                  !isBill ? 'bg-white shadow-sm ring-1 ring-line font-bold' : 'text-muted'
                }`}
              >
                {t('Purchase orders')}
              </button>
            </div>
            {isBill && can(session, 'finance') && (
              <Link to="/purchases/etims" className="btn btn-ghost">
                From suppliers’ eTIMS
              </Link>
            )}
            {isBill
              ? can(session, 'finance') && (
                  <Link to="/purchases/new?kind=Bill" className="btn btn-primary">
                    <Plus className="h-4 w-4" /> {t('New bill')}
                  </Link>
                )
              : can(session, 'buy') && (
                  <Link to="/purchases/new?kind=Order" className="btn btn-primary">
                    <Plus className="h-4 w-4" /> {t('New order')}
                  </Link>
                )}
          </div>
        }
      />

      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips
          label="Filter purchases"
          value={status}
          onChange={(v) => setParams(v === 'all' ? { kind } : { kind, status: v })}
          options={chips}
        />
        <SearchBox
          value={q}
          onChange={setQ}
          placeholder={`Search ${isBill ? 'bill' : 'order'} no., supplier or invoice ref`}
        />
      </div>

      {error && <ErrorNote>{error}</ErrorNote>}
      {loading && !data ? (
        <Loading />
      ) : rows.length === 0 ? (
        <Empty
          title={
            query || status !== 'all'
              ? 'Nothing matches'
              : isBill
              ? 'No bills entered yet'
              : 'No purchase orders yet'
          }
          action={
            !query &&
            status === 'all' && (
              <Link
                to={isBill ? '/purchases/new?kind=Bill' : '/purchases/new?kind=Order'}
                className="btn btn-primary"
              >
                {isBill ? t('New bill') : t('New order')}
              </Link>
            )
          }
        >
          {query || status !== 'all'
            ? 'Try another search or filter.'
            : isBill
            ? 'Enter supplier bills to keep stock costs, inventory balances and Accounts Payable right.'
            : 'Raise purchase orders for goods you need to order from suppliers.'}
        </Empty>
      ) : (
        <div className="card overflow-hidden">
          <ul className="divide-y divide-line sm:hidden">
            {rows.map((r) => (
              <li key={r.id}>
                <Link to={`/purchases/${r.id}`} className="flex items-start gap-3 px-4 py-3 hover:bg-paper">
                  <span className="min-w-0 flex-1">
                    <b className="block truncate">{r.supplier}</b>
                    <span className="text-sm text-muted">
                      {r.number}
                      {r.supplierRef && ` · ${r.supplierRef}`} · {day(r.issueDate)}
                    </span>
                  </span>
                  <span className="text-right">
                    <b className="num block">{ksh(r.status === 'Paid' || !isBill ? r.total : r.total - r.paid)}</b>
                    <PurchaseStatusBadge d={r} />
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
                <th className="px-4 py-2.5 font-semibold">Ref / Invoice</th>
                <th className="px-4 py-2.5 font-semibold">Date</th>
                <th className="px-4 py-2.5 font-semibold">Status</th>
                <th className="px-4 py-2.5 text-right font-semibold">Total</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-line">
              {rows.map((r) => (
                <tr key={r.id} className="hover:bg-paper">
                  <td className="px-4 py-3 font-mono text-xs font-semibold">
                    <Link to={`/purchases/${r.id}`} className="hover:text-brand hover:underline">
                      {r.number}
                    </Link>
                  </td>
                  <td className="px-4 py-3 font-semibold">
                    <Link to={`/purchases/${r.id}`} className="hover:text-brand hover:underline">
                      {r.supplier}
                    </Link>
                  </td>
                  <td className="px-4 py-3 text-muted">{r.supplierRef || '—'}</td>
                  <td className="px-4 py-3 text-muted">{day(r.issueDate)}</td>
                  <td className="px-4 py-3">
                    <PurchaseStatusBadge d={r} />
                  </td>
                  <td className="num px-4 py-3 text-right font-medium">{ksh(r.total)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </>
  );
}
