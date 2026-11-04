// "Tell me what you want to do" (Ctrl+K, or /): one box to reach any page, start any common action, or open
// any record (customer, invoice, item, supplier, employee, lead), like Dynamics 365's Tell Me. Pages and
// actions are matched here; records come from /api/search, limited to what the person's role may see.
import { useEffect, useMemo, useRef, useState } from 'react';
import { useNavigate } from 'react-router';
import { ArrowRight, CornerDownLeft, FileText, Search, Zap } from 'lucide-react';
import { api, can, type Session } from '../lib/api';
import { cn } from '../lib/format';

const HEADINGS: Record<string, string> = { Action: 'Actions', Page: 'Pages', Customer: 'Customers', Invoice: 'Invoices', Quote: 'Quotes', Item: 'Items', Lead: 'Leads', Supplier: 'Suppliers', Bill: 'Bills', 'Purchase order': 'Purchase orders', Employee: 'Employees' };

type Entry = { kind: string; title: string; subtitle?: string; url: string; keywords?: string };

function actionsFor(session: Session): Entry[] {
  const a: (Entry | false)[] = [
    can(session, 'sell') && { kind: 'Action', title: 'New invoice', url: '/invoices/new', keywords: 'bill customer sell ankara' },
    can(session, 'sell') && { kind: 'Action', title: 'New quote', url: '/quotes/new', keywords: 'quotation estimate nukuu' },
    can(session, 'sell') && { kind: 'Action', title: 'New customer', url: '/customers?new=1', keywords: 'add client mteja' },
    can(session, 'sell') && { kind: 'Action', title: 'Record a payment', url: '/money-in?new=1', keywords: 'mpesa cash receipt malipo' },
    can(session, 'crm') && { kind: 'Action', title: 'New lead', url: '/pipeline?new=1', keywords: 'prospect crm' },
    can(session, 'crm') && { kind: 'Action', title: 'New campaign', url: '/campaigns/new', keywords: 'sms whatsapp marketing offer' },
    can(session, 'finance') && { kind: 'Action', title: 'New bill', url: '/purchases/new?kind=Bill', keywords: 'supplier invoice expense' },
    can(session, 'buy') && { kind: 'Action', title: 'New purchase order', url: '/purchases/new?kind=Order', keywords: 'po order supplier' },
    can(session, 'stock') && { kind: 'Action', title: 'Count stock', url: '/stock?count=1', keywords: 'stocktake inventory hesabu' },
    can(session, 'make') && { kind: 'Action', title: 'Plan production', url: '/production', keywords: 'make batch recipe manufacture' },
    can(session, 'people') && { kind: 'Action', title: 'Run payroll', url: '/payroll', keywords: 'salaries paye nssf shif mishahara' },
    can(session, 'people') && { kind: 'Action', title: 'Approve leave', url: '/leave', keywords: 'holiday off likizo' },
    can(session, 'people') && { kind: 'Action', title: 'Add employee', url: '/people?new=1', keywords: 'staff hire worker' },
  ];
  const pages: (Entry | false)[] = [
    { kind: 'Page', title: 'Today', url: '/', keywords: 'home role centre dashboard leo' },
    can(session, 'sales.view') && { kind: 'Page', title: 'Invoices', url: '/invoices', keywords: 'sales ankara' },
    can(session, 'sales.view') && { kind: 'Page', title: 'Overdue invoices', url: '/invoices?status=overdue', keywords: 'late debtors owing' },
    can(session, 'sales.view') && { kind: 'Page', title: 'Quotes', url: '/quotes' },
    can(session, 'sales.view') && { kind: 'Page', title: 'Customers', url: '/customers', keywords: 'clients wateja' },
    can(session, 'sales.view') && { kind: 'Page', title: 'Money in', url: '/money-in', keywords: 'payments mpesa receipts match' },
    can(session, 'crm') && { kind: 'Page', title: 'Pipeline', url: '/pipeline', keywords: 'leads crm deals' },
    can(session, 'crm') && { kind: 'Page', title: 'Follow-ups', url: '/pipeline?view=follow-ups', keywords: 'call back reminders' },
    can(session, 'crm') && { kind: 'Page', title: 'Campaigns', url: '/campaigns', keywords: 'marketing sms whatsapp' },
    can(session, 'buy.view') && { kind: 'Page', title: 'Purchases', url: '/purchases', keywords: 'bills orders ununuzi' },
    can(session, 'stock.view') && { kind: 'Page', title: 'Stock', url: '/stock', keywords: 'inventory ghala' },
    can(session, 'stock.view') && { kind: 'Page', title: 'Low stock', url: '/stock?filter=low', keywords: 'reorder running out' },
    can(session, 'buy.view') && { kind: 'Page', title: 'Suppliers', url: '/suppliers', keywords: 'vendors wauzaji' },
    can(session, 'make') && { kind: 'Page', title: 'Production', url: '/production', keywords: 'manufacturing recipes' },
    can(session, 'reports') && { kind: 'Page', title: 'Profit and loss', url: '/reports', keywords: 'reports income' },
    can(session, 'finance') && { kind: 'Page', title: 'Supplier invoices from eTIMS', url: '/purchases/etims', keywords: 'kra etims purchases input vat' },
    can(session, 'finance') && { kind: 'Page', title: 'Reconcile M-Pesa and bank', url: '/reconcile', keywords: 'statement import bank mpesa csv reconciliation' },
    can(session, 'reports') && { kind: 'Page', title: 'Balance sheet', url: '/reports', keywords: 'assets liabilities equity' },
    can(session, 'reports') && { kind: 'Page', title: 'VAT report', url: '/reports?tab=vat', keywords: 'kra tax' },
    can(session, 'pos') && { kind: 'Page', title: 'Point of sale', url: '/pos', keywords: 'till cashier counter sell' },
    can(session, 'people') && { kind: 'Page', title: 'Employees', url: '/people', keywords: 'staff hr wafanyakazi' },
    can(session, 'people') && { kind: 'Page', title: 'Payroll', url: '/payroll', keywords: 'salaries payslips' },
    can(session, 'people') && { kind: 'Page', title: 'Leave', url: '/leave' },
    (can(session, 'sell') || can(session, 'finance') || can(session, 'buy') || can(session, 'admin')) && { kind: 'Page', title: 'Agents', url: '/agents', keywords: 'ai automation tasks review approve wakala' },
    can(session, 'admin') && { kind: 'Page', title: 'Settings', url: '/settings', keywords: 'company mpesa users team products' },
  ];
  return [...a, ...pages].filter(Boolean) as Entry[];
}

