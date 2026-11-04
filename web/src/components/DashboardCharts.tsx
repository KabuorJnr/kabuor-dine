// The owner's charts under the KPIs. Each one answers a question in its title line ("7 of 12 months on target",
// "WhatsApp brings 41% of sales") so the picture is read before the bars are. Rules kept throughout: one axis per
// chart, a legend only where there are two series (and those are labelled directly too), hover for the detail,
// and a table view of every chart for exact figures and screen readers.
import { useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { Link } from 'react-router';
import { BarChart3, Loader2, Table2 } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, ksh } from '../lib/format';
import { PageError } from './ui';

type Month = { month: string; sales: number; target: number | null; invoices: number; grossProfit: number; moneyIn: number; moneyOut: number };
export type Charts = {
  from: string; to: string; salesTarget: number | null; owed: number;
  monthly: Month[];
  ageing: { bucket: string; amount: number; invoices: number }[];
  channels: { channel: string; sales: number; invoices: number }[];
  products: { name: string; sales: number; grossProfit: number; quantity: number }[];
  funnel: { step: string; count: number }[];
};

// Categorical slots 1 and 2 of the reference palette, checked for colour-blind separation and contrast on white.
export const SERIES = ['#2a78d6', '#eb6834'];

export const money = (cents: number) => ksh(Math.round(cents / 100) * 100);
/** Axis and label money: KSh 1.2M, KSh 350K. */
export function compact(cents: number) {
  const s = cents / 100;
  const a = Math.abs(s);
  if (a >= 1e6) return `${(s / 1e6).toFixed(a >= 1e7 ? 0 : 1)}M`;
  if (a >= 1e3) return `${(s / 1e3).toFixed(a >= 1e4 ? 0 : 1)}K`;
  return `${Math.round(s)}`;
}
const pct = (part: number, whole: number) => (whole > 0 ? Math.round((100 * part) / whole) : 0);
/** A share for reading: a sliver isn't "0%". */
const share = (part: number, whole: number) => (part > 0 && pct(part, whole) === 0 ? 'under 1%' : `${pct(part, whole)}%`);
export const monthName = (iso: string, year = false) => new Date(`${iso}T00:00:00`).toLocaleDateString('en-GB', year ? { month: 'long', year: 'numeric' } : { month: 'short' });

function useWidth() {
  const ref = useRef<HTMLDivElement>(null);
  const [w, setW] = useState(0);
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    setW(el.clientWidth);
    const ro = new ResizeObserver(([e]) => setW(e.contentRect.width));
    ro.observe(el);
    return () => ro.disconnect();
  }, []);
  return [ref, w] as const;
}

function ticks(max: number, n = 4) {
  if (max <= 0) return [0, 1];
  const raw = max / n;
  const mag = 10 ** Math.floor(Math.log10(raw));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= raw)!;
  return Array.from({ length: Math.ceil(max / step) + 1 }, (_, i) => i * step);
}

/** A bar standing on the baseline with a 4px rounded top. */
function barPath(x: number, base: number, w: number, h: number) {
  if (h <= 0 || w <= 0) return '';
  const r = Math.min(4, w / 2, h);
  const t = base - h;
  return `M${x},${base}V${t + r}Q${x},${t} ${x + r},${t}H${x + w - r}Q${x + w},${t} ${x + w},${t + r}V${base}Z`;
}

type Series = { name: string; color: string; values: number[] };

