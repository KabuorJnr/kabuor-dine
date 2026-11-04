// A module's own workspace (/w/{module}): Operations, Procurement & supply, Sales & marketing, Human resources,
// Finance or Intelligence, worked in on its own. It shows how the module's links of the chain are doing, its pages,
// what happened in it, and what the other modules handed it, all live: when someone saves something anywhere in the
// business, this page refreshes by itself and the new rows are marked.
import { useEffect, useRef, useState } from 'react';
import { Link, Navigate, useParams } from 'react-router';
import { ArrowRight, ArrowDownLeft, Radio } from 'lucide-react';
import { useApi, useSession } from '../lib/api';
import { cn } from '../lib/format';
import { useLang } from '../lib/i18n';
import { lensFor, MODULE_ORDER, MODULES, type ModuleKey } from '../lib/modules';
import { navGroups } from '../lib/nav';
import { Loading, PageError } from '../components/ui';
import { ChainCard, type ChainLink } from './BusinessOS';

type Row = { id: number; at: string; actor: string; entity: string; action: string; entityId: string; module: ModuleKey | null; what: string };
type WorkspaceData = {
  module: ModuleKey; industry: string; links: ChainLink[]; today: { changes: number; people: number };
  activity: Row[]; handoffs: Row[]; watches: { entity: string; module: ModuleKey; name: string }[]; asOf: string;
};

/** Where a record opens, for the kinds that have a page of their own. */
const OPEN: Record<string, (id: string) => string> = {
  SalesDoc: (id) => `/sales/${id}`, Customer: (id) => `/customers/${id}`, PurchaseDoc: (id) => `/purchases/${id}`, Supplier: (id) => `/suppliers/${id}`,
  Asset: (id) => `/assets?id=${id}`, Lead: (id) => `/pipeline/${id}`, PayrollRun: (id) => `/payroll/${id}`, Campaign: (id) => `/campaigns/${id}`,
};

function ago(iso: string) {
  const s = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (s < 60) return 'just now';
  if (s < 3600) return `${Math.floor(s / 60)} min ago`;
  if (s < 86400) return `${Math.floor(s / 3600)} h ago`;
  const d = Math.floor(s / 86400);
  return d === 1 ? 'yesterday' : `${d} days ago`;
}

export default function Workspace() {
  const { module } = useParams();
  const key = (MODULE_ORDER as string[]).includes(module ?? '') ? (module as ModuleKey) : null;
  if (!key) return <Navigate to="/" replace />;
  return <WorkspaceView key={key} module={key} />;
}

