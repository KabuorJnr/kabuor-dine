// The platform console for Kabuor Builds (Platform:Admins only). It answers, in this order: does anything need me
// (trials ending, businesses about to lock or gone quiet, customers waiting, failing checks)? How is Kabuor doing?
// And for one business: which plan, access until when, who uses it, and changing that.
// Plain white surfaces and ink type; colour only where it carries state.
import { useCallback, useEffect, useId, useState, type ReactNode } from 'react';
import { useSearchParams } from 'react-router';
import {
  Activity,
  AlertTriangle,
  ArrowDown,
  ArrowUp,
  BarChart3,
  Building2,
  CheckCircle2,
  ChevronRight,
  Loader2,
  MessageSquare,
  RefreshCw,
  Send,
  Server,
  ShieldAlert,
  Trash2,
  Zap,
} from 'lucide-react';
import { api, useApi } from '../lib/api';
import { cn, dateTime, day, dayYear, ksh, kshShort } from '../lib/format';
import { Chips, Empty, ErrorNote, Loading, SearchBox, Sheet, Stat, Status, PageError } from '../components/ui';

type Tone = 'good' | 'warn' | 'bad';
type Check = { key: string; name: string; status: Tone; summary: string };

type Health = {
  status: Tone;
  checkedAt: string;
  server: { environment: string; version: string; startedAt: string; uptimeHours: number; memoryMb: number; heapMb: number; threads: number; dotnet: string; os: string };
  configured: Record<string, boolean>;
  checks: Check[];
  demoTraffic: boolean;
  traffic: {
    series: { at: string; requests: number; errors: number; rejected: number; avgMs: number }[];
    requests: number;
    errors: number;
    rejected: number;
    errorRate: number;
    p50: number;
    p95: number;
    p99: number;
    slowest: { route: string; requests: number; avgMs: number; maxMs: number; errors: number }[];
    failing: { route: string; errors: number; requests: number }[];
  };
};

type Resources = {
  scaling: { status: 'optimal' | 'scale_up_recommended' | 'attention_needed'; summary: string; recommendations: string[] };
  memory: { workingSetMb: number; heapMb: number; limitMb: number; gen0: number; gen1: number; gen2: number };
  cpu: { threads: number; busyWorkerThreads: number; maxWorkerThreads: number };
  database: { sizeMb: number | null; connections: number | null };
  uptime: { startedAt: string; uptimeHours: number };
};

type State = 'paying' | 'trial' | 'grace' | 'locked';

type BusinessItem = {
  id: string;
  name: string;
  email: string | null;
  phone: string | null;
  county: string | null;
  plan: string;
  createdAt: string;
  trialEndsAt: string | null;
  paidUntil: string | null;
  state: State;
  accessEndsAt: string;
  isDemo: boolean;
  usersCount: number;
  invoicesCount: number;
  invoiceValue: number;
  lastInvoiceOn: string | null;
  lastActive: string | null;
};

type BusinessDetail = {
  id: string;
  name: string;
  email: string | null;
  phone: string | null;
  county: string | null;
  kraPin: string | null;
  vatRegistered: boolean;
  plan: string;
  industry: string | null;
  mpesaShortcode: string | null;
  createdAt: string;
  trialEndsAt: string | null;
  paidUntil: string | null;
  state: State;
  accessEndsAt: string;
  isDemo: boolean;
  users: { id: string; name: string; email: string; phone: string | null; role: string; active: boolean; createdAt: string; lastSignInAt: string | null }[];
  invoices: { id: string; number: string; customerName: string; total: number; balance: number; status: string; issueDate: string; dueDate: string | null }[];
  invoicedTotal: number;
  footprint: Record<'users' | 'customers' | 'items' | 'invoices' | 'payments' | 'assets' | 'inquiries' | 'employees' | 'journals', number>;
  canWipe: boolean;
};

/** What the plan and access sheet needs; a row of the list and the full detail both have it. */
type AccessTarget = Pick<BusinessItem, 'id' | 'name' | 'plan' | 'state' | 'accessEndsAt' | 'paidUntil'>;

type Plan = { id: string; name: string; monthly: number; users: number; staff: number };

type IssueMessage = { id: string; from: 'customer' | 'staff' | 'agent' | string; author: string | null; text: string; at: string };

type IssueItem = {
  id: string;
  companyId: string;
  companyName: string;
  number: string;
  name: string;
  phone: string | null;
  email: string | null;
  subject: string;
  channel: string;
  status: 'Open' | 'Answered' | 'Quoted' | 'Won' | 'Lost';
  createdAt: string;
  firstResponseAt: string | null;
  messages: IssueMessage[];
};

type Erasure = { companyId: string; name: string; reason: string; removed: number; actor: string; at: string };

type Days = '7' | '30' | '90' | '365';

type Analytics = {
  days: number;
  businesses: {
    total: number;
    newInPeriod: number;
    newBefore: number;
    active7d: number;
    activeInPeriod: number;
    trial: number;
    paying: number;
    grace: number;
    locked: number;
    byPlan: { id: string; name: string; businesses: number; monthly: number; mrr: number }[];
  };
  revenue: { mrr: number; arr: number; collectedInPeriod: number };
  users: { total: number; signedIn7d: number; signedInPeriod: number };
  volume: {
    invoices: number;
    invoiceValue: number;
    invoiceValueChange: number | null;
    payments: number;
    paymentValue: number;
    paymentValueChange: number | null;
    inquiries: number;
    agentTasks: number;
    assets: number;
  };
  series: { day: string; signups: number; invoiceValue: number; paymentValue: number }[];
  top: { id: string; name: string; county: string; createdAt: string; state: State; plan: string; users: number; invoices: number; invoiceValue: number; lastInvoice: string | null; lastSignIn: string | null }[];
};

// ----------------------------------------------------------------------------- words and numbers
const count = (n: number) => n.toLocaleString('en-KE');
const plural = (n: number, one: string, many = `${one}s`) => `${count(n)} ${n === 1 ? one : many}`;
const pct = (n: number, of: number) => (of > 0 ? Math.round((100 * n) / of) : 0);
const short = (n: number) => (n >= 1e6 ? `${(n / 1e6).toFixed(1)}M` : n >= 1e3 ? `${(n / 1e3).toFixed(n >= 1e4 ? 0 : 1)}K` : String(Math.round(n)));
const daysUntil = (iso: string) => Math.ceil((Date.parse(iso) - Date.now()) / 864e5);
const DAY = 864e5;

/** "Just now", "40 min ago", "5 h ago", "3 days ago", then the date. */
function ago(iso?: string | null) {
  if (!iso) return 'Never';
  const min = (Date.now() - Date.parse(iso)) / 6e4;
  if (min < 1) return 'Just now';
  if (min < 60) return `${Math.round(min)} min ago`;
  if (min < 60 * 24) return `${Math.round(min / 60)} h ago`;
  if (min < 60 * 24 * 30) return `${plural(Math.round(min / 1440), 'day')} ago`;
  return dayYear(iso);
}

/** How long something has waited: "25 min", "6 h", "3 days". */
function waited(iso: string) {
  const min = Math.max(0, (Date.now() - Date.parse(iso)) / 6e4);
  return min < 60 ? `${Math.round(min)} min` : min < 60 * 24 ? `${Math.round(min / 60)} h` : plural(Math.round(min / 1440), 'day');
}

const STATE: Record<State, { label: string; tone: 'good' | 'info' | 'warn' | 'bad' }> = {
  paying: { label: 'Paying', tone: 'good' },
  trial: { label: 'Trial', tone: 'info' },
  grace: { label: 'Grace', tone: 'warn' },
  locked: { label: 'Locked', tone: 'bad' },
};

/** The one fact about access a founder acts on: when the current state ends. */
function accessText(state: State, endsAt: string) {
  const d = daysUntil(endsAt);
  const when = d <= 0 ? 'today' : d === 1 ? 'tomorrow' : `in ${d} days`;
  if (state === 'paying') return `Paid until ${dayYear(endsAt)}`;
  if (state === 'trial') return `Trial ends ${when}`;
  if (state === 'grace') return `Read-only ${when}`;
  return d >= 0 ? 'Locked today' : `Locked ${plural(-d, 'day')} ago`;
}

const endingSoon = (b: BusinessItem) => b.state === 'trial' && daysUntil(b.accessEndsAt) <= 7;
const urgent = (b: Pick<BusinessItem, 'state' | 'accessEndsAt'>) => b.state === 'grace' || (b.state === 'trial' && daysUntil(b.accessEndsAt) <= 7);
const quiet = (b: BusinessItem) => b.state !== 'locked' && (!b.lastActive || Date.now() - Date.parse(b.lastActive) > 14 * DAY);

const primaryBtn = 'btn bg-ink text-white hover:bg-ink-2';
const dangerBtn = 'btn bg-bad text-white hover:brightness-110';

function DemoPill() {
  return <span className="shrink-0 rounded-sm bg-paper px-1.5 py-0.5 text-[11px] font-semibold text-muted ring-1 ring-line">Demo</span>;
}

function Monogram({ name }: { name: string }) {
  const initials = name
    .split(/\s+/)
    .filter(Boolean)
    .map((w) => w[0])
    .slice(0, 2)
    .join('')
    .toUpperCase();
  return <span className="grid h-9 w-9 shrink-0 place-items-center rounded-md bg-paper text-xs font-bold text-ink-2 ring-1 ring-line">{initials || '?'}</span>;
}

function SectionTitle({ children, aside }: { children: ReactNode; aside?: ReactNode }) {
  return (
    <div className="mb-3 flex flex-wrap items-baseline justify-between gap-2">
      <h2 className="font-display text-lg font-semibold">{children}</h2>
      {aside && <div className="text-sm text-muted">{aside}</div>}
    </div>
  );
}

/** A two- or three-way switch where every option is visible. */
function Segmented<T extends string>({ value, onChange, options, label }: { value: T; onChange: (v: T) => void; options: [T, string][]; label: string }) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex rounded-md bg-paper p-0.5 ring-1 ring-line">
      {options.map(([id, text]) => (
        <button
          key={id}
          type="button"
          role="radio"
          aria-checked={value === id}
          onClick={() => onChange(id)}
          className={cn('rounded-[6px] px-3 py-1.5 text-sm font-semibold transition', value === id ? 'bg-ink text-white' : 'text-ink-2 hover:bg-white')}
        >
          {text}
        </button>
      ))}
    </div>
  );
}