/** Month columns on one money axis, optionally with a dashed target line. Hover a month for its figures. */
export function Columns({ labels, series, reference, tip, fmt, whole, height = 220 }: { labels: string[]; series: Series[]; reference?: { value: number; label: string } | null; tip: (i: number) => ReactNode; fmt?: (v: number) => string; whole?: boolean; height?: number }) {
  void whole;
  const formatter = fmt ?? compact;
  const [ref, w] = useWidth();
  const [hover, setHover] = useState<number | null>(null);
  const H = height, top = 16, bottom = 24, left = 46, right = 8;
  const max = Math.max(1, ...series.flatMap((s) => s.values), reference?.value ?? 0);
  const t = ticks(max);
  const yMax = t[t.length - 1];
  const plotW = Math.max(0, w - left - right);
  const plotH = H - top - bottom;
  const band = plotW / labels.length;
  const gap = 2;
  const groupW = Math.min(band * 0.72, 22 * series.length + gap);
  const barW = (groupW - gap * (series.length - 1)) / series.length;
  const y = (v: number) => top + plotH - (v / yMax) * plotH;
  const base = top + plotH;
  const every = Math.max(1, Math.ceil((labels.length * 34) / Math.max(plotW, 1)));
  const last = labels.length - 1;
  return (
    <div ref={ref} className="relative" onMouseLeave={() => setHover(null)}>
      {w > 0 && (
        <svg width={w} height={H} className="block overflow-visible" role="img" aria-label={series.map((s) => s.name).join(' and ') + ' by month'}>
          {t.map((v) => (
            <g key={v}>
              <line x1={left} x2={w - right} y1={y(v)} y2={y(v)} stroke="var(--color-line)" strokeWidth={1} />
              <text x={left - 6} y={y(v)} dy="0.32em" textAnchor="end" className="fill-faint text-[10px]">{formatter(v)}</text>
            </g>
          ))}
          {labels.map((l, i) => {
            const x0 = left + band * i + (band - groupW) / 2;
            return (
              <g key={i} opacity={hover == null || hover === i ? 1 : 0.4}>
                {series.map((s, k) => <path key={s.name} d={barPath(x0 + k * (barW + gap), base, barW, base - y(s.values[i]))} fill={s.color} />)}
                {(i % every === 0 || i === last) && <text x={left + band * (i + 0.5)} y={H - 6} textAnchor="middle" className={cn('text-2xs', hover === i ? 'fill-ink font-semibold' : 'fill-muted')}>{l}</text>}
              </g>
            );
          })}
          {/* Direct labels on the latest month, so the current figures read without hovering. */}
          {hover == null && series.map((s, k) => s.values[last] > 0 && (
            <text key={s.name} x={left + band * last + (band - groupW) / 2 + k * (barW + gap) + barW / 2} y={y(s.values[last]) - 4} textAnchor="middle" className="fill-ink-2 text-[10px] font-semibold">{formatter(s.values[last])}</text>
          ))}
          {reference && reference.value > 0 && (
            <g>
              <line x1={left} x2={w - right} y1={y(reference.value)} y2={y(reference.value)} stroke="var(--color-ink-2)" strokeWidth={1.5} strokeDasharray="5 4" />
              <text x={left + 4} y={y(reference.value) - 5} className="fill-ink-2 text-2xs font-semibold">{reference.label}</text>
            </g>
          )}
          {labels.map((_, i) => (
            <rect key={i} x={left + band * i} y={top} width={band} height={plotH + bottom} fill="transparent" onMouseEnter={() => setHover(i)} onClick={() => setHover(hover === i ? null : i)} />
          ))}
        </svg>
      )}
      {hover != null && w > 0 && (
        <div className="pointer-events-none absolute top-0 z-10 w-52 rounded-md bg-card p-2.5 text-xs shadow-lg ring-1 ring-line" style={{ left: Math.min(Math.max(left + band * (hover + 0.5) - 104, 0), w - 208) }}>
          {tip(hover)}
        </div>
      )}
    </div>
  );
}

/** Ranked horizontal bars, each labelled with its value, for a single measure. */
export function HBars({ rows, value, color = SERIES[0] }: { rows: { label: string; value: number; note?: string }[]; value: (v: number) => string; color?: string }) {
  const max = Math.max(1, ...rows.map((r) => r.value));
  return (
    <ul className="space-y-2">
      {rows.map((r) => (
        // Long names are cut to fit the column, so the full name (and its note) is on hover.
        <li key={r.label} className="grid grid-cols-[minmax(6rem,9rem)_1fr] items-center gap-3 text-sm" title={r.note ? `${r.label} · ${r.note}` : r.label}>
          <span className="truncate text-ink-2">{r.label}</span>
          <span className="flex items-center gap-2">
            <span className="h-3.5 rounded-r-[4px]" style={{ width: `${Math.max(1, (r.value / max) * 75)}%`, background: color }} />
            <span className="num shrink-0 text-xs font-semibold">{value(r.value)}</span>
            {r.note && <span className="hidden truncate text-xs text-faint sm:inline">{r.note}</span>}
          </span>
        </li>
      ))}
    </ul>
  );
}

