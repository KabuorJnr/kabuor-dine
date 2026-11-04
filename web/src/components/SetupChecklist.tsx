// The setup checklist on Today, for a business that has just started. The owner is deciding "what do I do next to
// actually start selling?", so it shows how far along they are, puts the next step first with the one button that
// does it (on the real screen, not a copy of it), and folds away what's done. It can be hidden, and it leaves on its
// own once everything is done.
import { useState } from 'react';
import { Link } from 'react-router';
import { ArrowRight, Check, X } from 'lucide-react';
import { useApi, useSession } from '../lib/api';
import { cn } from '../lib/format';

type Step = { key: string; title: string; detail: string; done: boolean; href: string; action: string };
type Status = { completionPercentage: number; steps?: Step[] };

const HIDDEN = 'kerp-setup-hidden';
export const SETUP_PATH = '/onboarding/status';

export default function SetupChecklist() {
  const session = useSession();
  const { data } = useApi<Status>(SETUP_PATH);
  const key = `${HIDDEN}:${session?.company.id}`;
  const [hidden, setHidden] = useState(() => {
    try { return localStorage.getItem(key) === '1'; } catch { return false; }
  });
  if (!data?.steps || hidden) return null;
  const steps = data.steps;
  const done = steps.filter((s) => s.done).length;
  if (done === steps.length) return null;
  const next = steps.find((s) => !s.done)!;
  const hide = () => {
    try { localStorage.setItem(key, '1'); } catch {}
    setHidden(true);
  };

  return (
    <section className="card overflow-hidden" aria-labelledby="setup-title">
      <div className="flex items-start gap-4 p-4 sm:p-5">
        <div className="min-w-0 flex-1">
          <h2 id="setup-title" className="font-display text-lg font-semibold">Get {session?.company.name} ready to sell</h2>
          <p className="mt-0.5 text-sm text-muted">{done} of {steps.length} done · about {Math.max(2, (steps.length - done) * 2)} minutes left</p>
          <div className="mt-3 h-1.5 overflow-hidden rounded-full bg-paper ring-1 ring-line" role="progressbar" aria-valuemin={0} aria-valuemax={steps.length} aria-valuenow={done} aria-label="Setup progress">
            <div className="h-full rounded-full bg-good transition-all" style={{ width: `${(done / steps.length) * 100}%` }} />
          </div>
        </div>
        <button onClick={hide} className="grid h-8 w-8 shrink-0 place-items-center rounded-full text-muted hover:bg-paper" aria-label="Hide the setup checklist" title="Hide (you can finish later from Settings)">
          <X className="h-4 w-4" />
        </button>
      </div>
      <ol className="divide-y divide-line border-t border-line">
        {steps.map((s, i) => {
          const isNext = s === next;
          return (
            // On a phone the button goes under the words; beside them it squeezed the text to a word per line.
            <li key={s.key} className={cn('flex flex-wrap items-center gap-x-3 gap-y-2 px-4 py-3 sm:flex-nowrap sm:px-5', isNext && 'bg-brand-soft/50')}>
              <span className={cn('grid h-6 w-6 shrink-0 place-items-center rounded-full text-xs font-bold', s.done ? 'bg-good text-white' : isNext ? 'bg-brand text-white' : 'bg-paper text-muted ring-1 ring-line')}>
                {s.done ? <Check className="h-3.5 w-3.5" /> : i + 1}
              </span>
              <span className="min-w-0 flex-1 basis-[calc(100%-2.25rem)] sm:basis-auto">
                <span className={cn('block text-sm font-semibold', s.done && 'text-muted line-through decoration-faint')}>{s.title}</span>
                {!s.done && <span className="block text-sm text-muted">{s.detail}</span>}
              </span>
              {!s.done && (
                <Link to={s.href} className={cn('ml-9 shrink-0 sm:ml-0', isNext ? 'btn btn-primary' : 'btn btn-ghost hidden sm:inline-flex')}>
                  {s.action} {isNext && <ArrowRight className="h-4 w-4" />}
                </Link>
              )}
            </li>
          );
        })}
      </ol>
    </section>
  );
}
