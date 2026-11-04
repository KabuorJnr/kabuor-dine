// Kabuor Dine's front door (/), for a restaurant owner deciding "will this stop the waiting, the wrong orders and the
// walk-outs, and is it worth KSh 3,000 a month?": what it is, how an order travels, what's included, the price, and
// the two ways in (start, or sign in).
import { useEffect } from 'react';
import { Link } from 'react-router';
import { BarChart3, BellRing, Check, ChefHat, QrCode, ShieldCheck, Smartphone, UtensilsCrossed } from 'lucide-react';
import { useApi } from '../lib/api';

const STEPS = [
  { icon: QrCode, title: 'Scan', text: 'The guest scans the card on the table. The menu opens on their phone: no app, no sign-in.' },
  { icon: UtensilsCrossed, title: 'Order', text: 'They choose dishes and how they like them, add a note, and send. A waiter confirms the table once.' },
  { icon: ChefHat, title: 'Cook', text: 'The kitchen and bar each see their own tickets, oldest first, with a timer.' },
  { icon: BellRing, title: 'Serve', text: 'The floor screen shows what’s ready, who’s calling and who wants the bill.' },
  { icon: Smartphone, title: 'Pay', text: 'An M-Pesa prompt on the guest’s phone, or cash and card to the waiter. eTIMS receipt included.' },
];

const FEATURES = [
  { icon: QrCode, title: 'A QR menu on every table', text: 'Sections, prices and choices (spice, sides, doneness), “Guests love” picks and “goes well with” suggestions.' },
  { icon: ChefHat, title: 'Kitchen and bar screens', text: 'Any tablet becomes a ticket screen, with a chime for new orders and tickets that turn amber, then red, as they wait.' },
  { icon: BellRing, title: 'The floor at a glance', text: 'Tables to confirm, guests calling, food ready on the pass and bills still to collect, sorted by what needs you first.' },
  { icon: Smartphone, title: 'M-Pesa at the table', text: 'Guests pay from their own phone; cash, card and paybill codes take two taps for the waiter.' },
  { icon: ShieldCheck, title: 'No fake orders, no walk-outs', text: 'A waiter confirms each new table, a lost card gets a new code, and only the owner can clear an unpaid table.' },
  { icon: BarChart3, title: 'Your night in numbers', text: 'Sales and money collected, the average bill, kitchen times, best sellers, busy hours and what guests said.' },
];

