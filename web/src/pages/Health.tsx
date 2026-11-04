// ERP health: how well the business is using its system, in Wallace's ABCD terms. Each check is measured from the data,
// with its target, why it matters and the page that fixes it.
import { Link } from 'react-router';
import { CheckCircle2, CircleAlert, CircleDashed } from 'lucide-react';
import { useApi } from '../lib/api';
import { cn } from '../lib/format';
import { MODULES } from '../lib/modules';
import { Loading, PageError, PageHeader } from '../components/ui';

type Check = { area: string; key: string; name: string; value: number | null; target: number; unit: string; why: string; fix: string; link: string; score: number; met: boolean; measured: boolean };
type Health = { overall: number; grade: 'A' | 'B' | 'C' | 'D'; meaning: string; areas: { area: string; score: number | null }[]; checks: Check[] };

const GRADE: Record<Health['grade'], string> = { A: 'text-good', B: 'text-brand', C: 'text-warn', D: 'text-bad' };

export default function HealthPage({ embedded }: { embedded?: boolean }) {
  const { data, error } = useApi<Health>('/os/health');
  if (error) return <PageError>{error}</PageError>;
  if (!data) return <Loading />;
  return (
    <div className="space-y-6">
      {!embedded && <PageHeader print title="ERP health" subtitle="Is the system running the business, or just recording it? Every check is measured from your own data." />}
      <section className="card flex flex-wrap items-center gap-6 p-5">
        <div className="text-center">
          <p className={cn('text-6xl font-black leading-none', GRADE[data.grade])}>{data.grade}</p>
          <p className="num mt-1 text-sm text-muted">{data.overall} / 100</p>
        </div>
        <p className="min-w-0 flex-1 text-sm sm:text-base">{data.meaning}</p>
        <div className="grid w-full grid-cols-2 gap-3 sm:w-auto sm:grid-cols-4">
          {data.areas.map((a) => (
            <div key={a.area} className="rounded-md bg-paper p-3 text-center ring-1 ring-line">
              <p className="text-2xs font-semibold tracking-wider text-muted uppercase">{a.area}</p>
              <p className="num text-xl font-bold">{a.score ?? '–'}</p>
            </div>
          ))}
        </div>
      </section>
      {data.areas.map((a) => (
        <section key={a.area}>
          <h2 className="mb-2 font-semibold">{a.area}</h2>
          <ul className="card divide-y divide-line">
            {data.checks.filter((c) => c.area === a.area).map((c) => (
              <li key={c.key} className="flex flex-wrap items-start gap-3 px-4 py-3 text-sm">
                {!c.measured ? <CircleDashed className="mt-0.5 h-5 w-5 shrink-0 text-faint" aria-label="Not measured yet" /> : c.met ? <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-good" aria-label="Met" /> : <CircleAlert className="mt-0.5 h-5 w-5 shrink-0 text-warn" aria-label="Not met" />}
                <div className="min-w-0 flex-1">
                  <p className="font-semibold">{c.name}</p>
                  <p className="text-muted">{c.why}</p>
                  {!c.met && <p className="mt-1">{c.fix} <Link className="font-semibold text-brand hover:underline" to={c.link}>Go there</Link></p>}
                </div>
                <div className="w-36 shrink-0 text-right">
                  <p className="num font-semibold">{c.measured ? `${c.value}${c.unit}` : 'Not measured yet'}</p>
                  <p className="text-xs text-muted">target {c.target}{c.unit}</p>
                  <div className="mt-1 h-1.5 rounded-full bg-paper ring-1 ring-line"><div className="h-1.5 rounded-full" style={{ width: `${c.score}%`, background: c.met ? 'var(--color-good)' : MODULES.intelligence.color }} /></div>
                </div>
              </li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
}
