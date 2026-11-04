import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import { createPortal } from 'react-dom';
import { AlertTriangle, Printer, RotateCw, Search, X } from 'lucide-react';
import { cn } from '../lib/format';
import { printAs } from './PrintDoc';

/** The page title, and under it the action bar: the page's actions in one strip, always in the same place,
 * that stays in reach while the page scrolls (the pattern Dynamics 365 calls the action bar). */
export function PageHeader({ title, subtitle, actions, print }: { title: string; subtitle?: ReactNode; actions?: ReactNode; print?: string | boolean }) {
  // print: a Print button that puts this page on paper as a document (PrintDoc.tsx), named for the saved PDF.
  if (print) {
    const name = typeof print === 'string' ? print : title;
    actions = <>{actions}<button type="button" className="btn btn-ghost ml-auto" onClick={() => printAs(name)}><Printer className="h-4 w-4" /> Print / PDF</button></>;
  }
  return (
    <>
      <header className="mb-3 min-w-0">
        <h1 className="text-2xl leading-tight font-semibold tracking-tight sm:text-[28px]">{title}</h1>
        {subtitle && <div className="mt-1 text-sm text-muted">{subtitle}</div>}
      </header>
      {actions ? (
        <div role="toolbar" aria-label={`${title} actions`} className="sticky top-[61px] z-20 -mx-4 mb-5 border-y border-line bg-paper/95 px-4 py-2 backdrop-blur sm:-mx-6 sm:px-6 lg:top-14 lg:-mx-8 lg:px-8 print:hidden">
          <div className="no-scrollbar flex items-center gap-2 overflow-x-auto lg:flex-wrap lg:overflow-visible [&_.btn]:py-2">{actions}</div>
        </div>
      ) : (
        <div className="mb-5" />
      )}
    </>
  );
}

/** Filter chips that show how many each one returns, before it's tapped. */
export function Chips<T extends string>({ value, onChange, options, label }: { value: T; onChange: (v: T) => void; options: [T, string, number?][]; label: string }) {
  return (
    // Phones swipe through one row; wider screens wrap, so no option is ever hidden past the edge.
    <div className="no-scrollbar -mx-4 overflow-x-auto px-4 sm:mx-0 sm:overflow-visible sm:px-0" role="group" aria-label={label}>
      <div className="flex w-max gap-1.5 sm:w-auto sm:flex-wrap">
        {options.map(([id, text, n]) => (
          <button
            key={id}
            type="button"
            aria-pressed={value === id}
            onClick={() => onChange(id)}
            className={cn('flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-medium ring-1 transition', value === id ? 'bg-brand-soft text-brand ring-brand/40' : 'bg-white text-ink-2 ring-line hover:bg-paper')}
          >
            {text}
            {n !== undefined && <span className={cn('num text-xs font-medium', value === id ? 'text-brand/70' : 'text-faint')}>{n}</span>}
          </button>
        ))}
      </div>
    </div>
  );
}

/** A page's views, side by side (Work orders | Faults | Schedule). Phones swipe through them in one row. */
export function Tabs<T extends string>({ value, onChange, options, label }: { value: T; onChange: (v: T) => void; options: [T, string, number?][]; label: string }) {
  return (
    <div className="no-scrollbar -mx-4 overflow-x-auto px-4 sm:mx-0 sm:px-0">
      <div className="inline-flex rounded-full bg-white p-0.5 ring-1 ring-line" role="tablist" aria-label={label}>
        {options.map(([id, text, n]) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={value === id}
            onClick={() => onChange(id)}
            className={cn('flex items-center gap-1.5 rounded-full px-4 py-1.5 text-sm font-semibold whitespace-nowrap transition', value === id ? 'bg-brand-soft text-brand' : 'text-muted hover:text-ink')}
          >
            {text}
            {n ? <span className={cn('num rounded-full px-1.5 text-xs', value === id ? 'bg-white/70' : 'bg-paper ring-1 ring-line')}>{n}</span> : null}
          </button>
        ))}
      </div>
    </div>
  );
}

export function SearchBox({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder: string }) {
  return (
    <label className="relative block w-full sm:max-w-sm">
      <Search className="pointer-events-none absolute top-1/2 left-3 h-4 w-4 -translate-y-1/2 text-faint" />
      <input className="field pl-9" value={value} onChange={(e) => onChange(e.target.value)} placeholder={placeholder} aria-label={placeholder} />
    </label>
  );
}

