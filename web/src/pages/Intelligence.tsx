// Kabuor Intelligence. The owner is deciding "where do I put my next shilling and my next hour?", so the page
// opens on their questions (what sells, what sits, what to buy, who buys, why inquiries don't buy, which channel
// works, who sells, does stock match the shelf), each answered with the one number that matters and a way in to
// the detail. Every figure comes from the books, stock and CRM; nothing is guessed.
import { useState } from 'react';
import { Link, useSearchParams } from 'react-router';
import { AlertTriangle, ArrowRight, Boxes, Crown, Megaphone, MessageCircleQuestion, PackageCheck, ScanLine, ShoppingCart, Sparkles, TrendingUp, Users } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn, day, ksh } from '../lib/format';
import { Chips, Empty, Loading, PageHeader, Status, PageError } from '../components/ui';

type Tab = 'overview' | 'selling' | 'shelf' | 'buy' | 'customers' | 'inquiries' | 'quotes' | 'channels' | 'team' | 'accuracy';
const TABS: [Tab, string][] = [
  ['overview', 'Questions'], ['selling', 'What sells'], ['shelf', 'What sits'], ['buy', 'What to buy'], ['customers', 'Who buys'],
  ['inquiries', 'Why no sale'], ['quotes', 'Quote vs Invoice'], ['channels', 'Channels'], ['team', 'Salespeople'], ['accuracy', 'Stock accuracy'],
];

/** A proportion bar: the value against the largest in the list, so rows can be compared at a glance. */
function Bar({ value, max }: { value: number; max: number }) {
  const pct = max > 0 ? Math.max(2, Math.round((value / max) * 100)) : 0;
  return (
    <span className="block h-1.5 w-full overflow-hidden rounded-full bg-paper" aria-hidden>
      <span className="block h-full rounded-full bg-indigo" style={{ width: `${pct}%` }} />
    </span>
  );
}

function Table({ head, rows }: { head: string[]; rows: React.ReactNode[][] }) {
  return (
    <div className="card overflow-x-auto">
      <table className="w-full min-w-[34rem] text-sm">
        <thead>
          <tr className="border-b border-line text-left text-xs font-semibold text-muted">{head.map((h, i) => <th key={h} className={cn('px-4 py-2.5', i > 0 && 'text-right')}>{h}</th>)}</tr>
        </thead>
        <tbody className="divide-y divide-line">
          {rows.map((r, i) => <tr key={i}>{r.map((c, j) => <td key={j} className={cn('px-4 py-2.5 align-top', j > 0 && 'num text-right')}>{c}</td>)}</tr>)}
        </tbody>
      </table>
    </div>
  );
}

export default function Intelligence({ embedded }: { embedded?: boolean }) {
  const [params, setParams] = useSearchParams();
  // Inside Business OS the page's own tabs live in ?view= (Business OS uses ?tab=).
  const key = embedded ? 'view' : 'tab';
  const tab = (params.get(key) as Tab) ?? 'overview';
  const [days, setDays] = useState('90');
  const to = new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 10);
  const from = new Date(Date.now() + 3 * 3600e3 - (Number(days) - 1) * 864e5).toISOString().slice(0, 10);
  const range = `from=${from}&to=${to}`;
  const go = (t: Tab) => setParams(embedded ? (t === 'overview' ? { tab: 'insights' } : { tab: 'insights', view: t }) : t === 'overview' ? {} : { tab: t });

  return (
    <div>
      {!embedded && <PageHeader print title="Kabuor Intelligence" subtitle="Answers from your own sales, stock, customers and links. Ask Msaidizi for anything else." />}
      <div className="mb-4 flex flex-col gap-3 lg:flex-row lg:items-center lg:justify-between">
        <Chips label="Show" value={tab} onChange={go} options={TABS} />
        {['selling', 'inquiries', 'channels', 'quotes'].includes(tab) && (
          <Chips label="Period" value={days} onChange={setDays} options={[['30', '30 days'], ['90', '90 days'], ['365', 'Year']]} />
        )}
      </div>
      {tab === 'overview' && <Overview go={go} />}
      {tab === 'selling' && <Selling range={range} />}
      {tab === 'shelf' && <Shelf />}
      {tab === 'buy' && <Buy />}
      {tab === 'customers' && <Customers />}
      {tab === 'inquiries' && <Inquiries range={range} />}
      {tab === 'quotes' && <QuoteVariance days={days} />}
      {tab === 'channels' && <Channels range={range} />}
      {tab === 'team' && <Team />}
      {tab === 'accuracy' && <Accuracy />}
    </div>
  );
}