function WorkspaceView({ module }: { module: ModuleKey }) {
  const session = useSession();
  const { t } = useLang();
  const M = MODULES[module];
  const ws = useApi<WorkspaceData>(`/workspaces/${module}`);
  const pages = session ? navGroups(session, t).find((g) => g.module === module)?.links ?? [] : [];

  // Rows that arrived after the page first loaded are marked as new until the person leaves.
  const firstSeen = useRef<number | null>(null);
  const [, force] = useState(0);
  useEffect(() => {
    if (!ws.data) return;
    if (firstSeen.current === null) {
      firstSeen.current = Math.max(0, ...ws.data.activity.map((r) => r.id), ...ws.data.handoffs.map((r) => r.id));
      force((n) => n + 1);
    }
  }, [ws.data]);
  const isNew = (r: Row) => firstSeen.current !== null && r.id > firstSeen.current;

  if (ws.status === 403) return <PageError>You don’t have access to the {M.name} workspace. Ask the owner to give your login a role that includes it.</PageError>;
  if (ws.error && !ws.data) return <PageError>{ws.error}</PageError>;
  const d = ws.data;
  const lens = lensFor(d?.industry ?? session?.company.industry ?? '');

  return (
    <div className="space-y-6">
      {/* The module, in its colour: what it's for, and how busy it is today. */}
      <header className="card relative overflow-hidden p-5 sm:p-6">
        <span className="absolute inset-y-0 left-0 w-1.5" style={{ background: M.color }} aria-hidden />
        <div className="flex flex-wrap items-start gap-4">
          <span className="grid h-12 w-12 shrink-0 place-items-center rounded-xl text-white shadow-xs" style={{ background: M.color }}><M.icon className="h-6 w-6" /></span>
          <div className="min-w-0 flex-1">
            <div className="flex flex-wrap items-center gap-2">
              <h1 className="font-display text-2xl font-semibold">{M.name}</h1>
            </div>
            <p className="mt-1 max-w-2xl text-sm text-muted">{M.promise}</p>
          </div>
          {d && (
            <div className="flex gap-6 text-sm">
              <div><p className="num text-2xl font-bold">{d.today.changes}</p><p className="text-2xs text-muted">changes today</p></div>
              <div><p className="num text-2xl font-bold">{d.today.people}</p><p className="text-2xs text-muted">{d.today.people === 1 ? 'person' : 'people'} at work</p></div>
            </div>
          )}
        </div>
        {pages.length > 0 && (
          <nav aria-label={`${M.name} pages`} className="mt-5 flex flex-wrap gap-2">
            {pages.map((p) => (
              <Link key={p.to} to={p.to} className="inline-flex items-center gap-2 rounded-lg bg-paper px-3 py-2 text-sm font-semibold text-ink-2 ring-1 ring-line transition hover:bg-white hover:text-ink">
                <p.icon className="h-4 w-4" style={{ color: M.color }} />{p.label}
              </Link>
            ))}
          </nav>
        )}
      </header>

      {!d ? <Loading /> : (
        <>
          {d.links.length > 0 && (
            <section aria-label="How the module is doing">
              <h2 className="mb-3 text-lg font-semibold">How {M.short.toLowerCase() === 'insight' ? 'decisions are' : 'it’s'} doing</h2>
              <ol className={cn('grid grid-cols-1 gap-3', d.links.length > 1 ? 'md:grid-cols-2' : 'md:max-w-md')}>
                {d.links.map((l, i) => <ChainCard key={l.key} link={l} step={i + 1} word={lens.words[l.key]} />)}
              </ol>
            </section>
          )}

          <div className="grid grid-cols-1 items-start gap-4 lg:grid-cols-2">
            <Feed
              title={`In ${M.name}`}
              icon={<Radio className="h-4 w-4" style={{ color: M.color }} />}
              rows={d.activity}
              isNew={isNew}
              empty={`Nothing recorded in ${M.name} in the last 30 days.`}
            />
            <Feed
              title="From the other modules"
              icon={<ArrowDownLeft className="h-4 w-4 text-muted" />}
              rows={d.handoffs}
              isNew={isNew}
              showModule
              empty={d.watches.length ? 'Nothing handed over in the last 30 days.' : 'This workspace hears from modules your role can’t open, so nothing is shown here.'}
              foot={d.watches.length > 0 && (
                <p className="mt-3 border-t border-line pt-3 text-2xs text-muted">
                  Listening for {d.watches.map((w) => w.name).join(', ')}: what the other modules hand {M.name}.
                </p>
              )}
            />
          </div>
          <p className="text-center text-2xs text-faint">One set of records: every module reads and writes the same books, stock and people, so what changes in one shows here as it happens.</p>
        </>
      )}
    </div>
  );
}

function Feed({ title, icon, rows, isNew, empty, showModule, foot }: { title: string; icon: React.ReactNode; rows: Row[]; isNew: (r: Row) => boolean; empty: string; showModule?: boolean; foot?: React.ReactNode }) {
  return (
    <section className="card min-w-0 p-4 sm:p-5" aria-label={title}>
      <h2 className="flex items-center gap-2 font-semibold">{icon}{title}</h2>
      {rows.length === 0 ? <p className="mt-3 text-sm text-muted">{empty}</p> : (
        <ul className="mt-2 divide-y divide-line">
          {rows.map((r) => {
            const R = r.module ? MODULES[r.module] : null;
            const to = OPEN[r.entity]?.(r.entityId);
            const text = <span className="font-medium">{r.what}</span>;
            return (
              <li key={r.id} className={cn('flex items-start gap-3 py-2.5 text-sm transition-colors', isNew(r) && 'animate-[pulse_1.2s_ease-in-out_2] rounded-md bg-brand-soft/60 px-2')}>
                {showModule && R ? (
                  <span className="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded-md text-white" style={{ background: R.color }} title={R.name}><R.icon className="h-3.5 w-3.5" /></span>
                ) : <span className={cn('mt-1.5 h-2 w-2 shrink-0 rounded-full', r.action === 'Added' ? 'bg-good' : r.action === 'Deleted' ? 'bg-bad' : 'bg-faint')} />}
                <div className="min-w-0 flex-1">
                  <p className="line-clamp-2">{to ? <Link to={to} className="hover:text-brand hover:underline">{text}</Link> : text}{isNew(r) && <span className="ml-2 rounded-full bg-brand px-1.5 py-px text-[10px] font-bold text-white uppercase">New</span>}</p>
                  <p className="text-2xs text-muted">{r.actor || 'System'}{showModule && R ? ` · ${R.name}` : ''} · {ago(r.at)}</p>
                </div>
                {to && <ArrowRight className="mt-1 h-3.5 w-3.5 shrink-0 text-faint" />}
              </li>
            );
          })}
        </ul>
      )}
      {foot}
    </section>
  );
}