export default function Landing() {
  const { data: cfg } = useApi<{ plans: { id: string; monthly: number }[] }>('/client-config');
  const monthly = (cfg?.plans.find((p) => p.id === 'dine')?.monthly ?? 3_000_00) / 100;
  useEffect(() => { document.title = 'Kabuor Dine · QR ordering and payments for restaurants'; }, []);

  return (
    <div className="min-h-screen bg-white text-ink">
      <header className="bg-dine-night text-white">
        <div className="mx-auto flex max-w-6xl items-center justify-between gap-3 px-4 py-4 sm:px-6">
          <span className="flex items-center gap-2 font-bold"><span className="grid h-8 w-8 place-items-center rounded-lg bg-dine">D</span> Kabuor Dine</span>
          <nav className="flex items-center gap-2 text-sm font-semibold">
            <Link to="/signin" className="rounded-md px-3 py-2 text-white/80 hover:text-white">Sign in</Link>
            <Link to="/signup" className="btn bg-dine px-4 py-2 text-white hover:bg-dine-hover">Start</Link>
          </nav>
        </div>
        <div className="mx-auto grid max-w-6xl items-center gap-10 px-4 pt-8 pb-16 sm:px-6 lg:grid-cols-[1fr_1.05fr] lg:pt-12">
          <div>
            <p className="text-sm font-semibold tracking-wide text-orange-300">For restaurants, cafés, hotels and bars</p>
            <h1 className="mt-3 text-4xl leading-tight font-bold tracking-tight sm:text-5xl">Guests order from the table. The kitchen sees it at once.</h1>
            <p className="mt-5 max-w-xl text-lg text-white/70">A QR menu on every table, kitchen and bar screens, and the bill paid by M-Pesa from the guest’s own phone. Every sale lands in your books, with stock and KRA eTIMS taken care of.</p>
            <div className="mt-8 flex flex-wrap gap-3">
              <Link to="/signup" className="btn bg-dine px-6 py-3 text-base text-white hover:bg-dine-hover">Start Kabuor Dine</Link>
              <Link to="/signin" className="btn bg-white/10 px-6 py-3 text-base text-white hover:bg-white/15">Sign in</Link>
            </div>
            <p className="mt-4 text-sm text-white/55">KSh {monthly.toLocaleString('en-KE')} a month per branch, paid by M-Pesa.</p>
          </div>
          <div className="relative mx-auto w-full max-w-xl sm:pr-[14%] sm:pb-6">
            <img src="/brand/dine-floor.webp" alt="The floor screen: every table, and which need a waiter now" className="w-full rounded-xl shadow-2xl ring-1 ring-white/10" />
            <img src="/brand/dine-guest-phone.webp" alt="A guest's QR menu on a phone" className="absolute right-0 bottom-0 hidden w-[28%] rounded-2xl border-4 border-black shadow-2xl sm:block" />
          </div>
        </div>
      </header>

      <section className="mx-auto max-w-6xl px-4 py-14 sm:px-6">
        <h2 className="text-3xl font-bold tracking-tight">From the table to the kitchen to the till</h2>
        <ol className="mt-8 grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
          {STEPS.map((s, i) => (
            <li key={s.title} className="rounded-xl bg-dine-soft/60 p-5">
              <p className="text-sm font-semibold text-muted">{i + 1}</p>
              <s.icon className="mt-2 h-6 w-6 text-dine" />
              <h3 className="mt-3 text-lg font-semibold">{s.title}</h3>
              <p className="mt-1 text-sm text-muted">{s.text}</p>
            </li>
          ))}
        </ol>
        <img src="/brand/dine-kitchen.webp" alt="The kitchen screen: a ticket with the guest's choices and a note for the cook" className="mt-8 w-full rounded-xl ring-1 ring-line" />
      </section>

      <section className="border-y border-line bg-paper">
        <div className="mx-auto max-w-6xl px-4 py-14 sm:px-6">
          <h2 className="text-3xl font-bold tracking-tight">Everything a service needs</h2>
          <ul className="mt-8 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
            {FEATURES.map((f) => (
              <li key={f.title} className="rounded-xl bg-white p-6 ring-1 ring-line">
                <f.icon className="h-6 w-6 text-dine" />
                <h3 className="mt-3 text-lg font-semibold">{f.title}</h3>
                <p className="mt-1 text-sm text-muted">{f.text}</p>
              </li>
            ))}
          </ul>
        </div>
      </section>

      <section className="mx-auto grid max-w-6xl gap-10 px-4 py-14 sm:px-6 lg:grid-cols-[1.2fr_1fr]">
        <div>
          <h2 className="text-3xl font-bold tracking-tight">One price per branch</h2>
          <p className="mt-3 text-muted">Everything is switched on from the first day. Pay monthly or yearly by M-Pesa; a year costs ten months. Your guests pay your own till or paybill, with no commission.</p>
          <p className="mt-6 text-sm text-muted">Need payroll, purchasing or several branches later? Kabuor Dine runs on the Kabuor ERP engine: switch on the full ERP from your plan screen, with nothing to move.</p>
        </div>
        <div className="rounded-2xl p-6 ring-2 ring-dine sm:p-8">
          <h3 className="text-xl font-semibold">Kabuor Dine</h3>
          <p className="mt-4"><span className="text-3xl font-bold">KSh {monthly.toLocaleString('en-KE')}</span> <span className="text-muted">a month</span></p>
          <ul className="mt-5 space-y-2 border-t border-line pt-5 text-sm">
            {['QR menu and printable table cards', 'Kitchen and bar screens', 'Floor screen and waiter ordering', 'M-Pesa at the table, eTIMS receipts', 'Sales, stock and books behind every bill', '10 staff logins'].map((x) => (
              <li key={x} className="flex gap-2"><Check className="mt-0.5 h-4 w-4 shrink-0 text-good" /> {x}</li>
            ))}
          </ul>
          <Link to="/signup" className="btn mt-6 w-full bg-dine py-3 text-white hover:bg-dine-hover">Start Kabuor Dine</Link>
        </div>
      </section>

      <footer className="border-t border-line">
        <div className="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-3 px-4 py-6 text-sm text-muted sm:px-6">
          <span>© {new Date().getFullYear()} Kabuor Technologies · Kabuor Dine</span>
          <span className="flex gap-4"><Link to="/terms" className="hover:text-ink">Terms</Link><Link to="/privacy" className="hover:text-ink">Privacy</Link></span>
        </div>
      </footer>
    </div>
  );
}