function Question({ icon: Icon, q, answer, detail, tone, onOpen }: { icon: typeof TrendingUp; q: string; answer: React.ReactNode; detail?: React.ReactNode; tone?: 'warn' | 'good'; onOpen: () => void }) {
  return (
    <button onClick={onOpen} className="card flex flex-col gap-2 p-4 text-left transition hover:ring-faint">
      <span className="flex items-center gap-2 text-xs font-semibold tracking-wide text-muted uppercase"><Icon className="h-4 w-4" /> {q}</span>
      <span className={cn('text-lg leading-snug font-bold', tone === 'warn' && 'text-warn', tone === 'good' && 'text-good')}>{answer}</span>
      {detail && <span className="text-sm text-muted">{detail}</span>}
      <span className="mt-auto inline-flex items-center gap-1 text-sm font-semibold text-brand">See why <ArrowRight className="h-4 w-4" /></span>
    </button>
  );
}

/** Whole shillings: the headline answers don't need cents. */
const kes = (cents: number) => ksh(Math.round(cents / 100) * 100);

function Overview({ go }: { go: (t: Tab) => void }) {
  const { data, error } = useApi<any>('/insights/overview');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const d = data;
  return (
    <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
      <Question icon={TrendingUp} q="What sells most?" onOpen={() => go('selling')}
        answer={d.bestSeller ? d.bestSeller.name : 'No sales yet'} detail={d.bestSeller && `${kes(d.bestSeller.revenue)} in 90 days, ${d.bestSeller.share}% of sales`} />
      <Question icon={Boxes} q="What sits on the shelf?" onOpen={() => go('shelf')} tone={d.stuckStock.value > 0 ? 'warn' : undefined}
        answer={d.stuckStock.value > 0 ? `${kes(d.stuckStock.value)} tied up` : 'Nothing stuck'} detail={d.stuckStock.worst && `${d.stuckStock.items} slow or dead items; most in ${d.stuckStock.worst.name}`} />
      <Question icon={ShoppingCart} q="What should I buy?" onOpen={() => go('buy')}
        answer={d.buyNow.count > 0 ? `${d.buyNow.count} item${d.buyNow.count === 1 ? '' : 's'} to reorder` : 'Stock covers demand'} detail={d.buyNow.count > 0 && `About ${kes(d.buyNow.cost)} at last prices`} />
      <Question icon={Users} q="Who buys most?" onOpen={() => go('customers')}
        answer={d.bestCustomer ? d.bestCustomer.name : 'No buyers yet'} detail={d.bestCustomer && `${kes(d.bestCustomer.monetary)} this year · ${d.bestCustomer.segment}${d.atRisk ? ` · ${d.atRisk} good customers at risk` : ''}`} />
      <Question icon={MessageCircleQuestion} q="Why do inquiries not buy?" onOpen={() => go('inquiries')} tone={d.inquiries.unanswered > 0 ? 'warn' : undefined}
        answer={d.inquiries.total === 0 ? 'No inquiries yet' : d.inquiries.topLostReason ? d.inquiries.topLostReason : `${d.inquiries.conversion ?? 0}% become sales`}
        detail={d.inquiries.total > 0 && `${d.inquiries.total} inquiries · ${d.inquiries.conversion ?? '—'}% bought${d.inquiries.unanswered ? ` · ${d.inquiries.unanswered} not answered yet` : ''}`} />
      <Question icon={Megaphone} q="Which channel brings people?" onOpen={() => go('channels')}
        answer={d.bestChannel ?? 'Not measured yet'} detail={!d.bestChannel ? 'Make tracking links for each salesperson and channel to see this.' : 'Most revenue in 90 days'} />
      <Question icon={Crown} q="Salesperson of the year" onOpen={() => go('team')} tone="good"
        answer={d.salespersonOfTheYear ? d.salespersonOfTheYear.name : 'No sales yet'} detail={d.salespersonOfTheYear && `${kes(d.salespersonOfTheYear.sold)} sold · ${kes(d.salespersonOfTheYear.brought)} from customers they brought`} />
      <Question icon={ScanLine} q="Does stock match the shelf?" onOpen={() => go('accuracy')} tone={d.stockAccuracy != null && d.stockAccuracy < 95 ? 'warn' : d.stockAccuracy != null ? 'good' : undefined}
        answer={d.stockAccuracy == null ? 'Not counted yet' : `${d.stockAccuracy}% of counts exact`} detail={d.stockAccuracy == null ? 'Count your stock to measure it.' : 'Last 90 days of counts'} />
      <Question icon={Sparkles} q="Why quotes differ from invoices?" onOpen={() => go('quotes')}
        answer="Quote vs Invoice Audit" detail="Compare quoted vs actual invoices, attribution (who brought them), and AI explanations." />
    </div>
  );
}

