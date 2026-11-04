// Money arrives from the API in cents. Show shillings: "KSh 12,500" (cents only when there are some).
const whole = new Intl.NumberFormat('en-KE', { maximumFractionDigits: 0 });
const exact = new Intl.NumberFormat('en-KE', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

export const ksh = (cents: number | null | undefined) => {
  const c = cents ?? 0;
  return `KSh ${c % 100 === 0 ? whole.format(c / 100) : exact.format(c / 100)}`;
};
export const kshShort = (cents: number) => {
  const v = Math.abs(cents / 100);
  const s = v >= 1e9 ? `${(v / 1e9).toFixed(2).replace(/\.?0+$/, '')}B` : v >= 1e6 ? `${(v / 1e6).toFixed(v >= 1e7 ? 0 : 1)}M` : v >= 1e4 ? `${Math.round(v / 1e3)}K` : whole.format(v);
  return `${cents < 0 ? '−' : ''}KSh ${s}`;
};
/** The currencies an invoice can be in besides shillings (the same list as the server's Currencies.cs). */
export const CURRENCIES = ['USD', 'EUR', 'GBP', 'UGX', 'TZS', 'RWF', 'ZAR', 'AED', 'CNY', 'INR'] as const;
/** An amount in a document's currency: "USD 1,250.00"; shillings (no currency) as ksh() shows them. */
export const money = (cents: number | null | undefined, currency?: string | null) => (currency ? `${currency} ${exact.format((cents ?? 0) / 100)}` : ksh(cents));
/** "129.5" → "129.50", a rate as people read it off CBK's page. */
export const rateText = (rate: number) => rate.toLocaleString('en-KE', { minimumFractionDigits: 2, maximumFractionDigits: 6 });
/** "1,250.50" typed by a person → 125050 cents. */
export const toCents = (text: string) => Math.round(Number(String(text).replace(/[^\d.]/g, '') || 0) * 100);
export const fromCents = (cents: number) => (cents / 100).toString();

export const day = (iso?: string | null) => (iso ? new Date(iso.length === 10 ? `${iso}T00:00:00` : iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short' }) : '—');
export const dayYear = (iso?: string | null) => (iso ? new Date(iso.length === 10 ? `${iso}T00:00:00` : iso).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' }) : '—');
export const dateTime = (iso: string) => new Date(iso).toLocaleString('en-GB', { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
export const todayIso = () => new Date(Date.now() + 3 * 3600e3).toISOString().slice(0, 10);
export const daysBetween = (a: string, b: string) => Math.round((Date.parse(b) - Date.parse(a)) / 864e5);

export const cn = (...parts: (string | false | null | undefined)[]) => parts.filter(Boolean).join(' ');

/** Kenyan mobile → 2547XXXXXXXX for wa.me links (null if it isn't one). */
export function msisdn(phone?: string | null) {
  const d = (phone ?? '').replace(/\D/g, '');
  const local = d.startsWith('254') ? d.slice(3) : d.startsWith('0') ? d.slice(1) : d;
  return /^[17]\d{8}$/.test(local) ? `254${local}` : null;
}
export const prettyPhone = (phone?: string | null) => {
  const m = msisdn(phone);
  return m ? `0${m.slice(3, 6)} ${m.slice(6, 9)} ${m.slice(9)}` : (phone ?? '');
};