export function Status({ tone, children }: { tone: 'good' | 'warn' | 'bad' | 'info' | 'plain'; children: ReactNode }) {
  const tones = { good: 'bg-good-soft text-good', warn: 'bg-warn-soft text-warn', bad: 'bg-bad-soft text-bad', info: 'bg-brand-soft text-brand', plain: 'bg-paper text-muted ring-1 ring-line' };
  return <span className={cn('inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-semibold whitespace-nowrap', tones[tone])}>{children}</span>;
}

/** While a page or section loads: grey placeholder rows where the content will be, not a lone spinner. */
export function Loading({ className }: { className?: string }) {
  return (
    <div className={cn('card space-y-3 p-5', className)} role="status" aria-label="Loading">
      {[72, 100, 88, 94, 60].map((w, i) => (
        <div key={i} className="flex items-center gap-3">
          <div className="h-3.5 animate-pulse rounded bg-line" style={{ width: `${w * 0.35}%` }} />
          <div className="h-3.5 flex-1 animate-pulse rounded bg-line/60" style={{ maxWidth: `${w * 0.4}%` }} />
          <div className="ml-auto h-3.5 w-16 animate-pulse rounded bg-line" />
        </div>
      ))}
    </div>
  );
}

export function ErrorNote({ children }: { children: ReactNode }) {
  return <p className="rounded-md bg-bad-soft px-3 py-2 text-sm font-medium text-bad" role="alert">{children}</p>;
}

/** A whole page or section that couldn't load: what happened, and a way to try again. */
export function PageError({ children }: { children: ReactNode }) {
  return (
    <div className="card flex flex-col items-center px-6 py-12 text-center" role="alert">
      <span className="grid h-10 w-10 place-items-center rounded-full bg-bad-soft text-bad">
        <AlertTriangle className="h-5 w-5" />
      </span>
      <p className="mt-3 text-base font-semibold">This didn’t load</p>
      <p className="mx-auto mt-1 max-w-md text-sm text-muted">{children}</p>
      <button type="button" className="btn btn-ghost mt-4" onClick={() => window.location.reload()}>
        <RotateCw className="h-4 w-4" /> Try again
      </button>
    </div>
  );
}

export function Empty({ title, children, action }: { title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className="card px-6 py-12 text-center">
      <p className="text-base font-semibold">{title}</p>
      {children && <div className="mx-auto mt-1 max-w-md text-sm text-muted">{children}</div>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  );
}

export function Field({ label, hint, children, className }: { label: string; hint?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <label className={cn('block min-w-0', className)}>
      <span className="mb-1.5 block text-sm font-semibold">{label}</span>
      {children}
      {hint && <span className="mt-1 block text-xs text-muted">{hint}</span>}
    </label>
  );
}

// Sheets can open over sheets: Escape closes only the top one, and the page stays still until the last one closes.
const openSheets: symbol[] = [];
const SheetContext = createContext<{ footer: HTMLElement | null; close: () => void } | null>(null);

const SHEET_WIDTHS = { sm: 'sm:max-w-[440px]', md: 'sm:max-w-[560px]', lg: 'sm:max-w-3xl lg:max-w-[880px]' };

/**
 * A panel that slides in from the right (full screen on phones), laid out like a Fluent / Dynamics 365 panel:
 * a white header with the title, an optional line saying what the panel is for, and a close button; the content
 * scrolls; the actions sit in a footer pinned to the bottom, so the main button is always in reach.
 * Put the actions in <SheetFooter> anywhere inside the panel (usually at the end of the form): they render in the
 * pinned footer and still submit the form they were written in.
 */
export function Sheet({ open, onClose, title, description, children, wide, size }: { open: boolean; onClose: () => void; title: string; description?: ReactNode; children: ReactNode; wide?: boolean; size?: keyof typeof SHEET_WIDTHS }) {
  const [footer, setFooter] = useState<HTMLElement | null>(null);
  useEffect(() => {
    if (!open) return;
    const me = Symbol(title);
    openSheets.push(me);
    const on = (e: KeyboardEvent) => e.key === 'Escape' && openSheets[openSheets.length - 1] === me && onClose();
    window.addEventListener('keydown', on);
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', on);
      openSheets.splice(openSheets.indexOf(me), 1);
      if (openSheets.length === 0) document.body.style.overflow = '';
    };
  }, [open, onClose, title]);
  if (!open) return null;
  return (
    <div className="fixed inset-0 z-50">
      <div className="absolute inset-0 animate-fade-in bg-ink/25" onClick={onClose} />
      <aside
        role="dialog"
        aria-modal="true"
        aria-label={title}
        className={cn('absolute inset-y-0 right-0 flex w-full animate-sheet-in flex-col bg-white shadow-[-8px_0_32px_rgb(10_11_30/0.14)]', SHEET_WIDTHS[size ?? (wide ? 'lg' : 'sm')])}
      >
        <header className="flex shrink-0 items-start justify-between gap-4 px-6 pt-5 pb-4">
          <div className="min-w-0">
            <h2 className="truncate text-xl font-semibold tracking-tight">{title}</h2>
            {description && <p className="mt-1 text-sm text-muted">{description}</p>}
          </div>
          <button onClick={onClose} className="-mt-1 -mr-2 grid h-9 w-9 shrink-0 place-items-center rounded-md text-muted hover:bg-paper hover:text-ink" aria-label="Close">
            <X className="h-5 w-5" />
          </button>
        </header>
        <div className="sheet-body min-h-0 flex-1 overflow-y-auto border-t border-line px-6 py-5">
          <SheetContext.Provider value={{ footer, close: onClose }}>{children}</SheetContext.Provider>
        </div>
        <div ref={setFooter} className="shrink-0 empty:hidden border-t border-line bg-white px-6 py-4 pb-[max(1rem,env(safe-area-inset-bottom))]" />
      </aside>
    </div>
  );
}

