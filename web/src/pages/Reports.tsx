import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { CheckCircle2, AlertTriangle, CalendarRange, FileSpreadsheet, Loader2 } from 'lucide-react';
import { download, useApi } from '../lib/api';
import { Download } from 'lucide-react';
import { cn, dayYear, ksh, todayIso } from '../lib/format';
import { Chips, Loading, PageHeader, PageError } from '../components/ui';

function periodOf(p: string): [string, string, string] {
  const t = todayIso();
  const [y, m] = t.split('-').map(Number);
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  if (p === 'last-month') return [iso(new Date(Date.UTC(y, m - 2, 1))), iso(new Date(Date.UTC(y, m - 1, 0))), 'last month'];
  if (p === 'year') return [`${y}-01-01`, t, `this year`];
  return [iso(new Date(Date.UTC(y, m - 1, 1))), t, 'this month'];
}

function Delta({ now, before, invert }: { now: number; before: number; invert?: boolean }) {
  if (!before) return null;
  const pct = Math.round(((now - before) / Math.abs(before)) * 100);
  const good = invert ? pct <= 0 : pct >= 0;
  return <span className={cn('text-xs font-semibold', good ? 'text-good' : 'text-bad')}>{pct >= 0 ? '▲' : '▼'} {Math.abs(pct)}%</span>;
}

