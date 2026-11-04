// English and Kiswahili for the app's frame and main labels. Plain words over accounting terms.
// (Have a native speaker review before launch.)
import { useEffect, useState } from 'react';

const sw = {
  Today: 'Leo',
  Sales: 'Mauzo',
  Invoices: 'Ankara',
  Quotes: 'Nukuu za bei',
  Customers: 'Wateja',
  'Money in': 'Pesa iliyoingia',
  Reports: 'Ripoti',
  Settings: 'Mipangilio',
  Assistant: 'Msaidizi',
  'Sign out': 'Toka',
  'New invoice': 'Ankara mpya',
  'New quote': 'Nukuu mpya',
  'New customer': 'Mteja mpya',
  'Record payment': 'Weka malipo',
  All: 'Zote',
  Draft: 'Rasimu',
  Unpaid: 'Hazijalipwa',
  Overdue: 'Zimechelewa',
  Paid: 'Zimelipwa',
  Owing: 'Wanadaiwa',
  'To match': 'Za kulinganisha',
  Matched: 'Zimelinganishwa',
  Save: 'Hifadhi',
  Cancel: 'Ghairi',
  'Good morning': 'Habari za asubuhi',
  'Good afternoon': 'Habari za mchana',
  'Good evening': 'Habari za jioni',
  'Needs you today': 'Yanayokuhitaji leo',
  'Money in, last 7 days': 'Pesa iliyoingia, siku 7',
  'Customers owe you': 'Wateja wanakudai',
  'Ask Msaidizi': 'Uliza Msaidizi',
  Purchases: 'Ununuzi',
  Suppliers: 'Wauzaji',
  Stock: 'Ghala',
  Bills: 'Bili',
  'Purchase orders': 'Maagizo ya ununuzi',
  'New bill': 'Bili mpya',
  'New order': 'Agizo jipya',
  'New supplier': 'Muuzaji mpya',
  'Count stock': 'Hesabu bidhaa',
  'Transfer stock': 'Hamisha bidhaa',
  'We owe': 'Tunawadai',
  'We owe suppliers': 'Tunawadai wauzaji',
  'Low stock': 'Bidhaa kidogo',
  'Out of stock': 'Imeisha',
  'In stock': 'Ipo',
} as const;

export type Lang = 'en' | 'sw';
export type Key = keyof typeof sw;

const KEY = 'kerp-lang';
const read = (): Lang => {
  try {
    return localStorage.getItem(KEY) === 'sw' ? 'sw' : 'en';
  } catch {
    return 'en';
  }
};

export function setLang(l: Lang) {
  try {
    localStorage.setItem(KEY, l);
  } catch {}
  window.dispatchEvent(new Event('kerp-lang'));
}

export function useLang() {
  const [lang, set] = useState<Lang>(read);
  useEffect(() => {
    const on = () => set(read());
    window.addEventListener('kerp-lang', on);
    return () => window.removeEventListener('kerp-lang', on);
  }, []);
  const t = (k: Key) => (lang === 'sw' ? sw[k] : k);
  return { lang, t };
}