/** Up or down against the period before, or why there's nothing to compare with. */
function Change({ value, days }: { value: number | null; days: string }) {
  if (value === null) return <span>nothing to compare in the {days} days before</span>;
  return (
    <span className={value >= 0 ? 'font-semibold text-good' : 'font-semibold text-bad'}>
      {value >= 0 ? '▲' : '▼'} {Math.abs(value)}% vs the {days} days before
    </span>
  );
}

// ----------------------------------------------------------------------------- line chart
function LineChart({
  data,
  height = 200,
  fmt = (v: number) => String(v),
  axis = short,
  empty = 'Nothing recorded in this window yet',
}: {
  data: { label: string; value: number }[];
  height?: number;
  fmt?: (v: number) => string;
  axis?: (v: number) => string;
  empty?: string;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const gradId = useId();

  if (data.length === 0 || data.every((d) => d.value === 0)) {
    return (
      <div className="grid place-items-center rounded-md border border-dashed border-line text-sm text-muted" style={{ height }}>
        {empty}
      </div>
    );
  }

  const pad = { top: 16, right: 16, bottom: 28, left: 56 };
  const width = 640;
  const innerW = width - pad.left - pad.right;
  const innerH = height - pad.top - pad.bottom;
  const max = Math.max(...data.map((d) => d.value), 1);
  const x = (i: number) => (data.length === 1 ? pad.left + innerW / 2 : pad.left + (i / (data.length - 1)) * innerW);
  const y = (v: number) => pad.top + innerH - (v / max) * innerH;
  const pts = data.map((d, i) => ({ x: x(i), y: y(d.value) }));

  // A smooth line through the points (Catmull-Rom as cubic Béziers), never curving below zero.
  const floor = (v: number) => Math.min(v, pad.top + innerH);
  let line = `M ${pts[0].x} ${pts[0].y}`;
  for (let i = 0; i < pts.length - 1; i++) {
    const p0 = pts[Math.max(i - 1, 0)];
    const p1 = pts[i];
    const p2 = pts[i + 1];
    const p3 = pts[Math.min(i + 2, pts.length - 1)];
    line += ` C ${p1.x + (p2.x - p0.x) / 6} ${floor(p1.y + (p2.y - p0.y) / 6)}, ${p2.x - (p3.x - p1.x) / 6} ${floor(p2.y - (p3.y - p1.y) / 6)}, ${p2.x} ${p2.y}`;
  }
  const area = `${line} L ${pts[pts.length - 1].x} ${pad.top + innerH} L ${pts[0].x} ${pad.top + innerH} Z`;
  const ticks = [0, max / 2, max];
  const step = Math.max(1, Math.ceil(data.length / 6));
  const labels = data.map((_, i) => i).filter((i) => i % step === 0 || i === data.length - 1);
  const total = data.reduce((s, d) => s + d.value, 0);

  return (
    <div className="relative w-full select-none text-ink" onMouseLeave={() => setHover(null)}>
      <svg viewBox={`0 0 ${width} ${height}`} className="h-auto w-full overflow-visible" style={{ maxHeight: height }} role="img" aria-label={`Line chart, total ${fmt(total)}`}>
        <defs>
          <linearGradient id={gradId} x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stopColor="currentColor" stopOpacity="0.10" />
            <stop offset="100%" stopColor="currentColor" stopOpacity="0" />
          </linearGradient>
        </defs>
        {ticks.map((t, i) => (
          <g key={i}>
            <line x1={pad.left} x2={width - pad.right} y1={y(t)} y2={y(t)} className="stroke-line" strokeDasharray={i === 0 ? undefined : '4 4'} />
            <text x={pad.left - 8} y={y(t) + 4} textAnchor="end" className="num fill-muted text-[11px]">
              {axis(t)}
            </text>
          </g>
        ))}
        <path d={area} fill={`url(#${gradId})`} />
        <path d={line} fill="none" stroke="currentColor" strokeWidth="2.25" strokeLinecap="round" />
        {labels.map((i) => (
          <text key={i} x={x(i)} y={height - 6} textAnchor="middle" className="fill-muted text-[11px]">
            {data[i].label}
          </text>
        ))}
        {hover !== null && (
          <g>
            <line x1={pts[hover].x} x2={pts[hover].x} y1={pad.top} y2={pad.top + innerH} stroke="currentColor" strokeOpacity="0.35" strokeDasharray="3 3" />
            <circle cx={pts[hover].x} cy={pts[hover].y} r="4.5" fill="currentColor" stroke="white" strokeWidth="2" />
          </g>
        )}
        {data.map((_, i) => (
          <rect key={i} x={x(i) - innerW / data.length / 2} y={pad.top} width={innerW / data.length} height={innerH} fill="transparent" onMouseEnter={() => setHover(i)} />
        ))}
      </svg>
      {hover !== null && (
        <div
          className="pointer-events-none absolute top-2 z-10 rounded-md bg-white px-3 py-2 text-xs shadow-lg ring-1 ring-line"
          style={{ left: `${Math.min(85, Math.max(15, (pts[hover].x / width) * 100))}%`, transform: 'translateX(-50%)' }}
        >
          <div className="text-muted">{data[hover].label}</div>
          <div className="num text-sm font-bold">{fmt(data[hover].value)}</div>
        </div>
      )}
    </div>
  );
}

// ----------------------------------------------------------------------------- entry
export default function Platform() {
  const me = useApi<{ allowed: boolean; why: string }>('/platform/me');
  if (me.error) return <PageError>{me.error}</PageError>;
  if (!me.data) return <Loading />;
  if (!me.data.allowed) {
    return (
      <div className="mx-auto max-w-md py-16 text-center">
        <ShieldAlert className="mx-auto h-10 w-10 text-ink" />
        <h1 className="mt-4 font-display text-xl font-semibold">The platform console is for Kabuor admins</h1>
        <p className="mt-2 text-sm text-muted">{me.data.why}</p>
      </div>
    );
  }
  return <Console />;
}

type Tab = 'businesses' | 'growth' | 'inquiries' | 'health' | 'server' | 'erase';
const TABS: { id: Tab; label: string; icon: typeof Building2 }[] = [
  { id: 'businesses', label: 'Businesses', icon: Building2 },
  { id: 'growth', label: 'Growth & revenue', icon: BarChart3 },
  { id: 'inquiries', label: 'Inquiries', icon: MessageSquare },
  { id: 'health', label: 'Traffic & health', icon: Activity },
  { id: 'server', label: 'Server', icon: Server },
  { id: 'erase', label: 'Erase data', icon: Trash2 },
];
type Show = 'all' | 'paying' | 'trial' | 'ending' | 'grace' | 'locked' | 'quiet';

/** Whether the seeded demo businesses count. Remembered on this device. */
function useDemoChoice() {
  const [demo, setDemo] = useState(() => {
    try {
      return localStorage.getItem('kerp-platform-demo') !== 'off';
    } catch {
      return true;
    }
  });
  const set = (v: boolean) => {
    setDemo(v);
    try {
      localStorage.setItem('kerp-platform-demo', v ? 'on' : 'off');
    } catch {}
  };
  return [demo, set] as const;
}

function Console() {
  const [params, setParams] = useSearchParams();
  const tab = (TABS.some((t) => t.id === params.get('tab')) ? params.get('tab') : 'businesses') as Tab;
  const show = (params.get('show') ?? 'all') as Show;
  const openId = params.get('b');
  const patch = useCallback(
    (changes: Record<string, string | null>) =>
      setParams((p) => {
        const next = new URLSearchParams(p);
        for (const [k, v] of Object.entries(changes)) v === null ? next.delete(k) : next.set(k, v);
        return next;
      }),
    [setParams],
  );

  const [demo, setDemo] = useDemoChoice();
  const [days, setDays] = useState<Days>('30');
  const health = useApi<Health>(`/platform/health${demo ? '?demo=true' : ''}`);
  const businesses = useApi<BusinessItem[]>('/platform/businesses');
  const plans = useApi<Plan[]>('/platform/plans');
  const issues = useApi<IssueItem[]>('/platform/issues');
  const analytics = useApi<Analytics>(`/platform/analytics?days=${days}${demo ? '' : '&demo=false'}`);
  const [managing, setManaging] = useState<AccessTarget | null>(null);
  const [saves, setSaves] = useState(0); // reopens the business sheet with fresh data after a plan change
  const [updatedAt, setUpdatedAt] = useState(() => new Date());

  // The console sits on pure white; cards keep their hairline borders for structure.
  useEffect(() => {
    document.body.classList.add('bg-white');
    return () => document.body.classList.remove('bg-white');
  }, []);

  // Health is live: refresh it every 30 seconds while the console is on screen.
  useEffect(() => {
    const t = window.setInterval(() => document.visibilityState === 'visible' && health.reload(), 30_000);
    return () => window.clearInterval(t);
  }, [health.reload]);

  const reloadAll = () => {
    health.reload();
    businesses.reload();
    plans.reload();
    issues.reload();
    analytics.reload();
    setUpdatedAt(new Date());
  };

  const all = businesses.data ?? [];
  const demoCount = all.filter((b) => b.isDemo).length;
  const list = demo ? all : all.filter((b) => !b.isDemo);
  const planById = new Map((plans.data ?? []).map((p) => [p.id, p]));
  const waiting = (issues.data ?? []).filter((i) => i.status === 'Open');
  const problems = health.data?.checks.filter((c) => c.status !== 'good') ?? [];

  const a = analytics.data;
  const ending = list.filter(endingSoon);
  const grace = list.filter((b) => b.state === 'grace');
  const locked = list.filter((b) => b.state === 'locked');
  const quietOnes = list.filter(quiet);
  const attention: { n: number; text: string; tone: 'warn' | 'bad' | 'plain'; go: () => void }[] = [
    { n: ending.length, text: ending.length === 1 ? 'trial ends within 7 days' : 'trials end within 7 days', tone: 'warn' as const, go: () => patch({ tab: 'businesses', show: 'ending' }) },
    { n: grace.length, text: 'in grace, read-only within a week', tone: 'warn' as const, go: () => patch({ tab: 'businesses', show: 'grace' }) },
    { n: waiting.length, text: waiting.length === 1 ? 'customer waiting on a reply' : 'customers waiting on a reply', tone: 'warn' as const, go: () => patch({ tab: 'inquiries', show: null }) },
    { n: problems.length, text: problems.length === 1 ? 'health check needs a look' : 'health checks need a look', tone: problems.some((p) => p.status === 'bad') ? ('bad' as const) : ('warn' as const), go: () => patch({ tab: 'health', show: null }) },
    { n: quietOnes.length, text: 'no sign-in for 14+ days', tone: 'plain' as const, go: () => patch({ tab: 'businesses', show: 'quiet' }) },
    { n: locked.length, text: 'locked, can’t record sales', tone: 'plain' as const, go: () => patch({ tab: 'businesses', show: 'locked' }) },
  ].filter((x) => x.n > 0);
  const attentionReady = businesses.data && issues.data && health.data;

  const badge: Partial<Record<Tab, { n: number; tone: 'plain' | 'warn' | 'bad' }>> = {
    businesses: businesses.data ? { n: list.length, tone: 'plain' } : undefined,
    inquiries: waiting.length ? { n: waiting.length, tone: 'warn' } : undefined,
    health: problems.length ? { n: problems.length, tone: problems.some((p) => p.status === 'bad') ? 'bad' : 'warn' } : undefined,
  };

  return (
    <div className="min-w-0 space-y-6 pb-12">
      <header className="flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
        <div className="min-w-0">
          <h1 className="font-display text-[26px] leading-tight font-semibold tracking-tight sm:text-3xl">Founder Command Center</h1>
          <p className="mt-1 text-sm text-muted">
            Every business on Kabuor and the server they run on · updated {updatedAt.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' })}
          </p>
        </div>
        <div className="flex items-center gap-2">
          {demoCount > 0 && (
            <label className="flex cursor-pointer items-center gap-2 rounded-md bg-white px-3 py-2 text-sm font-medium ring-1 ring-line">
              <input type="checkbox" className="h-4 w-4 accent-ink" checked={demo} onChange={(e) => setDemo(e.target.checked)} />
              Include {demoCount} demo businesses
            </label>
          )}
          <button className="btn btn-ghost" onClick={reloadAll} aria-label="Refresh">
            <RefreshCw className={cn('h-4 w-4', (businesses.loading || analytics.loading || health.loading) && 'animate-spin')} />
            <span className="hidden sm:inline">Refresh</span>
          </button>
        </div>
      </header>

      {/* How Kabuor is doing, each number with what it's out of. */}
      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Businesses" value={a ? count(a.businesses.total) : '—'}>
          {a && <>+{count(a.businesses.newInPeriod)} in {days} days{!demo && demoCount > 0 && ' · demo left out'}</>}
        </Stat>
        <Stat label="Paying" value={a ? count(a.businesses.paying) : '—'}>
          {a && <>{pct(a.businesses.paying, a.businesses.total)}% of businesses · {count(a.businesses.trial)} on trial</>}
        </Stat>
        <Stat label="Monthly revenue" value={a ? ksh(a.revenue.mrr) : '—'}>
          {a && <>recurring · {kshShort(a.revenue.arr)} a year · {kshShort(a.revenue.collectedInPeriod)} collected in {days} days</>}
        </Stat>
        <Stat label="Active this week" value={a ? count(a.businesses.active7d) : '—'}>
          {a && <>{pct(a.businesses.active7d, a.businesses.total)}% of businesses · {plural(a.users.signedIn7d, 'person', 'people')} signed in</>}
        </Stat>
      </div>
      {analytics.error && <ErrorNote>{analytics.error}</ErrorNote>}

      {/* What needs the founder today; each item opens the list it counts. */}
      {attentionReady && (
        <section aria-label="Needs you" className="card p-4">
          {attention.length === 0 ? (
            <p className="flex items-center gap-2 text-sm font-medium text-good">
              <CheckCircle2 className="h-4 w-4" /> Nothing needs you right now: no trials ending, nobody waiting, every check passing.
            </p>
          ) : (
            <div className="flex flex-wrap items-center gap-2">
              <span className="mr-1 text-sm font-semibold">Needs you</span>
              {attention.map((x) => (
                <button
                  key={x.text}
                  onClick={x.go}
                  className={cn(
                    'inline-flex items-center gap-1.5 rounded-full px-3 py-1.5 text-sm font-medium ring-1 transition hover:brightness-95',
                    x.tone === 'bad' ? 'bg-bad-soft text-bad ring-bad/20' : x.tone === 'warn' ? 'bg-warn-soft text-warn ring-warn/20' : 'bg-white text-ink-2 ring-line',
                  )}
                >
                  <span className="num font-bold">{count(x.n)}</span> {x.text} <ChevronRight className="h-3.5 w-3.5" />
                </button>
              ))}
            </div>
          )}
        </section>
      )}

      <nav className="no-scrollbar -mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0" aria-label="Console sections">
        <div role="tablist" className="flex w-max gap-1 rounded-lg bg-paper p-1 ring-1 ring-line">
          {TABS.map((t) => {
            const active = tab === t.id;
            const b = badge[t.id];
            return (
              <button
                key={t.id}
                role="tab"
                aria-selected={active}
                onClick={() => patch({ tab: t.id, show: null })}
                className={cn('flex items-center gap-2 rounded-md px-3.5 py-2 text-sm font-semibold transition', active ? 'bg-ink text-white' : 'text-ink-2 hover:bg-white')}
              >
                <t.icon className="h-4 w-4" /> {t.label}
                {b && (
                  <span
                    className={cn(
                      'num rounded-full px-1.5 text-xs font-bold',
                      active ? 'bg-white/20 text-white' : b.tone === 'bad' ? 'bg-bad-soft text-bad' : b.tone === 'warn' ? 'bg-warn-soft text-warn' : 'bg-white text-muted',
                    )}
                  >
                    {count(b.n)}
                  </span>
                )}
              </button>
            );
          })}
        </div>
      </nav>

      {tab === 'businesses' && (
        <BusinessesView
          list={businesses.data ? list : null}
          error={businesses.error}
          planById={planById}
          show={show}
          onShow={(s) => patch({ show: s === 'all' ? null : s })}
          onOpen={(id) => patch({ b: id })}
        />
      )}
      {tab === 'growth' && <GrowthView analytics={a} error={analytics.error} days={days} setDays={setDays} onOpen={(id) => patch({ b: id })} />}
      {tab === 'inquiries' && <InquiriesView issues={issues.data} loading={issues.loading} error={issues.error} onReload={issues.reload} />}
      {tab === 'health' && <HealthView health={health.data} error={health.error} />}
      {tab === 'server' && <ServerView health={health.data} onSeeded={businesses.reload} />}
      {tab === 'erase' && (
        <EraseView
          businesses={businesses.data}
          preselected={params.get('erase')}
          onErased={() => {
            businesses.reload();
            analytics.reload();
            issues.reload();
            patch({ erase: null });
          }}
        />
      )}

      {openId && (
        <BusinessSheet
          key={`${openId}-${saves}`}
          id={openId}
          planById={planById}
          onClose={() => patch({ b: null })}
          onManage={setManaging}
          onErase={(id) => patch({ b: null, tab: 'erase', erase: id, show: null })}
        />
      )}

      {managing && (
        <AccessSheet
          business={managing}
          plans={plans.data ?? []}
          onClose={() => setManaging(null)}
          onSaved={() => {
            setManaging(null);
            setSaves((n) => n + 1);
            businesses.reload();
            analytics.reload();
          }}
        />
      )}
    </div>
  );
}

// ----------------------------------------------------------------------------- businesses
type SortKey = 'name' | 'access' | 'plan' | 'users' | 'invoiced' | 'active';
const ACCESS_RANK: Record<State, number> = { grace: 0, trial: 1, paying: 2, locked: 3 };

function BusinessesView({
  list,
  error,
  planById,
  show,
  onShow,
  onOpen,
}: {
  list: BusinessItem[] | null;
  error: string | null;
  planById: Map<string, Plan>;
  show: Show;
  onShow: (s: Show) => void;
  onOpen: (id: string) => void;
}) {
  const [q, setQ] = useState('');
  const [sort, setSort] = useState<{ key: SortKey; desc: boolean }>({ key: 'invoiced', desc: true });

  if (error) return <PageError>{error}</PageError>;
  if (!list) return <Loading />;

  const n = (f: (b: BusinessItem) => boolean) => list.filter(f).length;
  const filters: [Show, string, number][] = [
    ['all', 'All', list.length],
    ['paying', 'Paying', n((b) => b.state === 'paying')],
    ['trial', 'On trial', n((b) => b.state === 'trial')],
    ['ending', 'Trial ends in 7 days', n(endingSoon)],
    ['grace', 'Grace', n((b) => b.state === 'grace')],
    ['locked', 'Locked', n((b) => b.state === 'locked')],
    ['quiet', 'Quiet 14+ days', n(quiet)],
  ];
  const matches = (b: BusinessItem) => (show === 'all' ? true : show === 'ending' ? endingSoon(b) : show === 'quiet' ? quiet(b) : b.state === show);
  const planName = (b: BusinessItem) => planById.get(b.plan)?.name ?? (b.plan || 'No plan');
  const needle = q.trim().toLowerCase();
  const rows = list
    .filter(matches)
    .filter((b) => !needle || [b.name, b.email, b.phone, b.county, planName(b)].some((v) => (v ?? '').toLowerCase().includes(needle)));

  const value = (b: BusinessItem): number | string => {
    switch (sort.key) {
      case 'name':
        return b.name.toLowerCase();
      case 'access':
        return ACCESS_RANK[b.state] * 1e13 + Date.parse(b.accessEndsAt);
      case 'plan':
        return planById.get(b.plan)?.monthly ?? -1;
      case 'users':
        return b.usersCount;
      case 'invoiced':
        return b.invoiceValue;
      case 'active':
        return b.lastActive ? Date.parse(b.lastActive) : 0;
    }
  };
  rows.sort((x, y) => {
    const [vx, vy] = [value(x), value(y)];
    const c = typeof vx === 'string' ? vx.localeCompare(vy as string) : vx - (vy as number);
    return sort.desc ? -c : c;
  });
  const onSort = (key: SortKey) => setSort((s) => (s.key === key ? { key, desc: !s.desc } : { key, desc: key !== 'name' && key !== 'access' }));

  const Head = ({ k, children, right }: { k: SortKey; children: ReactNode; right?: boolean }) => (
    <th aria-sort={sort.key === k ? (sort.desc ? 'descending' : 'ascending') : 'none'} className={cn('px-4 py-2.5 font-semibold', right && 'text-right')}>
      <button type="button" onClick={() => onSort(k)} className={cn('inline-flex items-center gap-1 hover:text-ink', sort.key === k && 'text-ink')}>
        {children}
        {sort.key === k && (sort.desc ? <ArrowDown className="h-3.5 w-3.5" /> : <ArrowUp className="h-3.5 w-3.5" />)}
      </button>
    </th>
  );

  return (
    <section className="space-y-3">
      <SearchBox value={q} onChange={setQ} placeholder="Search name, email, phone, county or plan" />
      <Chips label="Show businesses" value={show} onChange={onShow} options={filters} />
      <p className="text-sm text-muted" aria-live="polite">
        {rows.length === list.length ? plural(list.length, 'business', 'businesses') : `${count(rows.length)} of ${plural(list.length, 'business', 'businesses')}`}
      </p>

      {rows.length === 0 ? (
        <Empty
          title="No businesses match"
          action={
            <button
              className="btn btn-ghost"
              onClick={() => {
                setQ('');
                onShow('all');
              }}
            >
              Show all businesses
            </button>
          }
        >
          {needle ? `Nothing matches “${q.trim()}”${show !== 'all' ? ' with this filter' : ''}.` : 'None are in this state right now.'}
        </Empty>
      ) : (
        <>
          {/* Phones: one card per business, the same facts in the same places. */}
          <ul className="card divide-y divide-line sm:hidden">
            {rows.map((b) => (
              <li key={b.id}>
                <button className="flex w-full items-start gap-3 p-4 text-left" onClick={() => onOpen(b.id)}>
                  <Monogram name={b.name} />
                  <span className="min-w-0 flex-1">
                    <span className="flex items-center gap-2">
                      <span className="truncate font-semibold">{b.name}</span>
                      {b.isDemo && <DemoPill />}
                    </span>
                    <span className={cn('mt-0.5 block text-sm', urgent(b) ? 'font-semibold text-warn' : 'text-muted')}>{accessText(b.state, b.accessEndsAt)}</span>
                    <span className="mt-1 block text-xs text-muted">
                      {planName(b)} · {plural(b.usersCount, 'login')} · active {ago(b.lastActive).toLowerCase()}
                    </span>
                  </span>
                  <span className="text-right">
                    <Status tone={STATE[b.state].tone}>{STATE[b.state].label}</Status>
                    <span className="num mt-1 block text-sm font-semibold">{kshShort(b.invoiceValue)}</span>
                  </span>
                </button>
              </li>
            ))}
          </ul>

          <div className="card hidden overflow-x-auto sm:block">
            <table className="w-full min-w-[56rem] text-left text-sm">
              <thead className="border-b border-line text-muted">
                <tr>
                  <Head k="name">Business</Head>
                  <Head k="access">Access</Head>
                  <Head k="plan">Plan</Head>
                  <Head k="users" right>
                    Logins
                  </Head>
                  <Head k="invoiced" right>
                    Invoiced
                  </Head>
                  <Head k="active">Last sign-in</Head>
                  <th className="w-8" aria-label="Open" />
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {rows.map((b) => {
                  const plan = planById.get(b.plan);
                  return (
                    <tr
                      key={b.id}
                      tabIndex={0}
                      onClick={() => onOpen(b.id)}
                      onKeyDown={(e) => e.key === 'Enter' && onOpen(b.id)}
                      className="cursor-pointer transition hover:bg-paper focus-visible:bg-paper"
                    >
                      <td className="max-w-[18rem] px-4 py-3">
                        <div className="flex min-w-0 items-center gap-3">
                          <Monogram name={b.name} />
                          <div className="min-w-0">
                            <div className="flex items-center gap-2">
                              <span className="truncate font-semibold">{b.name}</span>
                              {b.isDemo && <DemoPill />}
                            </div>
                            <div className="truncate text-xs text-muted">{[b.email || b.phone, b.county].filter(Boolean).join(' · ') || 'No contact on file'}</div>
                          </div>
                        </div>
                      </td>
                      <td className="px-4 py-3">
                        <Status tone={STATE[b.state].tone}>{STATE[b.state].label}</Status>
                        <div className={cn('mt-1 text-xs', urgent(b) ? 'font-semibold text-warn' : 'text-muted')}>{accessText(b.state, b.accessEndsAt)}</div>
                      </td>
                      <td className="px-4 py-3">
                        <div className="font-medium">{plan?.name ?? (b.plan || 'No plan')}</div>
                        <div className="num text-xs text-muted">{plan ? `${ksh(plan.monthly)}/month` : b.plan ? 'Not a current plan' : '—'}</div>
                      </td>
                      <td className="num px-4 py-3 text-right">{count(b.usersCount)}</td>
                      <td className="px-4 py-3 text-right">
                        <div className="num font-semibold">{kshShort(b.invoiceValue)}</div>
                        <div className="num text-xs text-muted">{plural(b.invoicesCount, 'invoice')}</div>
                      </td>
                      <td className={cn('px-4 py-3', quiet(b) ? 'font-medium text-warn' : 'text-muted')}>{ago(b.lastActive)}</td>
                      <td className="pr-3 text-faint">
                        <ChevronRight className="h-4 w-4" />
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </>
      )}
    </section>
  );
}

// ----------------------------------------------------------------------------- one business
function BusinessSheet({
  id,
  planById,
  onClose,
  onManage,
  onErase,
}: {
  id: string;
  planById: Map<string, Plan>;
  onClose: () => void;
  onManage: (b: AccessTarget) => void;
  onErase: (id: string) => void;
}) {
  const detail = useApi<BusinessDetail>(`/platform/businesses/${id}`);
  const b = detail.data;
  const plan = b ? planById.get(b.plan) : undefined;
  const footprint: [keyof BusinessDetail['footprint'], string][] = [
    ['customers', 'Customers'],
    ['invoices', 'Invoices'],
    ['payments', 'Payments'],
    ['items', 'Products'],
    ['employees', 'Employees'],
    ['users', 'Logins'],
    ['inquiries', 'Inquiries'],
    ['assets', 'Assets'],
    ['journals', 'Journals'],
  ];

  return (
    <Sheet open wide onClose={onClose} title={b?.name ?? 'Business'}>
      {detail.error ? (
        <ErrorNote>{detail.error}</ErrorNote>
      ) : !b ? (
        <Loading />
      ) : (
        <div className="space-y-6 text-sm">
          <section className="card p-5">
            <div className="flex flex-wrap items-start justify-between gap-4">
              <div className="min-w-0">
                <div className="flex flex-wrap items-center gap-2">
                  <Status tone={STATE[b.state].tone}>{STATE[b.state].label}</Status>
                  {b.isDemo && <DemoPill />}
                </div>
                <p className={cn('mt-2 font-display text-xl font-semibold', urgent(b) && 'text-warn')}>{accessText(b.state, b.accessEndsAt)}</p>
                <p className="mt-0.5 text-muted">
                  {plan ? `${plan.name} plan · ${ksh(plan.monthly)}/month` : b.plan ? `Plan “${b.plan}” (not a current plan)` : 'No plan chosen'} · joined {dayYear(b.createdAt)}
                </p>
              </div>
              <button className={primaryBtn} onClick={() => onManage(b)}>
                Change plan or access
              </button>
            </div>
          </section>

          <section>
            <SectionTitle>Details</SectionTitle>
            <dl className="grid grid-cols-1 gap-x-6 gap-y-3 sm:grid-cols-2 lg:grid-cols-3">
              {(
                [
                  ['Email', b.email],
                  ['Phone', b.phone],
                  ['County', b.county],
                  ['KRA PIN', b.kraPin],
                  ['VAT registered', b.vatRegistered ? 'Yes' : 'No'],
                  ['Industry', b.industry],
                  ['M-Pesa shortcode', b.mpesaShortcode],
                ] as [string, string | null][]
              ).map(([k, v]) => (
                <div key={k} className="min-w-0">
                  <dt className="text-xs text-muted">{k}</dt>
                  <dd className={cn('truncate font-medium', !v && 'font-normal text-faint')} title={v ?? undefined}>
                    {v || 'Not set'}
                  </dd>
                </div>
              ))}
            </dl>
            <p className="mt-3 font-mono text-xs break-all text-faint">ID {b.id}</p>
          </section>

          <section>
            <SectionTitle aside={`${ksh(b.invoicedTotal)} on the latest ${b.invoices.length}`}>What they keep in Kabuor</SectionTitle>
            <div className="grid grid-cols-3 gap-2 sm:grid-cols-5">
              {footprint.map(([k, label]) => (
                <div key={k} className="min-w-0 rounded-md bg-paper p-2.5 ring-1 ring-line">
                  <div className="num font-display text-lg font-semibold">{count(b.footprint[k])}</div>
                  <div className="truncate text-xs text-muted">{label}</div>
                </div>
              ))}
            </div>
          </section>

          <section>
            <SectionTitle aside={`${b.users.filter((u) => u.active).length} active`}>People with a login ({b.users.length})</SectionTitle>
            {b.users.length === 0 ? (
              <p className="text-muted">Nobody has a login yet.</p>
            ) : (
              <ul className="card divide-y divide-line">
                {b.users.map((u) => (
                  <li key={u.id} className="flex items-center justify-between gap-3 px-4 py-3">
                    <div className="min-w-0">
                      <div className="flex items-center gap-2">
                        <span className="truncate font-semibold">{u.name}</span>
                        {!u.active && <Status tone="plain">No access</Status>}
                      </div>
                      <div className="truncate text-xs text-muted">{[u.email, u.phone].filter(Boolean).join(' · ')}</div>
                    </div>
                    <div className="shrink-0 text-right">
                      <div className="text-xs font-semibold text-ink-2">{u.role}</div>
                      <div className="text-xs text-muted">{u.lastSignInAt ? `Signed in ${ago(u.lastSignInAt).toLowerCase()}` : 'Never signed in'}</div>
                    </div>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section>
            <SectionTitle>Latest invoices</SectionTitle>
            {b.invoices.length === 0 ? (
              <p className="text-muted">No invoices yet.</p>
            ) : (
              <div className="card overflow-x-auto">
                <table className="w-full min-w-[32rem] text-left">
                  <thead className="border-b border-line text-muted">
                    <tr>
                      <th className="px-4 py-2.5 font-semibold">Invoice</th>
                      <th className="px-4 py-2.5 font-semibold">Customer</th>
                      <th className="px-4 py-2.5 text-right font-semibold">Total</th>
                      <th className="px-4 py-2.5 text-right font-semibold">Still owed</th>
                      <th className="px-4 py-2.5 font-semibold">Status</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-line">
                    {b.invoices.map((inv) => (
                      <tr key={inv.id}>
                        <td className="px-4 py-2.5">
                          <div className="font-mono text-xs font-semibold">{inv.number}</div>
                          <div className="text-xs text-muted">{day(inv.issueDate)}</div>
                        </td>
                        <td className="max-w-[12rem] truncate px-4 py-2.5">{inv.customerName}</td>
                        <td className="num px-4 py-2.5 text-right font-semibold">{ksh(inv.total)}</td>
                        <td className={cn('num px-4 py-2.5 text-right', inv.balance > 0 ? 'text-ink' : 'text-faint')}>{inv.balance > 0 ? ksh(inv.balance) : '—'}</td>
                        <td className="px-4 py-2.5">
                          <Status tone={inv.status === 'Paid' ? 'good' : inv.status === 'PartPaid' ? 'warn' : inv.status === 'Void' || inv.status === 'Draft' ? 'plain' : 'info'}>
                            {inv.status === 'PartPaid' ? 'Part paid' : inv.status}
                          </Status>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>

          {b.canWipe ? (
            <section className="rounded-lg bg-bad-soft/50 p-4 ring-1 ring-bad/20">
              <div className="flex flex-wrap items-center justify-between gap-3">
                <div className="min-w-0">
                  <p className="font-semibold text-bad">Erase this business</p>
                  <p className="text-xs text-muted">Deletes everything they keep in Kabuor, for good. Only Kabuor’s record of the erasure stays.</p>
                </div>
                <button className="btn btn-ghost text-bad" onClick={() => onErase(b.id)}>
                  <Trash2 className="h-4 w-4" /> Erase data…
                </button>
              </div>
            </section>
          ) : (
            <p className="text-xs text-muted">This is Kabuor’s own business; it can’t be erased from here.</p>
          )}
        </div>
      )}
    </Sheet>
  );
}

// ----------------------------------------------------------------------------- plan and access
const ACCESS_DAYS: [string, string][] = [
  ['7', '7 days'],
  ['14', '14 days'],
  ['30', '1 month'],
  ['90', '3 months'],
  ['365', '1 year'],
];

function AccessSheet({ business, plans, onClose, onSaved }: { business: AccessTarget; plans: Plan[]; onClose: () => void; onSaved: () => void }) {
  const known = plans.some((p) => p.id === business.plan);
  const [plan, setPlan] = useState(known ? business.plan : '');
  const [access, setAccess] = useState<'keep' | 'give' | 'lock'>(business.state === 'locked' ? 'give' : 'keep');
  const [days, setDays] = useState('30');
  const [saving, setSaving] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const chosen = plans.find((p) => p.id === plan);
  const current = plans.find((p) => p.id === business.plan);
  const planChanged = plan !== '' && plan !== business.plan;
  const now = Date.now();
  const base = business.paidUntil && Date.parse(business.paidUntil) > now ? Date.parse(business.paidUntil) : now;
  const until = new Date(base + Number(days) * DAY).toISOString();
  const after: State = access === 'lock' ? 'locked' : access === 'give' ? 'paying' : business.state;
  // What this does to monthly recurring revenue: only a business that pays (or will) counts.
  const mrrBefore = business.state === 'paying' ? (current?.monthly ?? 0) : 0;
  const mrrAfter = after === 'paying' ? ((chosen ?? current)?.monthly ?? 0) : 0;
  const mrrDelta = mrrAfter - mrrBefore;
  const changed = planChanged || access !== 'keep';

  const outcome =
    access === 'lock'
      ? `Lock ${business.name} now`
      : access === 'give'
        ? `Give access until ${day(until)}${planChanged && chosen ? ` on ${chosen.name}` : ''}`
        : planChanged && chosen
          ? `Move to ${chosen.name}`
          : 'Save';

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!changed) return;
    setSaving(true);
    setErr(null);
    try {
      await api(`/platform/businesses/${business.id}/plan`, {
        method: 'POST',
        body: { plan: planChanged ? plan : null, extendDays: access === 'give' ? Number(days) : 0, lock: access === 'lock' },
      });
      onSaved();
    } catch (error: any) {
      setErr(error.message || 'Couldn’t save that.');
      setSaving(false);
    }
  };

  return (
    <Sheet open onClose={onClose} title={`Plan and access · ${business.name}`}>
      <form onSubmit={submit} className="space-y-6 text-sm">
        <div className="rounded-md bg-paper p-3 ring-1 ring-line">
          <div className="text-xs text-muted">Now</div>
          <div className="mt-0.5 flex flex-wrap items-center gap-2">
            <Status tone={STATE[business.state].tone}>{STATE[business.state].label}</Status>
            <span className="font-medium">{accessText(business.state, business.accessEndsAt)}</span>
          </div>
        </div>

        <fieldset>
          <legend className="mb-2 font-semibold">Plan</legend>
          {!known && business.plan && <p className="mb-2 text-xs text-warn">“{business.plan}” isn’t a current plan. Choose one to fix it.</p>}
          <div className="space-y-2">
            {plans.map((p) => (
              <label key={p.id} className={cn('flex cursor-pointer items-center justify-between gap-3 rounded-md bg-white p-3 transition', plan === p.id ? 'ring-2 ring-ink' : 'ring-1 ring-line hover:ring-faint')}>
                <input type="radio" name="plan" value={p.id} checked={plan === p.id} onChange={() => setPlan(p.id)} className="sr-only" />
                <span className="min-w-0">
                  <span className="font-semibold">{p.name}</span>
                  {p.id === business.plan && <span className="ml-2 text-xs text-muted">current</span>}
                  <span className="block text-xs text-muted">{p.users >= 1000 ? 'Unlimited users' : `Up to ${p.users} users`}</span>
                </span>
                <span className="num shrink-0 font-semibold">
                  {ksh(p.monthly)}
                  <span className="text-xs font-normal text-muted">/month</span>
                </span>
              </label>
            ))}
          </div>
        </fieldset>

        <fieldset className="space-y-3">
          <legend className="mb-2 font-semibold">Access</legend>
          <Segmented
            label="Access"
            value={access}
            onChange={setAccess}
            options={[
              ['keep', 'Keep as it is'],
              ['give', 'Give access'],
              ['lock', 'Lock now'],
            ]}
          />
          {access === 'give' && (
            <>
              <Chips label="For how long" value={days} onChange={setDays} options={ACCESS_DAYS} />
              <p className="text-muted">
                Paid access until <strong className="text-ink">{dayYear(until)}</strong>
                {business.paidUntil && Date.parse(business.paidUntil) > now ? ', added to what they’ve already paid.' : ', starting today.'} No money changes hands; they don’t get a bill.
              </p>
            </>
          )}
          {access === 'lock' && <p className="text-bad">Read-only from now. They keep their data but can’t record sales until you give access again.</p>}
        </fieldset>

        {changed && mrrDelta !== 0 && (
          <p className="text-muted">
            Monthly recurring revenue{' '}
            <strong className={mrrDelta > 0 ? 'text-good' : 'text-bad'}>
              {mrrDelta > 0 ? '+' : '−'}
              {ksh(Math.abs(mrrDelta))}
            </strong>
          </p>
        )}

        {err && <ErrorNote>{err}</ErrorNote>}

        <div className="flex justify-end gap-2 border-t border-line pt-4">
          <button type="button" className="btn btn-ghost" onClick={onClose} disabled={saving}>
            Cancel
          </button>
          <button type="submit" className={access === 'lock' ? dangerBtn : primaryBtn} disabled={saving || !changed}>
            {saving && <Loader2 className="h-4 w-4 animate-spin" />} {outcome}
          </button>
        </div>
      </form>
    </Sheet>
  );
}

// ----------------------------------------------------------------------------- growth
function GrowthView({
  analytics,
  error,
  days,
  setDays,
  onOpen,
}: {
  analytics: Analytics | null;
  error: string | null;
  days: Days;
  setDays: (d: Days) => void;
  onOpen: (id: string) => void;
}) {
  const [chart, setChart] = useState<'invoiced' | 'payments' | 'signups'>('invoiced');
  if (error) return <PageError>{error}</PageError>;
  if (!analytics) return <Loading />;
  const b = analytics.businesses;
  const v = analytics.volume;
  const series = analytics.series.map((s) => ({
    label: day(s.day),
    value: chart === 'invoiced' ? s.invoiceValue : chart === 'payments' ? s.paymentValue : s.signups,
  }));
  const money = chart !== 'signups';
  const totalMrr = Math.max(1, b.byPlan.reduce((s, p) => s + p.mrr, 0));

  return (
    <section className="space-y-6">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <p className="text-sm text-muted">Across every business on Kabuor, against the {days} days before.</p>
        <Chips
          label="Period"
          value={days}
          onChange={setDays}
          options={[
            ['7', '7 days'],
            ['30', '30 days'],
            ['90', '90 days'],
            ['365', '1 year'],
          ]}
        />
      </div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="New businesses" value={count(b.newInPeriod)}>
          {b.newBefore > 0 ? `${count(b.newBefore)} in the ${days} days before` : `of ${count(b.total)} in all`}
        </Stat>
        <Stat label="Invoiced through Kabuor" value={kshShort(v.invoiceValue)}>
          <Change value={v.invoiceValueChange} days={days} />
        </Stat>
        <Stat label="Payments received" value={kshShort(v.paymentValue)}>
          <Change value={v.paymentValueChange} days={days} />
        </Stat>
        <Stat label="People signed in" value={count(analytics.users.signedInPeriod)}>
          of {plural(analytics.users.total, 'login')} · {count(b.activeInPeriod)} businesses active
        </Stat>
      </div>

      <div className="card p-5">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 className="font-semibold">
              {chart === 'invoiced' ? 'Invoiced per day' : chart === 'payments' ? 'Payments received per day' : 'New businesses per day'}
            </h3>
            <p className="text-sm text-muted">
              {chart === 'invoiced'
                ? `${ksh(v.invoiceValue)} on ${plural(v.invoices, 'invoice')}`
                : chart === 'payments'
                  ? `${ksh(v.paymentValue)} in ${plural(v.payments, 'payment')}`
                  : `${count(b.newInPeriod)} joined`}
            </p>
          </div>
          <Segmented
            label="Chart"
            value={chart}
            onChange={setChart}
            options={[
              ['invoiced', 'Invoiced'],
              ['payments', 'Payments'],
              ['signups', 'Sign-ups'],
            ]}
          />
        </div>
        <LineChart data={series} fmt={money ? ksh : (n) => plural(n, 'business', 'businesses')} axis={money ? (c) => short(c / 100) : short} />
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <div className="card p-5">
          <SectionTitle aside={`${ksh(analytics.revenue.mrr)} a month`}>Revenue by plan</SectionTitle>
          <ul className="space-y-3">
            {b.byPlan.map((p) => (
              <li key={p.id}>
                <div className="flex items-baseline justify-between gap-2 text-sm">
                  <span>
                    <span className="font-semibold">{p.name}</span> <span className="text-muted">· {plural(p.businesses, 'business', 'businesses')} at {ksh(p.monthly)}</span>
                  </span>
                  <span className="num font-semibold">{ksh(p.mrr)}</span>
                </div>
                <div className="mt-1.5 h-2 overflow-hidden rounded-full bg-paper ring-1 ring-line">
                  <div className="h-full rounded-full bg-ink" style={{ width: `${pct(p.mrr, totalMrr)}%` }} />
                </div>
              </li>
            ))}
          </ul>
          <p className="mt-4 text-xs text-muted">
            {count(b.paying)} paying · {count(b.trial)} on trial · {count(b.grace)} in grace · {count(b.locked)} locked
          </p>
        </div>

        <div className="card p-5">
          <SectionTitle aside={`invoiced in ${days} days`}>Busiest businesses</SectionTitle>
          {analytics.top.filter((t) => t.invoiceValue > 0).length === 0 ? (
            <p className="text-sm text-muted">No invoices in this period yet.</p>
          ) : (
            <ol className="divide-y divide-line">
              {analytics.top
                .filter((t) => t.invoiceValue > 0)
                .slice(0, 8)
                .map((t) => (
                  <li key={t.id}>
                    <button className="flex w-full items-center justify-between gap-3 py-2.5 text-left text-sm hover:bg-paper" onClick={() => onOpen(t.id)}>
                      <span className="min-w-0">
                        <span className="block truncate font-semibold">{t.name}</span>
                        <span className="text-xs text-muted">
                          {plural(t.invoices, 'invoice')} · signed in {ago(t.lastSignIn).toLowerCase()}
                        </span>
                      </span>
                      <span className="num shrink-0 font-semibold">{kshShort(t.invoiceValue)}</span>
                    </button>
                  </li>
                ))}
            </ol>
          )}
        </div>
      </div>
    </section>
  );
}

// ----------------------------------------------------------------------------- inquiries
type InquiryShow = 'Open' | 'Answered' | 'Quoted' | 'Won' | 'Lost' | 'all';
const INQUIRY: Record<IssueItem['status'], { label: string; tone: 'good' | 'info' | 'warn' | 'bad' | 'plain' }> = {
  Open: { label: 'Waiting on reply', tone: 'warn' },
  Answered: { label: 'Answered', tone: 'info' },
  Quoted: { label: 'Quoted', tone: 'info' },
  Won: { label: 'Won', tone: 'good' },
  Lost: { label: 'Lost', tone: 'plain' },
};

/** When the customer last wrote (what an open inquiry has been waiting since). */
const lastFromCustomer = (i: IssueItem) => [...i.messages].reverse().find((m) => m.from === 'customer')?.at ?? i.createdAt;
const lastActivity = (i: IssueItem) => i.messages[i.messages.length - 1]?.at ?? i.createdAt;

function useWide() {
  const query = '(min-width: 1024px)';
  const [wide, setWide] = useState(() => window.matchMedia(query).matches);
  useEffect(() => {
    const m = window.matchMedia(query);
    const on = () => setWide(m.matches);
    m.addEventListener('change', on);
    return () => m.removeEventListener('change', on);
  }, []);
  return wide;
}

function InquiriesView({ issues, loading, error, onReload }: { issues: IssueItem[] | null; loading: boolean; error: string | null; onReload: () => void }) {
  const [show, setShow] = useState<InquiryShow>('Open');
  const [q, setQ] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [sent, setSent] = useState<string | null>(null);
  const wide = useWide();
  const onSent = (i: IssueItem) => {
    setSent(`Reply sent to ${i.name || 'the customer'} at ${i.companyName}.`);
    setSelectedId(null);
    onReload();
  };

  if (error) return <PageError>{error}</PageError>;
  if (!issues) return <Loading />;

  const n = (s: IssueItem['status']) => issues.filter((i) => i.status === s).length;
  const needle = q.trim().toLowerCase();
  const rows = issues
    .filter((i) => show === 'all' || i.status === show)
    .filter((i) => !needle || [i.companyName, i.name, i.subject, i.number, i.phone, i.email].some((v) => (v ?? '').toLowerCase().includes(needle)))
    // Waiting longest first; everything else newest first.
    .sort((a, b) => (show === 'Open' ? Date.parse(lastFromCustomer(a)) - Date.parse(lastFromCustomer(b)) : Date.parse(lastActivity(b)) - Date.parse(lastActivity(a))));
  const selected = rows.find((i) => i.id === selectedId) ?? (wide ? rows[0] : undefined) ?? null;

  return (
    <section className="space-y-3">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl text-sm text-muted">
          Questions customers sent to businesses on Kabuor (the latest 50). A reply goes into that business’s own thread, signed as Kabuor support, and the
          customer sees it in the shop portal.
        </p>
        <button className="btn btn-ghost" onClick={onReload}>
          <RefreshCw className={cn('h-4 w-4', loading && 'animate-spin')} /> Refresh
        </button>
      </div>
      {sent && (
        <p role="status" className="flex items-center gap-2 rounded-md bg-good-soft px-3 py-2 text-sm font-medium text-good">
          <CheckCircle2 className="h-4 w-4" /> {sent}
        </p>
      )}
      <SearchBox value={q} onChange={setQ} placeholder="Search business, customer, subject or number" />
      <Chips
        label="Show inquiries"
        value={show}
        onChange={setShow}
        options={[
          ['Open', 'Waiting on reply', n('Open')],
          ['Answered', 'Answered', n('Answered')],
          ['Quoted', 'Quoted', n('Quoted')],
          ['Won', 'Won', n('Won')],
          ['Lost', 'Lost', n('Lost')],
          ['all', 'All', issues.length],
        ]}
      />

      {rows.length === 0 ? (
        <Empty title={show === 'Open' && !needle ? 'Nobody is waiting on a reply' : 'No inquiries match'}>
          {show === 'Open' && !needle ? 'Every customer question across the businesses has an answer.' : 'Try another filter or search.'}
        </Empty>
      ) : (
        <div className="grid gap-4 lg:grid-cols-[22rem_1fr]">
          <ul className="card max-h-[640px] divide-y divide-line overflow-y-auto">
            {rows.map((i) => {
              const since = lastFromCustomer(i);
              const late = i.status === 'Open' && Date.now() - Date.parse(since) > DAY;
              return (
                <li key={i.id}>
                  <button
                    onClick={() => setSelectedId(i.id)}
                    aria-current={selected?.id === i.id}
                    className={cn('block w-full border-l-2 p-3.5 text-left transition', selected?.id === i.id ? 'border-l-ink bg-paper' : 'border-l-transparent hover:bg-paper')}
                  >
                    <div className="flex items-start justify-between gap-2">
                      <span className="truncate font-semibold">{i.subject || 'No subject'}</span>
                      <span className={cn('num shrink-0 text-xs', late ? 'font-semibold text-bad' : 'text-faint')}>
                        {i.status === 'Open' ? waited(since) : ago(lastActivity(i))}
                      </span>
                    </div>
                    <div className="mt-0.5 truncate text-sm text-muted">
                      {i.companyName} · {i.name || 'Customer'}
                    </div>
                  </button>
                </li>
              );
            })}
          </ul>
          {wide && selected && <Thread key={selected.id} issue={selected} onSent={() => onSent(selected)} />}
        </div>
      )}
      {!wide && selected && (
        <Sheet open onClose={() => setSelectedId(null)} title={selected.subject || 'Inquiry'}>
          <Thread key={selected.id} issue={selected} onSent={() => onSent(selected)} />
        </Sheet>
      )}
    </section>
  );
}

function Thread({ issue, onSent }: { issue: IssueItem; onSent: () => void }) {
  const [text, setText] = useState('');
  const [sending, setSending] = useState(false);
  const [err, setErr] = useState<string | null>(null);

  const send = async (e?: React.FormEvent) => {
    e?.preventDefault();
    if (!text.trim() || sending) return;
    setSending(true);
    setErr(null);
    try {
      await api(`/platform/issues/${issue.id}/reply`, { method: 'POST', body: { message: text.trim() } });
      setText('');
      onSent();
    } catch (error: any) {
      setErr(error.message || 'Couldn’t send that reply.');
    } finally {
      setSending(false);
    }
  };

  return (
    <div className="card flex min-w-0 flex-col p-5">
      <div className="flex items-start justify-between gap-3 border-b border-line pb-3">
        <div className="min-w-0 flex-1">
          <h3 className="font-semibold">{issue.subject || 'No subject'}</h3>
          <p className="text-sm text-muted">
            {issue.number} · <strong className="font-semibold text-ink-2">{issue.companyName}</strong> · {issue.channel}
          </p>
          <p className="text-sm text-muted">{[issue.name, issue.phone, issue.email].filter(Boolean).join(' · ')}</p>
        </div>
        <span className="shrink-0">
          <Status tone={INQUIRY[issue.status].tone}>{INQUIRY[issue.status].label}</Status>
        </span>
      </div>

      <div className="my-4 max-h-[380px] space-y-3 overflow-y-auto pr-1">
        {issue.messages.map((m) => {
          const ours = m.from !== 'customer';
          return (
            <div key={m.id} className={cn('max-w-[85%] rounded-lg px-3.5 py-2.5 text-sm', ours ? 'ml-auto bg-ink text-white' : 'bg-paper ring-1 ring-line')}>
              <div className={cn('mb-1 flex items-center justify-between gap-3 text-xs', ours ? 'text-white/70' : 'text-muted')}>
                <span className="truncate">{m.author || (ours ? issue.companyName : issue.name || 'Customer')}</span>
                <span className="shrink-0">{dateTime(m.at)}</span>
              </div>
              <p className="break-words whitespace-pre-wrap">{m.text}</p>
            </div>
          );
        })}
      </div>

      <form onSubmit={send} className="mt-auto space-y-2 border-t border-line pt-3">
        {err && <ErrorNote>{err}</ErrorNote>}
        <label htmlFor={`reply-${issue.id}`} className="sr-only">
          Reply to {issue.name}
        </label>
        <textarea
          id={`reply-${issue.id}`}
          className="field h-24 resize-y"
          placeholder={`Reply to ${issue.name || 'the customer'} for ${issue.companyName}`}
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => (e.ctrlKey || e.metaKey) && e.key === 'Enter' && send()}
          disabled={sending}
          maxLength={4000}
        />
        <div className="flex flex-wrap items-center justify-between gap-2">
          <span className="text-xs text-muted">Signed as you (Kabuor support) · Ctrl+Enter sends</span>
          <button type="submit" className={primaryBtn} disabled={sending || text.trim().length < 2}>
            {sending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />} Send reply
          </button>
        </div>
      </form>
    </div>
  );
}

// ----------------------------------------------------------------------------- traffic and health
const CHECK: Record<Tone, { label: string; tone: 'good' | 'warn' | 'bad' }> = {
  good: { label: 'OK', tone: 'good' },
  warn: { label: 'Check', tone: 'warn' },
  bad: { label: 'Failing', tone: 'bad' },
};
const CONFIGURED: [string, string][] = [
  ['sentry', 'Error tracking'],
  ['ai', 'Msaidizi (AI)'],
  ['sms', 'SMS'],
  ['billing', 'M-Pesa billing'],
  ['redis', 'Shared cache'],
  ['legalEmail', 'Legal email'],
];
const time = (iso: string) => new Date(iso).toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit' });

function HealthView({ health, error }: { health: Health | null; error: string | null }) {
  const [metric, setMetric] = useState<'requests' | 'latency'>('requests');
  if (error) return <PageError>{error}</PageError>;
  if (!health) return <Loading />;
  const problems = health.checks.filter((c) => c.status !== 'good');
  const t = health.traffic;
  const missing = CONFIGURED.filter(([k]) => health.configured[k] === false);

  return (
    <section className="space-y-6">
      <div
        className={cn(
          'flex flex-wrap items-center justify-between gap-3 rounded-lg p-4 ring-1',
          health.status === 'good' ? 'bg-good-soft ring-good/20' : health.status === 'warn' ? 'bg-warn-soft ring-warn/20' : 'bg-bad-soft ring-bad/20',
        )}
      >
        <p className={cn('flex items-center gap-2 font-semibold', health.status === 'good' ? 'text-good' : health.status === 'warn' ? 'text-warn' : 'text-bad')}>
          {health.status === 'good' ? <CheckCircle2 className="h-5 w-5" /> : <AlertTriangle className="h-5 w-5" />}
          {problems.length === 0 ? 'Everything is working' : `${plural(problems.length, 'check')} ${problems.length === 1 ? 'needs' : 'need'} a look: ${problems.map((p) => p.name).join(', ')}`}
        </p>
        <span className="text-sm text-muted">Checked {time(health.checkedAt)} · refreshes every 30 s</span>
      </div>
      {health.demoTraffic && (
        <p className="-mt-3 text-sm text-muted">
          The figures and charts below include the simulated load of the demo businesses. The checks judge real requests only; untick “Include demo
          businesses” to see real traffic alone.
        </p>
      )}

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Requests, last hour" value={count(t.requests)}>
          {count(t.rejected)} turned away by rate limits
        </Stat>
        <Stat label="Server errors" value={`${t.errorRate}%`} tone={t.errorRate >= 5 ? 'bad' : undefined}>
          {plural(t.errors, 'request')} failed
        </Stat>
        <Stat label="Typical response" value={`${t.p50} ms`}>
          half of requests answer faster
        </Stat>
        <Stat label="Slow responses" value={`${t.p95} ms`} tone={t.p95 > 1000 ? 'bad' : undefined}>
          95% faster · 99% under {t.p99} ms
        </Stat>
      </div>

      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {[...health.checks]
          .sort((a, b) => ['bad', 'warn', 'good'].indexOf(a.status) - ['bad', 'warn', 'good'].indexOf(b.status))
          .map((c) => (
            <div key={c.key} className="card p-4">
              <div className="flex items-center justify-between gap-2">
                <span className="font-semibold">{c.name}</span>
                <Status tone={CHECK[c.status].tone}>{CHECK[c.status].label}</Status>
              </div>
              <p className="mt-1 text-sm text-muted">{c.summary}</p>
            </div>
          ))}
      </div>

      <div className="card p-5">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <div>
            <h3 className="font-semibold">{metric === 'requests' ? 'Requests per minute' : 'Average response time'}</h3>
            <p className="text-sm text-muted">The last hour on this server</p>
          </div>
          <Segmented
            label="Chart"
            value={metric}
            onChange={setMetric}
            options={[
              ['requests', 'Requests'],
              ['latency', 'Response time'],
            ]}
          />
        </div>
        <LineChart
          data={t.series.map((s) => ({ label: time(s.at), value: metric === 'requests' ? s.requests : s.avgMs }))}
          fmt={metric === 'requests' ? (v) => plural(v, 'request') : (v) => `${Math.round(v)} ms`}
          empty="No requests in the last hour"
        />
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <div className="card p-5">
          <SectionTitle>Slowest endpoints</SectionTitle>
          {t.slowest.length === 0 ? (
            <p className="text-sm text-muted">Nothing recorded yet.</p>
          ) : (
            <table className="w-full table-fixed text-left text-sm">
              <thead className="border-b border-line text-muted">
                <tr>
                  <th className="py-2 font-semibold">Route</th>
                  <th className="w-16 py-2 text-right font-semibold">Calls</th>
                  <th className="w-20 py-2 text-right font-semibold">Average</th>
                  <th className="w-20 py-2 text-right font-semibold">Slowest</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {t.slowest.map((r) => (
                  <tr key={r.route}>
                    <td className="truncate py-2 pr-2 font-mono text-xs" title={r.route}>
                      {r.route}
                    </td>
                    <td className="num py-2 text-right text-muted">{count(r.requests)}</td>
                    <td className="num py-2 text-right font-semibold">{r.avgMs} ms</td>
                    <td className="num py-2 text-right text-muted">{r.maxMs} ms</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>

        <div className="card p-5">
          <SectionTitle>Endpoints with server errors</SectionTitle>
          {t.failing.length === 0 ? (
            <p className="flex items-center gap-2 text-sm text-good">
              <CheckCircle2 className="h-4 w-4" /> No server errors in the last hour.
            </p>
          ) : (
            <table className="w-full table-fixed text-left text-sm">
              <thead className="border-b border-line text-muted">
                <tr>
                  <th className="py-2 font-semibold">Route</th>
                  <th className="w-20 py-2 text-right font-semibold">Errors</th>
                  <th className="w-20 py-2 text-right font-semibold">Of calls</th>
                </tr>
              </thead>
              <tbody className="divide-y divide-line">
                {t.failing.map((r) => (
                  <tr key={r.route}>
                    <td className="truncate py-2 pr-2 font-mono text-xs" title={r.route}>
                      {r.route}
                    </td>
                    <td className="num py-2 text-right font-semibold text-bad">{count(r.errors)}</td>
                    <td className="num py-2 text-right text-muted">{count(r.requests)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          <div className="mt-5 border-t border-line pt-4">
            <p className="mb-2 text-sm font-semibold">Connected services</p>
            <div className="flex flex-wrap gap-1.5">
              {CONFIGURED.map(([k, label]) => (
                <Status key={k} tone={health.configured[k] ? 'good' : 'plain'}>
                  {label}: {health.configured[k] ? 'on' : 'not set up'}
                </Status>
              ))}
            </div>
            {missing.length > 0 && <p className="mt-2 text-xs text-muted">Features that need {missing.map(([, l]) => l.toLowerCase()).join(', ')} stay off until their keys are set.</p>}
          </div>
        </div>
      </div>
    </section>
  );
}

// ----------------------------------------------------------------------------- server
function ServerView({ health, onSeeded }: { health: Health | null; onSeeded: () => void }) {
  const resources = useApi<Resources>('/platform/resources');
  const [running, setRunning] = useState<string | null>(null);
  const [result, setResult] = useState<{ text: string; ok: boolean } | null>(null);

  useEffect(() => {
    const t = window.setInterval(() => document.visibilityState === 'visible' && resources.reload(), 30_000);
    return () => window.clearInterval(t);
  }, [resources.reload]);

  const run = async (action: 'gc-collect' | 'seed-demo') => {
    setRunning(action);
    setResult(null);
    try {
      const res = await api<{ message: string }>(`/platform/actions/${action}`, { method: 'POST' });
      setResult({ text: res.message, ok: true });
      resources.reload();
      if (action === 'seed-demo') onSeeded();
    } catch (err: any) {
      setResult({ text: err.message || 'That didn’t work.', ok: false });
    } finally {
      setRunning(null);
    }
  };

  if (resources.error) return <PageError>{resources.error}</PageError>;
  const r = resources.data;
  if (!r) return <Loading />;
  const memPct = r.memory.limitMb > 0 ? pct(r.memory.workingSetMb, r.memory.limitMb) : 0;
  const tone = r.scaling.status === 'optimal' ? 'good' : r.scaling.status === 'attention_needed' ? 'bad' : 'warn';

  return (
    <section className="space-y-6">
      <div className={cn('rounded-lg p-4 ring-1', tone === 'good' ? 'bg-good-soft ring-good/20' : tone === 'warn' ? 'bg-warn-soft ring-warn/20' : 'bg-bad-soft ring-bad/20')}>
        <p className={cn('flex items-center gap-2 font-semibold', tone === 'good' ? 'text-good' : tone === 'warn' ? 'text-warn' : 'text-bad')}>
          {tone === 'good' ? <CheckCircle2 className="h-5 w-5" /> : <AlertTriangle className="h-5 w-5" />} {r.scaling.summary}
        </p>
        <ul className="mt-1 space-y-0.5 text-sm text-ink-2">
          {r.scaling.recommendations.map((rec) => (
            <li key={rec}>{rec}</li>
          ))}
        </ul>
      </div>

      <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
        <Stat label="Memory in use" value={`${Math.round(r.memory.workingSetMb)} MB`} tone={memPct >= 85 ? 'bad' : undefined}>
          {memPct}% of {count(r.memory.limitMb)} MB · {Math.round(r.memory.heapMb)} MB managed
        </Stat>
        <Stat label="Busy worker threads" value={count(r.cpu.busyWorkerThreads)}>
          of {count(r.cpu.maxWorkerThreads)} available · {r.cpu.threads} threads in all
        </Stat>
        <Stat label="Database" value={r.database.sizeMb !== null ? `${count(r.database.sizeMb)} MB` : 'Answering'}>
          {r.database.connections !== null ? plural(r.database.connections, 'open connection') : 'Size shown on PostgreSQL only'}
        </Stat>
        <Stat
          label="Up for"
          value={r.uptime.uptimeHours >= 48 ? `${Math.round(r.uptime.uptimeHours / 24)} days` : r.uptime.uptimeHours >= 1 ? `${r.uptime.uptimeHours} h` : `${Math.max(1, Math.round((Date.now() - Date.parse(r.uptime.startedAt)) / 6e4))} min`}
        >
          since {dateTime(r.uptime.startedAt)}
        </Stat>
      </div>

      <div className="grid gap-6 lg:grid-cols-2">
        <div className="card space-y-4 p-5">
          <SectionTitle aside={`${count(r.memory.limitMb)} MB limit`}>Memory</SectionTitle>
          <div>
            <div className="mb-1.5 flex justify-between text-sm">
              <span className="text-muted">In use</span>
              <span className="num font-semibold">
                {Math.round(r.memory.workingSetMb)} MB · {memPct}%
              </span>
            </div>
            <div className="h-2.5 overflow-hidden rounded-full bg-paper ring-1 ring-line">
              <div className={cn('h-full rounded-full transition-all', memPct >= 85 ? 'bg-bad' : memPct >= 70 ? 'bg-warn' : 'bg-ink')} style={{ width: `${Math.min(100, memPct)}%` }} />
            </div>
            <p className="mt-1.5 text-xs text-muted">Scale up past 85%.</p>
          </div>
          <dl className="grid grid-cols-3 gap-2 text-center">
            {(
              [
                ['Quick collections', r.memory.gen0],
                ['Middle', r.memory.gen1],
                ['Full', r.memory.gen2],
              ] as [string, number][]
            ).map(([label, n]) => (
              <div key={label} className="rounded-md bg-paper p-2.5 ring-1 ring-line">
                <dd className="num font-display text-lg font-semibold">{count(n)}</dd>
                <dt className="text-xs text-muted">{label}</dt>
              </div>
            ))}
          </dl>
          {health && (
            <dl className="grid grid-cols-2 gap-x-4 gap-y-2 border-t border-line pt-4 text-sm">
              {(
                [
                  ['Environment', health.server.environment],
                  ['Version', health.server.version],
                  ['.NET', health.server.dotnet],
                  ['System', health.server.os],
                ] as [string, string][]
              ).map(([k, v]) => (
                <div key={k} className="min-w-0">
                  <dt className="text-xs text-muted">{k}</dt>
                  <dd className="truncate font-medium" title={v}>
                    {v}
                  </dd>
                </div>
              ))}
            </dl>
          )}
        </div>

        <div className="card space-y-4 p-5">
          <SectionTitle>Maintenance</SectionTitle>
          <div className="flex flex-wrap items-center justify-between gap-3">
            <div className="min-w-0 flex-1">
              <p className="font-semibold">Free memory now</p>
              <p className="text-sm text-muted">Runs a full garbage collection. Safe while people work; requests pause for a moment.</p>
            </div>
            <button className="btn btn-ghost" disabled={running !== null} onClick={() => run('gc-collect')}>
              {running === 'gc-collect' ? <Loader2 className="h-4 w-4 animate-spin" /> : <Zap className="h-4 w-4" />} Free memory
            </button>
          </div>
          {result && (
            <p role="status" className={cn('rounded-md px-3 py-2 text-sm font-medium', result.ok ? 'bg-good-soft text-good' : 'bg-bad-soft text-bad')}>
              {result.text}
            </p>
          )}
        </div>
      </div>
    </section>
  );
}

// ----------------------------------------------------------------------------- erasure
const REASONS = [
  'The owner asked us to close the account and delete their data',
  'Erasure request under the Data Protection Act 2019, s. 40 (GDPR art. 17)',
  'Demo or test business, no longer needed',
];

function EraseView({ businesses, preselected, onErased }: { businesses: BusinessItem[] | null; preselected: string | null; onErased: () => void }) {
  const [selectedId, setSelectedId] = useState(preselected ?? '');
  const [reason, setReason] = useState('');
  const [confirm, setConfirm] = useState('');
  const [erasing, setErasing] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const erasures = useApi<Erasure[]>('/platform/erasures');
  const detail = useApi<BusinessDetail>(selectedId ? `/platform/businesses/${selectedId}` : null);

  useEffect(() => {
    if (preselected) setSelectedId(preselected);
  }, [preselected]);

  if (!businesses) return <Loading />;
  const choices = businesses.filter((b) => b.name !== 'Kabuor Builds').sort((a, b) => a.name.localeCompare(b.name));
  const target = choices.find((b) => b.id === selectedId);
  const typed = target && confirm.trim().toLowerCase() === target.name.trim().toLowerCase();
  const ready = Boolean(target && typed && reason.trim().length >= 4);
  const f = detail.data?.id === selectedId ? detail.data.footprint : null;

  const erase = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!target || !ready) return;
    setErasing(true);
    setErr(null);
    setDone(null);
    try {
      const res = await api<{ wiped: string; removed: number }>(`/platform/businesses/${target.id}/wipe`, { method: 'POST', body: { reason: reason.trim(), confirm: confirm.trim() } });
      setDone(`${res.wiped} is erased: ${plural(res.removed, 'record')} removed. The line in the record below is all that’s left.`);
      setSelectedId('');
      setConfirm('');
      setReason('');
      erasures.reload();
      onErased();
    } catch (error: any) {
      setErr(error.message || 'The erasure didn’t go through. Nothing was deleted.');
    } finally {
      setErasing(false);
    }
  };

  return (
    <section className="grid gap-6 lg:grid-cols-[minmax(0,36rem)_1fr]">
      <form onSubmit={erase} className="card space-y-5 p-5">
        <div>
          <h2 className="font-display text-lg font-semibold">Erase a business</h2>
          <p className="mt-1 text-sm text-muted">
            Permanently deletes everything the business keeps in Kabuor: books, invoices, payroll, stock, customers and logins, and its audit log. This can’t be
            undone. For the Data Protection Act 2019 (s. 40) and GDPR (art. 17).
          </p>
        </div>

        {done && <p className="rounded-md bg-good-soft px-3 py-2 text-sm font-medium text-good">{done}</p>}
        {err && <ErrorNote>{err}</ErrorNote>}

        <label className="block">
          <span className="mb-1.5 block text-sm font-semibold">Business</span>
          <select
            className="field"
            value={selectedId}
            onChange={(e) => {
              setSelectedId(e.target.value);
              setConfirm('');
              setDone(null);
            }}
          >
            <option value="">Choose a business…</option>
            <optgroup label="Businesses">
              {choices
                .filter((b) => !b.isDemo)
                .map((b) => (
                  <option key={b.id} value={b.id}>
                    {b.name} · {STATE[b.state].label.toLowerCase()} · {plural(b.usersCount, 'login')}
                  </option>
                ))}
            </optgroup>
            {choices.some((b) => b.isDemo) && (
              <optgroup label="Demo businesses">
                {choices
                  .filter((b) => b.isDemo)
                  .map((b) => (
                    <option key={b.id} value={b.id}>
                      {b.name} · demo
                    </option>
                  ))}
              </optgroup>
            )}
          </select>
          <span className="mt-1 block text-xs text-muted">Kabuor Builds, the platform’s own business, can’t be erased.</span>
        </label>

        {target && (
          <>
            <div className="rounded-md bg-paper p-3 ring-1 ring-line">
              <p className="text-sm font-semibold">
                {f === null ? 'Counting what will be deleted…' : 'Deleted, with their accounts, settings and history:'}
                {target.state === 'paying' && <span className="font-normal text-warn"> · they’re paying, until {dayYear(target.accessEndsAt)}</span>}
              </p>
              {f && (
                <div className="mt-2 grid grid-cols-3 gap-x-4 gap-y-1 text-sm sm:grid-cols-5">
                  {(
                    [
                      ['customer', f.customers],
                      ['invoice', f.invoices],
                      ['payment', f.payments],
                      ['employee', f.employees],
                      ['login', f.users],
                    ] as [string, number][]
                  ).map(([k, v]) => (
                    <div key={k} className="text-muted">
                      <span className="num font-semibold text-ink">{count(v)}</span> {plural(v, k).replace(/^[\d,]+ /, '')}
                    </div>
                  ))}
                </div>
              )}
            </div>

            <div>
              <label htmlFor="erase-reason" className="mb-1.5 block text-sm font-semibold">
                Why
              </label>
              <div className="mb-2 flex flex-wrap gap-1.5">
                {REASONS.map((r) => (
                  <button
                    key={r}
                    type="button"
                    onClick={() => setReason(r)}
                    className={cn('rounded-full px-3 py-1 text-xs font-medium ring-1 transition', reason === r ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white text-ink-2 ring-line hover:ring-faint')}
                  >
                    {r.split(/[,(]/)[0]}
                  </button>
                ))}
              </div>
              <textarea
                id="erase-reason"
                className="field h-20"
                placeholder="Who asked, and how (email, letter, call). Kept in Kabuor’s record of the erasure."
                value={reason}
                onChange={(e) => setReason(e.target.value)}
                maxLength={500}
              />
            </div>

            <div>
              <label htmlFor="erase-confirm" className="mb-1.5 block text-sm font-semibold">
                Type <span className="font-mono">{target.name}</span> to confirm
              </label>
              <input id="erase-confirm" className="field" autoComplete="off" value={confirm} onChange={(e) => setConfirm(e.target.value)} placeholder={target.name} />
            </div>

            <button type="submit" className={cn(dangerBtn, 'w-full')} disabled={erasing || !ready}>
              {erasing ? (
                <>
                  <Loader2 className="h-4 w-4 animate-spin" /> Erasing…
                </>
              ) : (
                <>
                  <Trash2 className="h-4 w-4" /> Erase {target.name} for good
                </>
              )}
            </button>
            {!ready && (
              <p className="text-center text-xs text-muted">
                {reason.trim().length < 4 ? 'Say why first. ' : ''}
                {!typed ? 'Then type the business name exactly.' : ''}
              </p>
            )}
          </>
        )}
      </form>

      <div className="card min-w-0 p-5">
        <SectionTitle aside="kept by Kabuor">Erasure record</SectionTitle>
        {erasures.error ? (
          <ErrorNote>{erasures.error}</ErrorNote>
        ) : !erasures.data ? (
          <Loading />
        ) : erasures.data.length === 0 ? (
          <p className="text-sm text-muted">No business has been erased yet. Each erasure leaves one line here: which business, who, when and why.</p>
        ) : (
          <ul className="divide-y divide-line">
            {erasures.data.map((e) => (
              <li key={`${e.companyId}-${e.at}`} className="py-3 text-sm">
                <div className="flex flex-wrap items-baseline justify-between gap-2">
                  <span className="font-semibold">{e.name || 'A business'}</span>
                  <span className="text-xs text-muted">{dateTime(e.at)}</span>
                </div>
                <p className="mt-0.5 text-muted">
                  By {e.actor} · {plural(e.removed, 'record')} removed
                </p>
                <p className="mt-0.5 break-words text-ink-2">{e.reason}</p>
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