function ProfitAndLoss({ period }: { period: string }) {
  const [from, to] = periodOf(period);
  const { data, error } = useApi<any>(`/reports/profit-and-loss?from=${from}&to=${to}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const c = data.current;
  const p = data.previous;
  const rows = (list: any[], prev: any[]) =>
    list.map((r) => (
      <tr key={r.code}>
        <td className="py-2">{r.name}</td>
        <td className="num py-2 text-right">{ksh(r.amount)}</td>
        <td className="num py-2 text-right text-muted">{ksh(prev.find((x) => x.code === r.code)?.amount ?? 0)}</td>
      </tr>
    ));
  return (
    <div className="grid gap-4 lg:grid-cols-[1fr_18rem]">
      <section className="card overflow-x-auto p-4 sm:p-5">
        <table className="w-full min-w-[26rem] text-sm">
          <thead className="text-left text-xs text-muted">
            <tr>
              <th className="pb-2 font-semibold">
                {dayYear(c.from)} – {dayYear(c.to)}
              </th>
              <th className="pb-2 text-right font-semibold">This period</th>
              <th className="pb-2 text-right font-semibold">Period before</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-line">
            <tr>
              <td colSpan={3} className="pt-3 pb-1 text-xs font-semibold text-muted uppercase">
                Money earned
              </td>
            </tr>
            {c.income.length ? rows(c.income, p.income) : <tr><td colSpan={3} className="py-2 text-muted">No sales in this period.</td></tr>}
            <tr className="font-semibold">
              <td className="py-2">Total earned</td>
              <td className="num py-2 text-right">{ksh(c.totalIncome)}</td>
              <td className="num py-2 text-right text-muted">{ksh(p.totalIncome)}</td>
            </tr>
            <tr>
              <td colSpan={3} className="pt-4 pb-1 text-xs font-semibold text-muted uppercase">
                Money spent
              </td>
            </tr>
            {c.expenses.length ? rows(c.expenses, p.expenses) : <tr><td colSpan={3} className="py-2 text-muted">No expenses in this period. Expense bills and approved payroll show here; stock you buy counts when it sells.</td></tr>}
            <tr className="font-semibold">
              <td className="py-2">Total spent</td>
              <td className="num py-2 text-right">{ksh(c.totalExpenses)}</td>
              <td className="num py-2 text-right text-muted">{ksh(p.totalExpenses)}</td>
            </tr>
          </tbody>
        </table>
      </section>
      <aside className="card h-fit p-5">
        <p className="text-sm text-muted">Profit</p>
        <p className={cn('num mt-1 font-display text-3xl font-semibold', c.profit < 0 && 'text-bad')}>{ksh(c.profit)}</p>
        <p className="mt-1 text-xs text-muted">
          {ksh(p.profit)} the period before <Delta now={c.profit} before={p.profit} />
        </p>
        <p className="mt-4 text-xs text-muted">Sales are counted without VAT (VAT belongs to KRA).</p>
      </aside>
    </div>
  );
}

function Aging() {
  const { data, error } = useApi<any>('/reports/aging');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  if (!data.rows.length) return <p className="card p-5 text-sm text-muted">Nobody owes you anything right now.</p>;
  return (
    <section className="card overflow-x-auto">
      <table className="w-full min-w-[44rem] text-sm">
        <thead className="border-b border-line bg-paper text-left text-xs text-muted">
          <tr>
            <th className="px-4 py-2.5 font-semibold">Customer</th>
            {data.labels.map((l: string, i: number) => (
              <th key={l} className={cn('px-3 py-2.5 text-right font-semibold', i >= 3 && 'text-bad')}>
                {l}
              </th>
            ))}
            <th className="px-4 py-2.5 text-right font-semibold">Total</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-line">
          {data.rows.map((r: any) => (
            <tr key={r.customerId} className="hover:bg-paper">
              <td className="px-4 py-2.5">
                <Link to={`/customers/${r.customerId}`} className="font-semibold hover:text-brand">
                  {r.customer}
                </Link>
              </td>
              {r.buckets.map((b: number, i: number) => (
                <td key={i} className={cn('num px-3 py-2.5 text-right', !b && 'text-faint', b > 0 && i >= 3 && 'font-semibold text-bad')}>
                  {b ? ksh(b) : '—'}
                </td>
              ))}
              <td className="num px-4 py-2.5 text-right font-semibold">{ksh(r.total)}</td>
            </tr>
          ))}
        </tbody>
        <tfoot className="border-t border-line bg-paper font-semibold">
          <tr>
            <td className="px-4 py-2.5">Everyone</td>
            {data.totals.map((t: number, i: number) => (
              <td key={i} className="num px-3 py-2.5 text-right">
                {t ? ksh(t) : '—'}
              </td>
            ))}
            <td className="num px-4 py-2.5 text-right">{ksh(data.total)}</td>
          </tr>
        </tfoot>
      </table>
    </section>
  );
}

function Vat({ period }: { period: string }) {
  const [from, to, label] = periodOf(period);
  const { data, error } = useApi<any>(`/reports/vat?from=${from}&to=${to}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="grid gap-4 lg:grid-cols-[1fr_18rem]">
      <section className="card overflow-x-auto p-4 sm:p-5">
        <table className="w-full min-w-[26rem] text-sm">
          <thead className="text-left text-xs text-muted">
            <tr>
              <th className="pb-2 font-semibold">Tax type</th>
              <th className="pb-2 text-right font-semibold">Sales before VAT</th>
              <th className="pb-2 text-right font-semibold">VAT</th>
              <th className="pb-2 text-right font-semibold">Total</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-line">
            {data.byType.length ? (
              data.byType.map((r: any) => (
                <tr key={r.type}>
                  <td className="py-2">{r.label}</td>
                  <td className="num py-2 text-right">{ksh(r.net)}</td>
                  <td className="num py-2 text-right">{ksh(r.tax)}</td>
                  <td className="num py-2 text-right">{ksh(r.total)}</td>
                </tr>
              ))
            ) : (
              <tr>
                <td colSpan={4} className="py-3 text-muted">
                  No invoices issued {label}.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </section>
      <aside className="card h-fit p-5">
        <p className="text-sm text-muted">VAT on sales, {label}</p>
        <p className="num mt-1 font-display text-3xl font-semibold">{ksh(data.totalTax)}</p>
        <dl className="mt-3 space-y-1.5 text-sm">
          <div className="flex justify-between"><dt className="text-muted">VAT on purchases (claimable)</dt><dd className="num">−{ksh(data.inputTax)}</dd></div>
          {data.vatWithheld > 0 && <div className="flex justify-between"><dt className="text-muted">Withheld by customers (certificates)</dt><dd className="num">−{ksh(data.vatWithheld)}</dd></div>}
          <div className="flex justify-between border-t border-line pt-1.5 font-semibold"><dt>{data.payable >= 0 ? 'To pay KRA' : 'Credit to carry forward'}</dt><dd className="num">{ksh(Math.abs(data.payable))}</dd></div>
        </dl>
        {data.billsMissingSupplierInvoiceNo?.length > 0 && <p className="mt-2 text-xs text-warn">{data.billsMissingSupplierInvoiceNo.length} bill(s) have no supplier invoice number, so their VAT can’t be claimed: {data.billsMissingSupplierInvoiceNo.join(', ')}.</p>}
        <p className="mt-3 text-xs font-semibold text-muted">For the iTax VAT3 return</p>
        <div className="mt-1.5 flex flex-wrap gap-1.5">
          {[
            [`/reports/itax/sales.csv?from=${from}&to=${to}`, 'Sales 16%'],
            [`/reports/itax/sales.csv?from=${from}&to=${to}&rate=8`, 'Sales 8%'],
            [`/reports/itax/purchases.csv?from=${from}&to=${to}`, 'Purchases'],
          ].map(([path, name]) => (
            <button key={path} onClick={() => download(path, `${name}.csv`)} className="btn btn-ghost py-1 text-xs">
              <Download className="h-3.5 w-3.5" /> {name}
            </button>
          ))}
        </div>
        <p className="mt-2 text-xs text-muted">Columns follow KRA’s VAT3 template: paste each into its sheet. Returns are due by the 20th of the following month.</p>
      </aside>
    </div>
  );
}

function TrialBalance() {
  const { data, error } = useApi<any>('/reports/trial-balance');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <section className="card overflow-x-auto p-4 sm:p-5">
      <p className={cn('mb-3 flex items-center gap-2 text-sm font-semibold', data.balanced ? 'text-good' : 'text-bad')}>
        {data.balanced ? <CheckCircle2 className="h-4 w-4" /> : <AlertTriangle className="h-4 w-4" />}
        {data.balanced ? 'The books balance.' : 'The books don’t balance. Contact support.'}
      </p>
      <table className="w-full min-w-[26rem] text-sm">
        <thead className="text-left text-xs text-muted">
          <tr>
            <th className="pb-2 font-semibold">Account</th>
            <th className="pb-2 text-right font-semibold">Debit</th>
            <th className="pb-2 text-right font-semibold">Credit</th>
          </tr>
        </thead>
        <tbody className="divide-y divide-line">
          {data.accounts.map((a: any) => (
            <tr key={a.id}>
              <td className="py-2">
                <span className="text-muted">{a.code}</span> {a.name}
              </td>
              <td className="num py-2 text-right">{a.debit ? ksh(a.debit) : ''}</td>
              <td className="num py-2 text-right">{a.credit ? ksh(a.credit) : ''}</td>
            </tr>
          ))}
        </tbody>
        <tfoot className="border-t border-line font-semibold">
          <tr>
            <td className="py-2">Total</td>
            <td className="num py-2 text-right">{ksh(data.debit)}</td>
            <td className="num py-2 text-right">{ksh(data.credit)}</td>
          </tr>
        </tfoot>
      </table>
    </section>
  );
}

function BalanceSheet() {
  const { data, error } = useApi<any>('/reports/balance-sheet');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const block = (title: string, lines: any[], total: number, extra?: [string, number]) => (
    <section className="card p-4 sm:p-5">
      <h2 className="text-sm font-semibold text-muted">{title}</h2>
      <ul className="mt-2 divide-y divide-line text-sm">
        {lines.map((l: any) => (
          <li key={l.code} className="flex justify-between gap-3 py-1.5"><span className="min-w-0 truncate">{l.name}</span><span className="num shrink-0">{ksh(l.amount)}</span></li>
        ))}
        {extra && <li className="flex justify-between gap-3 py-1.5"><span>{extra[0]}</span><span className="num">{ksh(extra[1])}</span></li>}
      </ul>
      <p className="mt-2 flex justify-between border-t border-line pt-2 font-semibold"><span>Total</span><span className="num">{ksh(total)}</span></p>
    </section>
  );
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">As at {new Date(`${data.at}T00:00:00`).toLocaleDateString('en-GB', { day: 'numeric', month: 'long', year: 'numeric' })}. {data.balanced ? 'What you own equals what you owe plus what’s yours.' : <b className="text-bad">Doesn’t balance: tell support.</b>}</p>
      <div className="grid gap-4 lg:grid-cols-2">
        {block('What the business owns', data.assets.lines, data.assets.total)}
        <div className="space-y-4">
          {block('What it owes', data.liabilities.lines, data.liabilities.total)}
          {block('What’s yours (equity)', data.equity.lines, data.equity.total, ['Profit so far', data.equity.profitSoFar])}
        </div>
      </div>
    </div>
  );
}