/**
 * The panel's actions, shown in its pinned footer: the main action first, then the others (Cancel, Delete…).
 * Written inside a <form>, a submit button here still submits that form, and Enter in a field still works.
 * A Cancel button that closes the panel comes last, unless cancel={false} (or another label, e.g. "Close").
 */
export function SheetFooter({ children, className, cancel = 'Cancel' }: { children?: ReactNode; className?: string; cancel?: string | false }) {
  const sheet = useContext(SheetContext);
  const slot = sheet?.footer ?? null;
  const marker = useRef<HTMLSpanElement>(null);
  const submit = (e: React.MouseEvent) => {
    const button = (e.target as HTMLElement).closest('button');
    const form = marker.current?.closest('form');
    if (!button || !form || button.disabled || (button.getAttribute('type') ?? 'submit') !== 'submit' || button.form) return;
    e.preventDefault();
    form.requestSubmit();
  };
  const actions = (
    <div onClickCapture={submit} className={cn('flex flex-wrap items-center gap-2 [&_.btn]:min-w-24', className)}>
      {children}
      {cancel && sheet && (
        <button type="button" className="btn btn-ghost" onClick={sheet.close}>
          {cancel}
        </button>
      )}
    </div>
  );
  return (
    <>
      {/* A hidden default button keeps "press Enter to submit" working in the form this footer was written in. */}
      <span ref={marker} hidden>{slot && <button type="submit" tabIndex={-1} />}</span>
      {slot ? createPortal(actions, slot) : actions}
    </>
  );
}

// ------------------------------------------------------------------------------------------------ Dialogs
// The app's own confirm, question and notice boxes, in place of the browser's (which can't be styled, look
// different in every browser and on phones show the site's address as their title). Call confirmDialog(),
// askDialog() or noticeDialog() from anywhere and await the answer; <DialogHost /> (mounted once, in main.tsx)
// draws them. They open over sheets, and Escape closes the dialog, not the sheet under it.

type DialogRequest = {
  title: string;
  body?: ReactNode;
  confirm: string;
  cancel: string | false;
  danger?: boolean;
  input?: { label: string; placeholder?: string; required?: boolean };
  resolve: (answer: string | null) => void;
};
let showDialog: ((d: DialogRequest) => void) | null = null;

function openDialog(d: Omit<DialogRequest, 'resolve'>) {
  return new Promise<string | null>((resolve) => (showDialog ? showDialog({ ...d, resolve }) : resolve(window.confirm(d.title) ? '' : null)));
}

/** Asks before doing something. Resolves true when the person confirms. */
export async function confirmDialog({ title, body, confirm = 'Confirm', cancel = 'Cancel', danger }: { title: string; body?: ReactNode; confirm?: string; cancel?: string; danger?: boolean }) {
  return (await openDialog({ title, body, confirm, cancel, danger })) !== null;
}

