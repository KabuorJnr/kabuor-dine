// The monthly report: a month's numbers to read, print as PDF, or download as one Excel workbook to send to a
// partner, the bank or the accountant. The owner is deciding "how did the month go, and what do I send?", so it
// opens on the handful of figures that answer the first question (each against the month before and its target),
// then every table, the same ones the workbook holds.
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { ArrowLeft, FileSpreadsheet, Loader2, Printer } from 'lucide-react';
import { download, useApi } from '../lib/api';
import { cn, dayYear, todayIso } from '../lib/format';
import { ErrorNote, Loading, PageHeader } from '../components/ui';
import SheetTable, { cellText, type SheetData } from '../components/SheetTable';
import { DocFooter, Letterhead, printAs } from '../components/PrintDoc';

type Pack = { company: string; month: string; sheets: SheetData[] };

const lastMonth = () => {
  const [y, m] = todayIso().split('-').map(Number);
  return m === 1 ? `${y - 1}-12` : `${y}-${String(m - 1).padStart(2, '0')}`;
};

// The figures that say how a month went, in the order people ask about them.
const HEADLINES = ['Profit (before tax)', 'Sales (before VAT)', 'Gross margin', 'Money collected', 'Days to get paid', 'Overdue share of debts'];

function Headlines({ summary }: { summary: SheetData }) {
  const rows = HEADLINES.map((name) => summary.rows.find((r) => r.cells[0]?.v === name)).filter((r): r is SheetData['rows'][number] => !!r);
  return (
    <div className="grid grid-cols-2 gap-3 lg:grid-cols-3 print:grid-cols-3">
      {rows.map((r) => {
        const [label, now, before, change, target, status] = r.cells;
        const s = String(status?.v ?? '');
        return (
          <div key={String(label?.v)} className="card p-4 print:break-inside-avoid">
            <p className="text-sm font-medium text-muted">{String(label?.v)}</p>
            <p className="num mt-1 font-display text-2xl font-semibold tracking-tight">{cellText(now) || '—'}</p>
            <p className="mt-1 text-xs text-muted">
              {before && <>Month before {cellText(before)}</>}
              {change && <span className="ml-1">({Number(change.v) >= 0 ? '+' : ''}{cellText(change)})</span>}
            </p>
            {target && (
              <p className={cn('mt-1 text-xs font-semibold', s === 'On target' ? 'text-good' : s === 'Behind' ? 'text-bad' : 'text-warn')}>
                {s} · target {cellText(target)}
              </p>
            )}
          </div>
        );
      })}
    </div>
  );
}

export default function MonthlyReport() {
  const [params, setParams] = useSearchParams();
  const month = /^\d{4}-\d{2}$/.test(params.get('month') ?? '') ? params.get('month')! : lastMonth();
  const { data, error, loading } = useApi<Pack>(`/reports/monthly?month=${month}&format=json`);
  const [busy, setBusy] = useState(false);
  const [problem, setProblem] = useState('');
  const label = new Date(`${month}-01T00:00:00`).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' });
  const excel = async () => {
    setBusy(true);
    setProblem('');
    try {
      await download(`/reports/monthly?month=${month}`, `Monthly report ${month}.xlsx`);
    } catch (e) {
      setProblem((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  const [summary, ...tables] = data?.sheets ?? [];

  return (
    <>
      <div className="print:hidden">
        <Link to="/reports" className="mb-2 inline-flex items-center gap-1 text-sm font-semibold text-muted hover:text-ink"><ArrowLeft className="h-4 w-4" /> Reports</Link>
        <PageHeader
          title={`Monthly report · ${label}`}
          subtitle="Results against targets, the statements, what sold, who owes, VAT and payroll: ready to send."
          actions={
            <>
              <label className="flex items-center gap-2 text-sm font-semibold">
                <span className="text-muted">Month</span>
                <input type="month" className="field w-auto py-2" value={month} max={todayIso().slice(0, 7)} onChange={(e) => e.target.value && setParams({ month: e.target.value }, { replace: true })} aria-label="Month" />
              </label>
              <button className="btn btn-primary" onClick={excel} disabled={busy || !data}>
                {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <FileSpreadsheet className="h-4 w-4" />} Download Excel
              </button>
              <button className="btn btn-ghost" onClick={() => printAs(`Monthly report ${label} · ${data?.company ?? ''}`)} disabled={!data}>
                <Printer className="h-4 w-4" /> Print or save as PDF
              </button>
            </>
          }
        />
        {problem && <ErrorNote>{problem}</ErrorNote>}
      </div>

      {error ? <ErrorNote>{error}</ErrorNote> : loading && !data ? <Loading /> : summary ? (
        <div className="doc space-y-5">
          {/* On paper: the letterhead, since the app's own header doesn't print. One document, so it prints whole. */}
          <div className="mb-6 hidden print:block">
            <Letterhead title="Monthly report" number={label} facts={[['Prepared', dayYear(todayIso())]]} />
          </div>
          <Headlines summary={summary} />
          <section className="card p-4 sm:p-5 print:rounded-none print:p-0 print:pt-2 print:ring-0">
            <div className="print:break-after-avoid">
              <h2 className="font-display text-lg font-semibold">{summary.title}</h2>
              <p className="mb-3 text-sm text-muted">{summary.subtitle}</p>
            </div>
            <SheetTable sheet={summary} />
          </section>
          <div className="grid gap-5 xl:grid-cols-2 print:block">
            {tables.map((t) => (
              <section key={t.name} className="card min-w-0 p-4 sm:p-5 print:mt-8 print:rounded-none print:p-0 print:ring-0">
                <div className="print:break-after-avoid">
                  <h2 className="font-display text-lg font-semibold">{t.name}</h2>
                  <p className="mb-3 text-sm text-muted">{t.subtitle}</p>
                </div>
                <SheetTable sheet={t} compact />
              </section>
            ))}
          </div>
          <div className="hidden print:block">
            <DocFooter>Amounts in KSh, as recorded in Kabuor ERP on {dayYear(todayIso())}. The Excel workbook holds the same tables.</DocFooter>
          </div>
        </div>
      ) : null}
    </>
  );
}
