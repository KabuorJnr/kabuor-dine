// A report table as the server builds it for spreadsheets (see Reports/Exports.cs), drawn on screen and on paper.
// Each cell says what it is, so money, shares and dates read the way they do everywhere else in the app.
import { cn, day, ksh } from '../lib/format';

export type Cell = { k: 'ksh' | 'pct' | 'date' | 'num' | 'text'; v: string | number } | null;
export type SheetData = { name: string; title: string; subtitle: string; columns: string[]; note: string | null; rows: { style: 'plain' | 'section' | 'total'; cells: Cell[] }[] };

export function cellText(c: Cell) {
  if (!c) return '';
  switch (c.k) {
    case 'ksh': return ksh(Number(c.v));
    case 'pct': return `${Number(c.v).toFixed(1)}%`;
    case 'date': return day(String(c.v));
    case 'num': return Number(c.v).toLocaleString('en-KE', { maximumFractionDigits: 1 });
    default: return String(c.v);
  }
}

const numeric = (c: Cell) => !!c && c.k !== 'text' && c.k !== 'date';

export default function SheetTable({ sheet, compact }: { sheet: SheetData; compact?: boolean }) {
  // A column is right-aligned when its figures are numbers, so totals line up under them.
  const right = sheet.columns.map((_, i) => sheet.rows.some((r) => numeric(r.cells[i] ?? null)));
  if (sheet.rows.length === 0) return <p className="text-sm text-muted">{sheet.note ?? 'Nothing in this period.'}</p>;
  return (
    <div className="overflow-x-auto print:overflow-visible">
      <table className={cn('w-full text-sm print:min-w-0 print:text-xs', compact ? 'min-w-[28rem]' : 'min-w-[32rem]')}>
        <thead>
          <tr className="border-b border-line text-left text-xs text-muted">
            {sheet.columns.map((c, i) => <th key={i} className={cn('px-2 py-2 first:pl-0', right[i] && 'text-right')}>{c}</th>)}
          </tr>
        </thead>
        <tbody>
          {sheet.rows.map((r, i) =>
            r.style === 'section' ? (
              <tr key={i}><td colSpan={sheet.columns.length} className="pt-4 pb-1 text-xs font-semibold tracking-wide text-muted uppercase">{cellText(r.cells[0])}</td></tr>
            ) : (
              <tr key={i} className={cn(r.style === 'total' ? 'border-t border-ink-2/30 font-semibold' : 'border-b border-line/70')}>
                {sheet.columns.map((_, j) => (
                  <td key={j} className={cn('px-2 py-1.5 first:pl-0', right[j] && 'num text-right whitespace-nowrap', j === 0 && 'max-w-[18rem]')}>{cellText(r.cells[j] ?? null)}</td>
                ))}
              </tr>
            ),
          )}
        </tbody>
      </table>
      {sheet.note && <p className="mt-2 text-xs text-muted">{sheet.note}</p>}
    </div>
  );
}