/** Asks for a line of text (a reason, a note). Resolves to the text, or null if they cancel. */
export function askDialog({ title, body, label, placeholder, required, confirm = 'Save', danger }: { title: string; body?: ReactNode; label: string; placeholder?: string; required?: boolean; confirm?: string; danger?: boolean }) {
  return openDialog({ title, body, confirm, cancel: 'Cancel', danger, input: { label, placeholder, required } });
}

/** Tells the person something that needs no answer. */
export async function noticeDialog({ title, body }: { title: string; body?: ReactNode }) {
  await openDialog({ title, body, confirm: 'OK', cancel: false });
}

export function DialogHost() {
  const [queue, setQueue] = useState<DialogRequest[]>([]);
  const [text, setText] = useState('');
  const d = queue[0];
  useEffect(() => {
    showDialog = (req) => setQueue((q) => [...q, req]);
    return () => void (showDialog = null);
  }, []);
  const answer = (value: string | null) => {
    d?.resolve(value);
    setText('');
    setQueue((q) => q.slice(1));
  };
  useEffect(() => {
    if (!d) return;
    const me = Symbol(d.title);
    openSheets.push(me);
    const on = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || openSheets[openSheets.length - 1] !== me) return;
      e.stopImmediatePropagation();
      answer(d.cancel === false ? '' : null);
    };
    window.addEventListener('keydown', on, true);
    const overflow = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      window.removeEventListener('keydown', on, true);
      openSheets.splice(openSheets.indexOf(me), 1);
      document.body.style.overflow = overflow;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [d]);
  if (!d) return null;
  const cancel = () => answer(d.cancel === false ? '' : null);
  return createPortal(
    <div className="fixed inset-0 z-[70] grid place-items-center p-4">
      <div className="absolute inset-0 animate-fade-in bg-ink/30" onClick={cancel} />
      <form
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="dialog-title"
        className="relative w-full max-w-[440px] animate-fade-in rounded-2xl bg-white shadow-[0_24px_64px_rgb(10_11_30/0.22)]"
        onSubmit={(e) => {
          e.preventDefault();
          if (d.input?.required && !text.trim()) return;
          answer(d.input ? text.trim() : '');
        }}
      >
        <div className="flex gap-3.5 px-6 pt-6">
          {d.danger && (
            <span className="grid h-9 w-9 shrink-0 place-items-center rounded-full bg-bad-soft text-bad">
              <AlertTriangle className="h-[18px] w-[18px]" />
            </span>
          )}
          <div className="min-w-0 pt-1">
            <h2 id="dialog-title" className="text-lg leading-snug font-semibold tracking-tight">
              {d.title}
            </h2>
            {d.body && <div className="mt-1.5 text-sm leading-relaxed text-muted">{d.body}</div>}
          </div>
        </div>
        {d.input && (
          <div className="px-6 pt-4">
            <Field label={d.input.label}>
              <input className="field" autoFocus value={text} onChange={(e) => setText(e.target.value)} placeholder={d.input.placeholder} required={d.input.required} />
            </Field>
          </div>
        )}
        <div className="mt-6 flex flex-col-reverse gap-2 border-t border-line px-6 py-4 sm:flex-row sm:justify-end [&_.btn]:min-w-24">
          {d.cancel !== false && (
            // For something that can't be undone the safe answer has focus, so a stray Enter cancels.
            <button type="button" className="btn btn-ghost" autoFocus={d.danger && !d.input} onClick={cancel}>
              {d.cancel}
            </button>
          )}
          <button type="submit" autoFocus={!d.input && !d.danger} className={cn('btn', d.danger ? 'bg-bad text-white hover:brightness-95' : 'btn-primary')} disabled={Boolean(d.input?.required && !text.trim())}>
            {d.confirm}
          </button>
        </div>
      </form>
    </div>,
    document.body,
  );
}

/** A number with what it means: big figure, label above, context below. */
export function Stat({ label, value, children, tone }: { label: string; value: ReactNode; children?: ReactNode; tone?: 'bad' | 'good' }) {
  return (
    <div className="card p-4 sm:p-5 min-w-0">
      <p className="text-sm font-medium text-muted truncate">{label}</p>
      <p className={cn('num mt-1 text-xl sm:text-2xl font-semibold tracking-tight break-words', tone === 'bad' && 'text-bad', tone === 'good' && 'text-good')}>{value}</p>
      {children && <div className="mt-1 text-xs text-muted break-words">{children}</div>}
    </div>
  );
}
