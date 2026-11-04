// A table's bill on paper (/dine/bill/:id): the receipt a waiter brings to the table or hands over after paying.
// Restaurants print on thermal receipt printers, so it's laid out for 80 mm (or 58 mm, or A4) paper: the restaurant,
// the table, every dish with its choices, the total with VAT included, what's paid and what's left, how to pay by
// M-Pesa, and KRA eTIMS's signature with its QR code once the bill is signed.
import { useEffect, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import QRCode from 'qrcode';
import { ArrowLeft, Printer } from 'lucide-react';
import { useApi } from '../lib/api';
import { ksh, prettyPhone } from '../lib/format';
import { Loading, PageError } from '../components/ui';
import type { Visit } from './lib';

type Detail = { table: { id: string; name: string; area: string }; visit: Visit; phone: string; openedAt: string };
type Letterhead = { name: string; kraPin: string; phone: string; email: string; county: string; address: string; vatRegistered: boolean };
type Settings = { paybill: string; mpesaPrompt: boolean };
type Paper = '80' | '58' | 'a4';

const PAPER_KEY = 'kerp-receipt-paper';

export default function BillPrint() {
  const { id = '' } = useParams();
  const [params] = useSearchParams();
  const bill = useApi<Detail>(`/dine/sessions/${id}`);
  const head = useApi<Letterhead>('/company/letterhead');
  const settings = useApi<Settings>('/dine/settings');
  const [paper, setPaper] = useState<Paper>(() => { try { return (localStorage.getItem(PAPER_KEY) as Paper) ?? '80'; } catch { return '80'; } });
  const [qr, setQr] = useState('');

  // The paper is the printer's: remembered on this device, and set for the browser's print.
  useEffect(() => {
    try { localStorage.setItem(PAPER_KEY, paper); } catch { /* fine */ }
    const style = document.createElement('style');
    style.textContent = paper === 'a4'
      ? '@page { size: A4; margin: 16mm } @media print { .receipt-paper { width: 92mm; margin: 0 auto; font-size: 12px; color: #000 } }'
      : `@page { size: ${paper}mm auto; margin: 0 } @media print { .receipt-paper { width: ${paper === '58' ? 48 : 72}mm; margin: 0 auto; padding: 3mm 0 8mm; font-size: ${paper === '58' ? 10 : 11.5}px; color: #000 } }`;
    document.head.appendChild(style);
    return () => style.remove();
  }, [paper]);

  const signed = bill.data?.visit.receipts.find((r) => r.etimsQrUrl);
  useEffect(() => {
    if (!signed?.etimsQrUrl) return;
    QRCode.toDataURL(signed.etimsQrUrl, { margin: 0, width: 220, errorCorrectionLevel: 'M' }).then(setQr).catch(() => {});
  }, [signed?.etimsQrUrl]);

  const ready = bill.data && head.data && settings.data;
  // Opened from the floor's Print bill button: print as soon as everything is drawn.
  useEffect(() => {
    if (!ready || params.get('print') !== '1') return;
    const t = window.setTimeout(() => window.print(), 400);
    return () => window.clearTimeout(t);
  }, [ready, params]);

  if (bill.error) return <div className="p-6"><PageError>{bill.error}</PageError></div>;
  if (!ready) return <Loading />;
  const { visit: v, table } = bill.data!;
  const r = head.data!;
  const s = settings.data!;
  const now = new Date();
  const paid = v.total > 0 && v.due === 0;
  const lines = v.orders.flatMap((o) => o.lines.map((l) => ({ ...l, order: o.number })));
  const vat = r.vatRegistered ? Math.round((v.total * 16) / 116) : 0;

  return (
    <div className="min-h-screen bg-cream print:bg-white">
      <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-dine/10 bg-white/90 px-4 py-3 backdrop-blur print:hidden">
        <Link to="/dine" className="btn btn-ghost px-3"><ArrowLeft className="h-4 w-4" /> Floor</Link>
        <span className="flex-1" />
        <select value={paper} onChange={(e) => setPaper(e.target.value as Paper)} aria-label="Paper" className="field w-auto py-2">
          <option value="80">80 mm receipt</option>
          <option value="58">58 mm receipt</option>
          <option value="a4">A4 page</option>
        </select>
        <button onClick={() => window.print()} className="btn bg-dine text-white hover:bg-dine-hover"><Printer className="h-4 w-4" /> Print</button>
      </div>

      <div className="mx-auto my-6 w-[80mm] max-w-full rounded-lg bg-white px-5 py-6 shadow-sm ring-1 ring-line print:my-0 print:w-auto print:rounded-none print:p-0 print:shadow-none print:ring-0">
        <div className="receipt-paper font-mono text-[12px] leading-relaxed text-black">
          <div className="text-center">
            <p className="font-sans text-[17px] leading-tight font-extrabold tracking-tight">{r.name}</p>
            {(r.address || r.county) && <p>{[r.address, r.county].filter(Boolean).join(', ')}</p>}
            {r.phone && <p>Tel {prettyPhone(r.phone)}</p>}
            {r.kraPin && <p>KRA PIN {r.kraPin}</p>}
          </div>

          <Rule />
          <p className="text-center text-[13px] font-bold tracking-[0.2em]">{paid ? 'RECEIPT' : 'BILL'}</p>
          <Row left={table.name + (table.area ? ` · ${table.area}` : '')} right={v.number} bold />
          <Row left={now.toLocaleDateString('en-KE', { day: 'numeric', month: 'short', year: 'numeric' })} right={now.toLocaleTimeString('en-KE', { hour: '2-digit', minute: '2-digit' })} />
          {v.receipts.length > 0 && <Row left="Invoice" right={v.receipts.map((x) => x.number).join(', ')} />}
          <Rule />

          {lines.map((l, i) => (
            <div key={i} className="mb-1.5 break-inside-avoid">
              <Row left={`${l.quantity} × ${l.name}`} right={ksh(l.unitPrice * l.quantity)} />
              {(l.options || l.note) && <p className="pl-4 text-[0.92em] text-neutral-600">{[l.options, l.note].filter(Boolean).join(' · ')}</p>}
              {l.quantity > 1 && <p className="pl-4 text-[0.92em] text-neutral-600">@ {ksh(l.unitPrice)}</p>}
            </div>
          ))}
          {lines.length === 0 && <p className="text-center text-neutral-600">Nothing ordered yet.</p>}

          <Rule />
          <Row left="TOTAL" right={ksh(v.total)} big />
          {vat > 0 && <Row left="Includes VAT (16%)" right={ksh(vat)} muted />}
          {v.paid > 0 && <Row left="Paid" right={ksh(v.paid)} />}
          {!paid && <Row left="To pay" right={ksh(v.due)} bold />}

          {!paid && v.due > 0 && (
            <>
              <Rule />
              <div className="text-center">
                <p className="font-bold">Pay by M-Pesa</p>
                {s.mpesaPrompt && <p>Scan the table’s QR and tap Pay: the prompt comes to your phone.</p>}
                {s.paybill && <p>Paybill <b>{s.paybill}</b> · Account <b>{v.receipts[0]?.number ?? v.number}</b></p>}
                {!s.paybill && !s.mpesaPrompt && <p>Your waiter will take cash, card or M-Pesa.</p>}
              </div>
            </>
          )}

          {signed && (
            <>
              <Rule />
              <div className="text-center">
                <p className="font-bold">KRA eTIMS</p>
                {signed.etimsInvoiceNo && <p>Invoice {signed.etimsInvoiceNo}</p>}
                {signed.etimsControlCode && <p className="break-all">CU {signed.etimsControlCode}</p>}
                {qr && <img src={qr} alt="KRA eTIMS QR code" className="mx-auto mt-2 h-28 w-28" />}
              </div>
            </>
          )}

          <Rule />
          <p className="text-center font-bold">Asante! Karibu tena.</p>
          <p className="mt-1 text-center text-[0.85em] text-neutral-500">Prices include VAT where it applies · Kabuor Dine</p>
        </div>
      </div>
    </div>
  );
}

function Rule() {
  return <div className="my-2.5 border-t border-dashed border-black/40" />;
}

function Row({ left, right, bold, big, muted }: { left: string; right: string; bold?: boolean; big?: boolean; muted?: boolean }) {
  return (
    <p className={`flex items-baseline justify-between gap-3 ${bold || big ? 'font-bold' : ''} ${big ? 'text-[1.25em]' : ''} ${muted ? 'text-neutral-600' : ''}`}>
      <span className="min-w-0">{left}</span>
      <span className="shrink-0 tabular-nums">{right}</span>
    </p>
  );
}