function Selling({ range }: { range: string }) {
  const { data, error } = useApi<any>(`/insights/selling?${range}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  if (!data.items.length) return <Empty title="No sales in this period" />;
  const max = data.items[0].revenue;
  return (
    <>
      <p className="mb-3 text-sm text-muted">{ksh(data.total)} of sales from {day(data.from)} to {day(data.to)}.</p>
      <Table head={['Product', 'Sold', 'Revenue', 'Margin', 'Customers']} rows={data.items.map((i: any) => [
        <span className="block min-w-40"><b>{i.name}</b><Bar value={i.revenue} max={max} /><span className="text-xs text-muted">{i.share}% of sales</span></span>,
        `${Number(i.quantity).toLocaleString()} ${i.unit}`, ksh(i.revenue), i.margin == null ? '—' : ksh(i.margin), i.customers,
      ])} />
    </>
  );
}

function Shelf() {
  const { data, error } = useApi<any>('/insights/shelf');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted"><b className="text-ink">{ksh(data.tiedUp)}</b> of your {ksh(data.stockValue)} stock is in {data.dead} item{data.dead === 1 ? '' : 's'} that haven’t sold in 90 days and {data.slow} that would take over four months to sell. That money could be stock that sells.</p>
      {data.stuck.length ? (
        <Table head={['Product', 'On hand', 'Tied up', 'Sold in 90 days', 'Days to sell it', 'Last sold']} rows={data.stuck.map((i: any) => [
          <span><b>{i.name}</b> <Status tone={i.state === 'dead' ? 'bad' : 'warn'}>{i.state === 'dead' ? 'Not selling' : 'Slow'}</Status></span>,
          `${i.onHand} ${i.unit}`, ksh(i.value), i.sold90, i.daysOfCover ?? '∞', i.lastSold ? day(i.lastSold) : 'Never',
        ])} />
      ) : <Empty title="Nothing is stuck">Everything in stock sold within the last 90 days.</Empty>}
      {data.fastest.length > 0 && <p className="text-sm text-muted">Selling fastest (under two weeks of stock left): {data.fastest.map((i: any) => `${i.name} (${i.daysOfCover} days)`).join(', ')}.</p>}
    </div>
  );
}

function Buy() {
  const { data, error } = useApi<any>('/insights/buy');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-4">
      <p className="text-sm text-muted">Based on the last 60 days of sales, enough stock for {data.leadDays} days of delivery plus {data.safetyDays} days spare, after what’s on hand and already on order.</p>
      {data.items.length ? (
        <Table head={['Product', 'Brand', 'Sells a day', 'On hand', 'On order', 'Runs out in', 'Buy', 'About']} rows={data.items.map((i: any) => [
          <span><b>{i.name}</b>{i.supplier && <span className="block text-xs text-muted">from {i.supplier}</span>}</span>,
          i.brand ? <span className="rounded bg-brand/10 px-2 py-0.5 text-xs font-semibold text-brand">{i.brand}</span> : <span className="text-muted">—</span>,
          i.perDay, i.onHand, i.onOrder, i.daysLeft == null ? '—' : <b className={cn(i.daysLeft <= 3 && 'text-bad')}>{i.daysLeft} days</b>, <b>{i.buy} {i.unit}</b>, i.estimatedCost == null ? '—' : ksh(i.estimatedCost),
        ])} />
      ) : <Empty title="Stock covers demand">Nothing needs reordering at today’s rate of sale.</Empty>}
      {data.turnedAway.length > 0 && (
        <div className="card flex gap-3 p-4 text-sm"><AlertTriangle className="h-5 w-5 shrink-0 text-warn" /><span><b>Asked for while out of stock:</b> {data.turnedAway.map((i: any) => `${i.name} (${i.turnedAway} inquir${i.turnedAway === 1 ? 'y' : 'ies'})`).join(', ')}. These are sales being turned away.</span></div>
      )}
      <Link to="/agents?tab=agents" className="inline-flex items-center gap-1 text-sm font-semibold text-brand">The restock agent can draft these orders for you <ArrowRight className="h-4 w-4" /></Link>
    </div>
  );
}

const SEGMENT_HINT: Record<string, string> = {
  Champions: 'Buy often and recently: thank them, ask for referrals.', Loyal: 'Buy often: offer them a price list.', 'Big spenders': 'Spend the most: look after them.',
  Promising: 'Could buy more: follow up.', New: 'Just started: make the second order easy.', 'At risk': 'Used to buy often, gone quiet: call them now.', Sleeping: 'Haven’t bought in a while: a campaign might wake them.',
};

function Customers() {
  const { data, error } = useApi<any>('/insights/customers');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  const max = Math.max(...data.segments.map((s: any) => s.revenue), 1);
  return (
    <div className="space-y-5">
      <div className="grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
        {data.segments.filter((s: any) => s.customers > 0).map((s: any) => (
          <div key={s.segment} className="card p-3">
            <p className="flex justify-between text-sm"><b>{s.segment}</b><span className="num text-muted">{s.customers}</span></p>
            <p className="num text-sm font-semibold">{ksh(s.revenue)}</p>
            <Bar value={s.revenue} max={max} />
            <p className="mt-1 text-xs text-muted">{SEGMENT_HINT[s.segment]}</p>
          </div>
        ))}
      </div>
      {data.neverBought > 0 && <p className="text-sm text-muted">{data.neverBought} customer{data.neverBought === 1 ? ' has' : 's have'} never bought.</p>}
      <h2 className="text-sm font-semibold">Top customers this year</h2>
      <Table head={['Customer', 'Spent', 'Invoices', 'Last bought', 'Pays in', 'Segment']} rows={data.top.map((c: any) => [
        <Link to={`/customers/${c.id}`} className="font-semibold hover:underline">{c.name}</Link>, ksh(c.monetary), c.frequency, `${c.recency} days ago`,
        c.averageDaysToPay == null ? '—' : `${c.averageDaysToPay} days`, <Status tone={c.segment === 'At risk' ? 'warn' : c.segment === 'Champions' ? 'good' : 'plain'}>{c.segment}</Status>,
      ])} />
      {data.slowPayers.length > 0 && <p className="text-sm text-muted"><b className="text-warn">Slow payers</b> (over 30 days on average): {data.slowPayers.map((c: any) => `${c.name} (${c.averageDaysToPay} days)`).join(', ')}.</p>}
    </div>
  );
}

function Inquiries({ range }: { range: string }) {
  const { data, error } = useApi<any>(`/insights/inquiries?${range}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  if (!data.total) return <Empty title="No inquiries in this period" action={<Link to="/inquiries" className="btn btn-primary">Open the inbox</Link>}>Inquiries from your shop, links, calls and walk-ins are counted here.</Empty>;
  const maxReason = Math.max(...data.lostReasons.map((r: any) => r.count), 1);
  return (
    <div className="space-y-5">
      <p className="text-sm">
        <b>{data.total}</b> inquiries: <b className="text-good">{data.won} bought</b>, <b className="text-bad">{data.lost} lost</b>, {data.open} still open.
        {data.conversion != null && <> Of those finished, <b>{data.conversion}%</b> bought.</>}
        {data.medianHoursToAnswer != null && <> Half were answered within <b>{data.medianHoursToAnswer} hours</b>.</>}
        {data.unanswered > 0 && <b className="text-warn"> {data.unanswered} haven’t been answered.</b>}
      </p>
      <div className="grid gap-4 lg:grid-cols-2">
        <section className="card p-4">
          <h2 className="mb-2 text-sm font-semibold">Why they didn’t buy</h2>
          {data.lostReasons.length ? data.lostReasons.map((r: any) => (
            <div key={r.reason} className="mb-2 text-sm"><span className="flex justify-between"><span>{r.reason}</span><b className="num">{r.count}</b></span><Bar value={r.count} max={maxReason} /></div>
          )) : <p className="text-sm text-muted">No lost inquiries yet.</p>}
        </section>
        <section className="card p-4">
          <h2 className="mb-2 text-sm font-semibold">Does answering fast matter?</h2>
          {data.bySpeed.map((s: any) => (
            <p key={s.answered} className="flex justify-between border-b border-line py-1.5 text-sm last:border-0"><span>{s.answered}</span><span className="num">{s.inquiries} · <b>{s.conversion == null ? '—' : `${s.conversion}% bought`}</b></span></p>
          ))}
        </section>
      </div>
      {data.products.length > 0 && <Table head={['Most asked about', 'Inquiries', 'Bought']} rows={data.products.map((p: any) => [p.name, p.asked, p.conversion == null ? '—' : `${p.conversion}%`])} />}
    </div>
  );
}

function Channels({ range }: { range: string }) {
  const { data, error } = useApi<any>(`/insights/channels?${range}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  if (!data.channels.length) return <Empty title="No channel data yet" action={<Link to="/links" className="btn btn-primary">Make tracking links</Link>}>Give each salesperson and each channel (WhatsApp, TikTok, LinkedIn…) its own link to see what each brings.</Empty>;
  const max = Math.max(...data.channels.map((c: any) => c.revenue), 1);
  return (
    <div className="space-y-3">
      {data.best && <p className="text-sm"><b>{data.best}</b> brings the most money{data.mostPeople && data.mostPeople !== data.best ? <>; <b>{data.mostPeople}</b> brings the most people</> : null}.</p>}
      <Table head={['Channel', 'Visits', 'People', 'Inquiries', 'New customers', 'Sales', 'Revenue']} rows={data.channels.map((c: any) => [
        <span className="block min-w-32"><b>{c.channel}</b><Bar value={c.revenue} max={max} /></span>, c.visits, c.people, c.inquiries, c.newCustomers, c.sales, ksh(c.revenue),
      ])} />
      <p className="text-xs text-muted">Visits and people are counted through tracking links; sales are credited to the channel that first brought each customer.</p>
    </div>
  );
}

function Team() {
  const year = new Date().getFullYear();
  const { data, error } = useApi<any>(`/insights/salespeople?year=${year}`);
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  if (!data.people.length) return <Empty title="No sales credited yet this year" />;
  return (
    <div className="space-y-3">
      {data.leader && <p className="flex items-center gap-2 text-sm"><Crown className="h-5 w-5 text-gold" /> Salesperson of {year} so far: <b>{data.leader.name}</b>, {ksh(data.leader.sold + data.leader.broughtRevenue)} credited.</p>}
      <Table head={['Person', 'Sold', 'Customers brought', 'Their spend', 'Link visits', 'Inquiries', 'Bought', 'Answers in']} rows={data.people.map((p: any, i: number) => [
        <span><b>{i + 1}. {p.name}</b><span className="block text-xs text-muted">{p.role}</span></span>, ksh(p.sold), p.customersBrought, ksh(p.broughtRevenue), p.linkVisits, p.inquiries,
        p.conversion == null ? '—' : `${p.conversion}%`, p.averageHoursToAnswer == null ? '—' : `${p.averageHoursToAnswer} h`,
      ])} />
      <p className="text-xs text-muted">“Sold” is invoices they made; “customers brought” came through their own tracking link and keep counting for them on every later sale.</p>
    </div>
  );
}

function Accuracy() {
  const { data, error } = useApi<any>('/insights/accuracy');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-4">
      {data.linesCounted === 0 ? (
        <Empty title="Stock hasn’t been counted yet" action={<Link to="/stock" className="btn btn-primary">Count stock</Link>}>Count the shelf to see whether the system matches it. Start with the list below: the most valuable stock first.</Empty>
      ) : (
        <p className="text-sm"><b className={cn('text-lg', data.accuracy >= 95 ? 'text-good' : 'text-warn')}>{data.accuracy}%</b> of {data.linesCounted} counted lines matched the system exactly in the last {data.days} days. Differences were worth {ksh(data.differenceValue)}, of which <b className="text-bad">{ksh(data.missingValue)}</b> was missing.</p>
      )}
      {data.mostOff.length > 0 && <Table head={['Most often off', 'Times', 'Short', 'Over', 'Value']} rows={data.mostOff.map((i: any) => [i.name, i.times, i.short_, i.over, ksh(i.value)])} />}
      <h2 className="flex items-center gap-2 text-sm font-semibold"><PackageCheck className="h-4 w-4" /> Count these next</h2>
      <p className="text-sm text-muted">The most valuable stock not counted in a month{data.neverCounted ? ` (${data.neverCounted} item${data.neverCounted === 1 ? ' has' : 's have'} never been counted)` : ''}.</p>
      <Table head={['Product', 'System says', 'Worth', 'Last counted']} rows={data.countNext.map((i: any) => [i.name, `${i.onHand} ${i.unit}`, ksh(i.value), i.lastCounted ? day(i.lastCounted) : 'Never'])} />
    </div>
  );
}

export function IntelligenceHint() {
  return <span className="inline-flex items-center gap-1 text-xs text-indigo"><Sparkles className="h-3.5 w-3.5" /> Kabuor Intelligence</span>;
}

function QuoteVariance({ days }: { days: string }) {
  const { data, error, loading } = useApi<any>(`/insights/quote-variance?days=${days}`);
  if (loading) return <Loading />;
  if (error) return <PageError>{error}</PageError>;
  if (!data || data.reports.length === 0) {
    return <Empty title="No quote-to-invoice conversions">No invoices were converted from quotes in the selected {days} days.</Empty>;
  }

  return (
    <div className="space-y-6">
      {/* Top metrics */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted uppercase">Total Quoted</p>
          <p className="mt-1 text-xl font-bold">{ksh(data.totalQuoted)}</p>
          <p className="mt-0.5 text-xs text-muted">{data.count} quotes converted</p>
        </div>
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted uppercase">Total Invoiced</p>
          <p className="mt-1 text-xl font-bold">{ksh(data.totalInvoiced)}</p>
          <p className="mt-0.5 text-xs text-muted">What actually left the business</p>
        </div>
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted uppercase">Net Variance</p>
          <p className={cn('mt-1 text-xl font-bold', data.netVariance < 0 ? 'text-warn' : data.netVariance > 0 ? 'text-indigo' : 'text-good')}>
            {data.netVariance === 0 ? '0' : `${data.netVariance > 0 ? '+' : ''}${ksh(data.netVariance)}`}
          </p>
          <p className="mt-0.5 text-xs text-muted">{data.discountedCount} discounted · {data.identicalCount} exact</p>
        </div>
        <div className="card p-4">
          <p className="text-xs font-semibold text-muted uppercase">Audit Coverage</p>
          <p className="mt-1 text-xl font-bold text-good">100%</p>
          <p className="mt-0.5 text-xs text-muted">AI Agent audits every difference</p>
        </div>
      </div>

      {/* Reports Table with Agent Explanations */}
      <div className="card divide-y divide-line">
        <div className="p-4 bg-paper/50 flex items-center justify-between">
          <div>
            <h3 className="font-semibold text-ink">Converted Sales & AI Audit Log</h3>
            <p className="text-xs text-muted">Every quote compared line-by-line with its issued invoice to show management what actually happens.</p>
          </div>
          <span className="rounded-full bg-brand-soft px-3 py-1 text-xs font-semibold text-brand">
            {data.reports.length} audits
          </span>
        </div>

        {data.reports.map((r: any) => {
          const isIdentical = r.status === 'Identical';
          const isDiscount = r.varianceAmount < 0;

          return (
            <div key={r.invoiceId} className="p-5 hover:bg-paper/30 transition">
              <div className="flex flex-wrap items-start justify-between gap-4">
                <div>
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-bold text-ink">{r.customerName}</span>
                    <span className="text-xs text-muted">· {r.invoiceNumber} (from {r.quoteNumber})</span>
                    <span className="rounded bg-brand-soft px-2 py-0.5 text-2xs font-semibold text-brand">
                      Brought by: {r.broughtBy}
                    </span>
                    <span className={cn('rounded-full px-2.5 py-0.5 text-xs font-bold',
                      isIdentical ? 'bg-good-soft text-good' : isDiscount ? 'bg-warn-soft text-warn' : 'bg-indigo-soft text-indigo'
                    )}>
                      {isIdentical ? 'Exact match' : `${r.varianceAmount > 0 ? '+' : ''}${ksh(r.varianceAmount)} (${r.variancePercent > 0 ? '+' : ''}${r.variancePercent}%)`}
                    </span>
                  </div>
                  <p className="mt-1 text-xs text-muted">
                    Quoted by {r.quotedBy} ({day(r.quoteDate)}) · Invoiced by {r.invoicedBy} ({day(r.invoiceDate)})
                  </p>
                </div>

                <div className="text-right">
                  <div className="flex items-center gap-3 text-sm">
                    <span className="text-muted">Quoted: <b className="text-ink">{ksh(r.quotedTotal)}</b></span>
                    <span className="text-muted">→</span>
                    <span>Invoiced: <b className="text-ink">{ksh(r.invoicedTotal)}</b></span>
                  </div>
                  <Link className="mt-1 inline-block text-xs font-semibold text-brand hover:underline" to={`/invoices/${r.invoiceId}`}>
                    Open invoice & full line audit →
                  </Link>
                </div>
              </div>

              {/* AI Agent Reasoning & Explanations */}
              <div className="mt-3.5 rounded-lg border border-line bg-paper/60 p-3 text-xs">
                <div className="flex items-start gap-2">
                  <span className="mt-0.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full bg-ink text-2xs font-bold text-white">AI</span>
                  <div className="space-y-1">
                    <p className="font-medium text-ink">{r.agentSummary}</p>
                    {r.agentReasons?.length > 0 && (
                      <ul className="list-inside list-disc text-muted space-y-0.5">
                        {r.agentReasons.map((reason: string, i: number) => (
                          <li key={i}>{reason}</li>
                        ))}
                      </ul>
                    )}
                  </div>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}