export function Table({ head, rows }: { head: string[]; rows: ReactNode[][] }) {
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead><tr className="text-left text-xs text-muted">{head.map((h, i) => <th key={h} className={cn('pb-1.5 font-semibold', i > 0 && 'text-right')}>{h}</th>)}</tr></thead>
        <tbody className="divide-y divide-line">
          {rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j} className={cn('py-1.5', j > 0 && 'num text-right')}>{c}</td>)}</tr>)}
        </tbody>
      </table>
    </div>
  );
}

export function Legend({ items }: { items: [string, string][] }) {
  return (
    <span className="flex flex-wrap gap-3 text-xs text-ink-2">
      {items.map(([name, color]) => <span key={name} className="inline-flex items-center gap-1.5"><span className="h-2.5 w-2.5 rounded-sm" style={{ background: color }} />{name}</span>)}
    </span>
  );
}

export function ChartCard({ title, answer, legend, link, table, children, className }: { title: string; answer: ReactNode; legend?: ReactNode; link?: [string, string]; table: ReactNode; children: ReactNode; className?: string }) {
  const [asTable, setAsTable] = useState(false);
  return (
    <section className={cn('card flex flex-col gap-3 p-4 sm:p-5', className)}>
      <div className="flex items-start gap-3">
        <div className="min-w-0 flex-1">
          <h3 className="text-sm font-semibold text-muted">{title}</h3>
          <p className="text-sm font-semibold text-ink">{answer}</p>
        </div>
        <button className="btn btn-ghost shrink-0 px-2" onClick={() => setAsTable(!asTable)} aria-label={asTable ? 'Show as chart' : 'Show as table'} title={asTable ? 'Show as chart' : 'Show as table'}>
          {asTable ? <BarChart3 className="h-4 w-4" /> : <Table2 className="h-4 w-4" />}
        </button>
      </div>
      {legend && !asTable && legend}
      <div className="flex-1">{asTable ? table : children}</div>
      {link && <Link to={link[0]} className="text-sm font-semibold text-brand hover:underline">{link[1]} →</Link>}
    </section>
  );
}

export const CHARTS_PATH = '/insights/charts?months=12';