export default function TellMe({ session, open, onClose }: { session: Session; open: boolean; onClose: () => void }) {
  const navigate = useNavigate();
  const [q, setQ] = useState('');
  const [records, setRecords] = useState<Entry[]>([]);
  const [active, setActive] = useState(0);
  const input = useRef<HTMLInputElement>(null);
  const all = useMemo(() => actionsFor(session), [session]);

  useEffect(() => {
    if (!open) return;
    setQ('');
    setRecords([]);
    setActive(0);
    setTimeout(() => input.current?.focus(), 10);
  }, [open]);
  useEffect(() => {
    if (q.trim().length < 2) return setRecords([]);
    const t = setTimeout(() => api<any[]>(`/search?q=${encodeURIComponent(q.trim())}`).then((r) => setRecords(r.map((h) => ({ kind: h.kind, title: h.title, subtitle: h.subtitle, url: h.url })))).catch(() => {}), 200);
    return () => clearTimeout(t);
  }, [q]);

  const term = q.trim().toLowerCase();
  const matched = term ? all.filter((e) => `${e.title} ${e.keywords ?? ''}`.toLowerCase().includes(term)) : all.filter((e) => e.kind === 'Action').slice(0, 6);
  const results = [...matched.slice(0, 8), ...records];
  useEffect(() => setActive(0), [q, records.length]);

  const go = (e: Entry) => {
    onClose();
    navigate(e.url);
  };
  const onKey = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') (e.preventDefault(), setActive((i) => Math.min(results.length - 1, i + 1)));
    else if (e.key === 'ArrowUp') (e.preventDefault(), setActive((i) => Math.max(0, i - 1)));
    else if (e.key === 'Enter' && results[active]) go(results[active]);
    else if (e.key === 'Escape') onClose();
  };

  if (!open) return null;
  let lastKind = '';
  return (
    <div className="fixed inset-0 z-[60] flex items-start justify-center bg-ink/40 px-3 pt-[10vh]" onClick={onClose}>
      <div role="dialog" aria-modal="true" aria-label="Tell me what you want to do" className="w-full max-w-xl overflow-hidden rounded-lg bg-white shadow-2xl ring-1 ring-line" onClick={(e) => e.stopPropagation()}>
        <label className="flex items-center gap-3 border-b border-line px-4">
          <Search className="h-5 w-5 shrink-0 text-faint" />
          <input
            ref={input}
            autoFocus
            value={q}
            onChange={(e) => setQ(e.target.value)}
            onKeyDown={onKey}
            placeholder="Tell me what you want to do, or find a customer, invoice, item…"
            className="h-14 w-full bg-transparent text-sm outline-none placeholder:text-faint"
            aria-label="Search"
            role="combobox"
            aria-expanded="true"
            aria-controls="tellme-results"
          />
          <kbd className="hidden rounded border border-line px-1.5 py-0.5 text-2xs text-muted sm:block">Esc</kbd>
        </label>
        <ul id="tellme-results" role="listbox" className="max-h-[60vh] overflow-y-auto py-1">
          {!term && <li className="px-4 pt-2 pb-1 text-2xs font-semibold tracking-wider text-faint uppercase">Common actions</li>}
          {results.map((e, i) => {
            const showHeader = Boolean(term) && e.kind !== lastKind;
            lastKind = e.kind;
            return (
              <li key={`${e.kind}-${e.url}-${i}`}>
                {showHeader && <p className="px-4 pt-2 pb-1 text-2xs font-semibold tracking-wider text-faint uppercase">{HEADINGS[e.kind] ?? e.kind}</p>}
                <button
                  role="option"
                  aria-selected={i === active}
                  onMouseEnter={() => setActive(i)}
                  onClick={() => go(e)}
                  className={cn('flex w-full items-center gap-3 px-4 py-2.5 text-left', i === active ? 'bg-brand-soft' : '')}
                >
                  <span className={cn('grid h-8 w-8 shrink-0 place-items-center rounded-md', e.kind === 'Action' ? 'bg-ink text-gold' : 'bg-paper text-muted')}>
                    {e.kind === 'Action' ? <Zap className="h-4 w-4" /> : e.kind === 'Page' ? <ArrowRight className="h-4 w-4" /> : <FileText className="h-4 w-4" />}
                  </span>
                  <span className="min-w-0 flex-1">
                    <b className="block truncate text-sm">{e.title}</b>
                    {e.subtitle && <span className="block truncate text-xs text-muted">{e.subtitle}</span>}
                  </span>
                  {i === active && <CornerDownLeft className="h-4 w-4 text-faint" />}
                </button>
              </li>
            );
          })}
          {term && results.length === 0 && <li className="px-4 py-6 text-center text-sm text-muted">Nothing matches “{q}”. Try a customer name, an invoice number or what you want to do.</li>}
        </ul>
        <p className="border-t border-line px-4 py-2 text-2xs text-faint">↑↓ to move · Enter to open · Ctrl K from anywhere</p>
      </div>
    </div>
  );
}