function CashFlow({ period }: { period: string }) {
  const [from, to, label] = periodOf(period);
  const { data, error } = useApi<any>(`/reports/cash-flow?from=${from}&to=${to}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="grid gap-4 lg:grid-cols-[1fr_18rem]">
      <section className="card overflow-x-auto p-4 sm:p-5">
        <table className="w-full min-w-[26rem] text-sm">
          <thead className="text-left text-xs text-muted">
            <tr><th className="pb-2 font-semibold">Money {label}</th><th className="pb-2 text-right font-semibold">In</th><th className="pb-2 text-right font-semibold">Out</th></tr>
          </thead>
          <tbody className="divide-y divide-line">
            {data.rows.length ? data.rows.map((r: any) => (
              <tr key={r.category}><td className="py-2">{r.category}</td><td className="num py-2 text-right">{r.moneyIn ? ksh(r.moneyIn) : ''}</td><td className="num py-2 text-right">{r.moneyOut ? ksh(r.moneyOut) : ''}</td></tr>
            )) : <tr><td colSpan={3} className="py-3 text-muted">No money moved {label}.</td></tr>}
          </tbody>
        </table>
      </section>
      <aside className="card h-fit space-y-1.5 p-5 text-sm">
        <p className="flex justify-between"><span className="text-muted">At the start</span><span className="num">{ksh(data.opening)}</span></p>
        <p className="flex justify-between"><span className="text-muted">Came in</span><span className="num text-good">+{ksh(data.moneyIn)}</span></p>
        <p className="flex justify-between"><span className="text-muted">Went out</span><span className="num text-bad">−{ksh(data.moneyOut)}</span></p>
        <p className="flex justify-between border-t border-line pt-1.5 font-semibold"><span>At the end</span><span className="num">{ksh(data.closing)}</span></p>
        <p className="pt-1 text-xs text-muted">M-Pesa, bank and cash together.</p>
      </aside>
    </div>
  );
}

// The file each tab downloads as, and whether it covers a period or stands at a date.
const EXPORTS: Record<string, [string, string, boolean]> = {
  pnl: ['profit-and-loss', 'Profit and loss', true],
  bs: ['balance-sheet', 'Balance sheet', false],
  cf: ['cash-flow', 'Cash flow', true],
  aging: ['aging', 'Customers owe', false],
  vat: ['vat', 'VAT', true],
  tb: ['trial-balance', 'Trial balance', false],
};

function ExportButtons({ tab, period }: { tab: string; period: string }) {
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [report, label, periodic] = EXPORTS[tab];
  // Statements at a date (balance sheet, trial balance, debtors) download as they stand today, like the screen.
  const [from, to, words] = periodic ? periodOf(period) : [todayIso(), todayIso(), 'today'];
  const get = async (format: 'xlsx' | 'csv') => {
    setBusy(format);
    setError('');
    try {
      await download(`/reports/export/${report}?format=${format}&from=${from}&to=${to}`, `${label}.${format}`);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy('');
    }
  };
  return (
    <>
      {error && <span className="text-sm font-medium text-bad" role="alert">{error}</span>}
      <button className="btn btn-ghost" onClick={() => get('xlsx')} disabled={!!busy} title={`${label}${periodic ? `, ${words}` : ''}, as an Excel workbook`}>
        {busy === 'xlsx' ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileSpreadsheet className="h-4 w-4" />} Excel
      </button>
      <button className="btn btn-ghost" onClick={() => get('csv')} disabled={!!busy} title={`${label} as CSV, for any spreadsheet or accounting program`}>
        {busy === 'csv' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />} CSV
      </button>
      <Link to="/reports/monthly" className="btn btn-primary">
        <CalendarRange className="h-4 w-4" /> Monthly report
      </Link>
    </>
  );
}

export default function Reports() {
  // In the address, so a link (from Finance, say) opens the right report and period, and a refresh keeps them.
  const [params, setParams] = useSearchParams();
  const tab = params.get('tab') && EXPORTS[params.get('tab')!] ? params.get('tab')! : 'pnl';
  const period = ['month', 'last-month', 'year'].includes(params.get('period') ?? '') ? params.get('period')! : 'month';
  const set = (next: { tab?: string; period?: string }) => setParams({ tab: next.tab ?? tab, period: next.period ?? period }, { replace: true });
  return (
    <>
      <PageHeader print title="Reports" subtitle="Numbers come straight from your invoices and payments, so they’re always up to date." actions={<ExportButtons tab={tab} period={period} />} />
      {/* Report and period each get a full row: side by side they ran out of room and hid "Trial balance". */}
      <div className="mb-4 flex flex-col gap-2.5">
        <Chips label="Report" value={tab} onChange={(t) => set({ tab: t })} options={[['pnl', 'Profit & loss'], ['bs', 'Balance sheet'], ['cf', 'Cash flow'], ['aging', 'Who owes you'], ['vat', 'VAT'], ['tb', 'Trial balance']]} />
        {(tab === 'pnl' || tab === 'vat' || tab === 'cf') && (
          <div className="flex items-center gap-2">
            <span className="text-xs font-semibold text-muted">Period</span>
            <Chips label="Period" value={period} onChange={(p) => set({ period: p })} options={[['month', 'This month'], ['last-month', 'Last month'], ['year', 'This year']]} />
          </div>
        )}
      </div>
      {tab === 'pnl' && <ProfitAndLoss period={period} />}
      {tab === 'aging' && <Aging />}
      {tab === 'vat' && <Vat period={period} />}
      {tab === 'tb' && <TrialBalance />}
      {tab === 'bs' && <BalanceSheet />}
      {tab === 'cf' && <CashFlow period={period} />}
    </>
  );
}