export default function DashboardCharts() {
  const { data, error } = useApi<Charts>(CHARTS_PATH);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loader2 className="h-5 w-5 animate-spin text-faint" />;
  // A young business: start at its first month with any activity, so the chart isn't mostly empty months.
  const firstActive = data.monthly.findIndex((x) => x.sales || x.moneyIn || x.moneyOut);
  const m = data.monthly.slice(Math.min(firstActive < 0 ? 0 : firstActive, data.monthly.length - 3));
  const span = m.length === 12 ? 'The last 12 months' : `Since ${monthName(m[0].month, true)}`;
  const labels = m.map((x) => monthName(x.month));
  const target = data.salesTarget;
  // The current month is still running, so it isn't judged against the full target.
  const closed = m.slice(0, -1);
  const hit = target ? closed.filter((x) => x.sales >= target).length : 0;
  const best = [...m].sort((a, b) => b.sales - a.sales)[0];
  const net = m.reduce((s, x) => s + x.moneyIn - x.moneyOut, 0);
  const shortMonths = m.filter((x) => x.moneyOut > x.moneyIn).length;
  const late = data.ageing.filter((a) => a.bucket !== 'Not yet due').reduce((s, a) => s + a.amount, 0);
  const veryLate = data.ageing.find((a) => a.bucket.startsWith('Over 90'))?.amount ?? 0;
  const channelTotal = data.channels.reduce((s, c) => s + c.sales, 0);
  // Untagged sales say nothing about outreach, so the answer names the best channel we actually know.
  const known = data.channels.filter((c) => c.channel !== 'Direct / unknown');
  const topChannel = known[0];
  const untagged = channelTotal - known.reduce((s, c) => s + c.sales, 0);
  const top3 = data.products.slice(0, 3).reduce((s, p) => s + p.sales, 0);
  const f = data.funnel;
  const asked = f[1]?.count ?? 0;
  const bought = f[f.length - 1]?.count ?? 0;
  // The step where most people are lost, counting from the inquiry on (visits are a different crowd).
  const drops = f.slice(1).map((s, i, a) => (i === 0 ? null : { from: a[i - 1].step, to: s.step, lost: a[i - 1].count - s.count })).filter((d): d is { from: string; to: string; lost: number } => d != null && d.lost > 0);
  const worst = drops.sort((a, b) => b.lost - a.lost)[0];

  return (
    <section className="space-y-3" aria-label="Charts">
      <h2 className="flex items-center gap-2 font-semibold"><BarChart3 className="h-4 w-4" /> {span}</h2>
      <div className="grid gap-4 lg:grid-cols-2">
        <ChartCard
          title="Sales by month (before VAT)"
          answer={target ? <>{hit} of {closed.length} finished months on target</> : <>Best month: {monthName(best.month, true)}, {money(best.sales)}</>}
          link={target ? ['/reports', 'Sales reports'] : undefined}
          table={<Table head={['Month', 'Sales', 'Gross profit', 'Invoices']} rows={m.map((x) => [monthName(x.month, true), money(x.sales), money(x.grossProfit), x.invoices])} />}
        >
          {!target && <p className="-mt-1 mb-1 text-xs text-muted">Set a monthly sales target above to see it on this chart.</p>}
          <Columns
            labels={labels}
            series={[{ name: 'Sales', color: SERIES[0], values: m.map((x) => x.sales) }]}
            reference={target ? { value: target, label: `Target ${compact(target)}` } : null}
            tip={(i) => (
              <>
                <b className="block text-ink">{monthName(m[i].month, true)}</b>
                <span className="flex justify-between"><span className="text-muted">Sales</span><b className="num">{money(m[i].sales)}</b></span>
                {target != null && <span className="flex justify-between"><span className="text-muted">Target</span><span className="num">{money(target)} ({pct(m[i].sales, target)}%)</span></span>}
                <span className="flex justify-between"><span className="text-muted">Gross profit</span><span className="num">{money(m[i].grossProfit)}</span></span>
                <span className="flex justify-between"><span className="text-muted">Invoices</span><span className="num">{m[i].invoices}</span></span>
              </>
            )}
          />
        </ChartCard>

        <ChartCard
          title="Money in and money out"
          answer={<span className={net < 0 ? 'text-bad' : 'text-good'}>{net < 0 ? '−' : '+'}{money(Math.abs(net))} net {m.length === 12 ? 'over 12 months' : `since ${monthName(m[0].month, true)}`}{shortMonths > 0 && <span className="font-normal text-muted"> · {shortMonths} month{shortMonths === 1 ? '' : 's'} paid out more than came in</span>}</span>}
          legend={<Legend items={[['Money in (customer payments)', SERIES[0]], ['Money out (suppliers and pay)', SERIES[1]]]} />}
          link={['/money-in', 'Money in']}
          table={<Table head={['Month', 'In', 'Out', 'Net']} rows={m.map((x) => [monthName(x.month, true), money(x.moneyIn), money(x.moneyOut), <span className={x.moneyIn - x.moneyOut < 0 ? 'text-bad' : ''}>{money(x.moneyIn - x.moneyOut)}</span>])} />}
        >
          <Columns
            labels={labels}
            series={[{ name: 'Money in', color: SERIES[0], values: m.map((x) => x.moneyIn) }, { name: 'Money out', color: SERIES[1], values: m.map((x) => x.moneyOut) }]}
            tip={(i) => (
              <>
                <b className="block text-ink">{monthName(m[i].month, true)}</b>
                <span className="flex justify-between"><span className="text-muted">In</span><b className="num">{money(m[i].moneyIn)}</b></span>
                <span className="flex justify-between"><span className="text-muted">Out</span><b className="num">{money(m[i].moneyOut)}</b></span>
                <span className="flex justify-between border-t border-line pt-1"><span className="text-muted">Net</span><b className={cn('num', m[i].moneyIn - m[i].moneyOut < 0 && 'text-bad')}>{money(m[i].moneyIn - m[i].moneyOut)}</b></span>
              </>
            )}
          />
        </ChartCard>

        <ChartCard
          title="What customers owe, by how late"
          answer={data.owed === 0 ? 'Nobody owes you anything' : <>{money(data.owed)} owed · {pct(late, data.owed)}% late{veryLate > 0 && <span className="text-bad"> · {money(veryLate)} over 90 days</span>}</>}
          link={['/invoices?status=overdue', 'Chase late invoices']}
          table={<Table head={['Age', 'Owed', 'Invoices']} rows={data.ageing.map((a) => [a.bucket, money(a.amount), a.invoices])} />}
        >
          <HBars rows={data.ageing.map((a) => ({ label: a.bucket, value: a.amount, note: `${a.invoices} invoice${a.invoices === 1 ? '' : 's'}` }))} value={money} />
        </ChartCard>

        <ChartCard
          title="Where sales come from (last 90 days)"
          answer={channelTotal === 0 ? 'No sales in the last 90 days' : <>
            {topChannel ? <>{topChannel.channel} brings the most: {share(topChannel.sales, channelTotal)} of sales</> : 'No sales are linked to a channel yet'}
            {pct(untagged, channelTotal) >= 50 && <span className="block text-sm font-normal text-muted">{pct(untagged, channelTotal)}% came with no channel. Give each salesperson and campaign a tracking link to see where they really come from.</span>}
          </>}
          link={['/os?tab=insights&view=channels', 'Channels in detail']}
          table={<Table head={['Channel', 'Sales', 'Share', 'Invoices']} rows={data.channels.map((c) => [c.channel, money(c.sales), `${share(c.sales, channelTotal)}`, c.invoices])} />}
        >
          <HBars rows={data.channels.slice(0, 7).map((c) => ({ label: c.channel, value: c.sales, note: `${share(c.sales, channelTotal)} · ${c.invoices} invoice${c.invoices === 1 ? '' : 's'}` }))} value={money} />
        </ChartCard>

        <ChartCard
          title="Best sellers (last 90 days)"
          answer={data.products.length ? <>Top 3 make {pct(top3, channelTotal)}% of sales</> : 'Nothing sold in the last 90 days'}
          link={['/os?tab=insights&view=selling', 'What sells']}
          table={<Table head={['Product', 'Sales', 'Gross profit', 'Quantity']} rows={data.products.map((p) => [p.name, money(p.sales), money(p.grossProfit), p.quantity.toLocaleString('en-KE')])} />}
        >
          <HBars rows={data.products.map((p) => ({ label: p.name, value: p.sales, note: p.sales && p.grossProfit === p.sales ? 'cost not recorded' : `${pct(p.grossProfit, p.sales)}% margin` }))} value={money} />
        </ChartCard>

        <ChartCard
          title="From inquiry to sale (last 90 days)"
          answer={asked > 0 ? <>{pct(bought, asked)}% of inquiries bought{worst && <span className="font-normal text-muted"> · most are lost between “{worst.from.toLowerCase()}” and “{worst.to.toLowerCase()}”</span>}</> : 'No inquiries in the last 90 days'}
          link={['/os?tab=insights&view=inquiries', 'Why inquiries don’t buy']}
          table={<Table head={['Step', 'People', 'Of the step before']} rows={f.map((s, i) => [s.step, s.count, i === 0 ? '—' : `${pct(s.count, f[i - 1].count)}%`])} />}
        >
          <HBars rows={f.map((s, i) => ({ label: s.step, value: s.count, note: i === 0 ? 'people who clicked a tracking link' : `${pct(s.count, f[i - 1].count)}% of the step before` }))} value={(v) => v.toLocaleString('en-KE')} />
        </ChartCard>
      </div>
    </section>
  );
}
