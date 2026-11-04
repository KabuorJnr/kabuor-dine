// What the person sees of live sync: a small "Live" mark in the app bar, and a short notice when someone else changes
// something in a module they can open ("Peter Otieno · Request RQ-0012 moved to approved"). The screen itself has
// already refreshed by then; the notice only says why the numbers moved.
import { useCallback, useEffect, useRef, useState } from 'react';
import { X } from 'lucide-react';
import { useLiveEvents, useLiveStatus, type LiveEvent } from '../lib/live';
import { MODULES, type ModuleKey } from '../lib/modules';
import { cn } from '../lib/format';

export function LiveMark({ className }: { className?: string }) {
  const s = useLiveStatus();
  if (s === 'off') return null;
  const live = s === 'live';
  return (
    <span className={cn('inline-flex items-center gap-1.5 rounded-full px-2 py-0.5 text-2xs font-semibold ring-1', live ? 'text-good ring-good/30' : 'text-warn ring-warn/30', className)}
      title={live ? 'Live: changes made by others in any module show here as they happen.' : 'Reconnecting to live updates… Pages still load; they refresh by themselves once the line is back.'}>
      <span className={cn('h-1.5 w-1.5 rounded-full', live ? 'bg-good' : 'animate-pulse bg-warn')} />
      {live ? 'Live' : 'Reconnecting'}
    </span>
  );
}

type Notice = { id: number; actor: string; module: ModuleKey; what: string; more: number };

export function LiveNotices() {
  const [items, setItems] = useState<Notice[]>([]);
  const seq = useRef(0);
  const on = useCallback((e: LiveEvent) => {
    const first = e.changes[0];
    if (!first || !e.actor) return; // a change in a module this person can't open: refresh quietly
    const module = (first.module in MODULES ? first.module : 'intelligence') as ModuleKey;
    const n: Notice = { id: ++seq.current, actor: e.actor, module, what: first.what, more: Math.max(0, e.total - 1) };
    setItems((xs) => [...xs.filter((x) => !(x.actor === n.actor && x.what === n.what)), n].slice(-3));
  }, []);
  useLiveEvents(on);

  useEffect(() => {
    if (!items.length) return;
    const t = setTimeout(() => setItems((xs) => xs.slice(1)), 6000);
    return () => clearTimeout(t);
  }, [items]);

  if (!items.length) return null;
  return (
    <div className="pointer-events-none fixed right-4 bottom-20 z-40 flex w-[min(22rem,calc(100vw-2rem))] flex-col gap-2 lg:bottom-4 print:hidden" aria-live="polite">
      {items.map((n) => {
        const M = MODULES[n.module];
        return (
          <div key={n.id} className="pointer-events-auto flex items-start gap-2.5 rounded-lg bg-white p-3 text-sm shadow-lg ring-1 ring-line">
            <span className="mt-0.5 grid h-6 w-6 shrink-0 place-items-center rounded-md text-white" style={{ background: M.color }}><M.icon className="h-3.5 w-3.5" /></span>
            <div className="min-w-0 flex-1">
              <p className="truncate text-2xs text-muted"><b className="text-ink-2">{n.actor}</b> · {M.name}</p>
              <p className="line-clamp-2 font-medium">{n.what}{n.more > 0 && <span className="font-normal text-muted"> and {n.more} more</span>}</p>
            </div>
            <button onClick={() => setItems((xs) => xs.filter((x) => x.id !== n.id))} className="text-faint hover:text-ink" aria-label="Dismiss"><X className="h-4 w-4" /></button>
          </div>
        );
      })}
    </div>
  );
}
