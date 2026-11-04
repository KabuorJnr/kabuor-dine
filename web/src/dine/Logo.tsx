// The Kabuor Dine logo: the cloche mark (a walnut serving dome with a K on an oak board, steam rising) and the stacked
// wordmark, whose A is a wooden table with the covered dish on it, in the
// same family as Kabuor Builds and Kabuor ERP (brand/logo.html holds the master artwork).
import { useId } from 'react';
import { cn } from '../lib/format';

/** The full lockup as drawn in brand/logo.html: the cloche, "Kabuor / Dine" stacked, and "Karibu mezani." */
export function DineLockup({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-logo-on-dark.png' : '/brand/kabuor-dine-logo.png'} alt="Kabuor Dine. Karibu mezani." className={cn('w-auto', className)} />;
}

export function DineMark({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-mark-on-dark.svg' : '/brand/kabuor-dine-mark.svg'} alt="" aria-hidden className={cn('w-auto', className)} />;
}

/** The A of "Kabuor": a wooden table with the covered dish on it. The dish is the A's point, the table top its crossbar
 * and the table's legs its legs. Each copy has its own gradient ids, so several logos on a page don't clash. */
export function TableA({ className }: { className?: string }) {
  const id = useId().replace(/[^a-zA-Z0-9_-]/g, '');
  const u = (n: string) => `url(#${id}${n})`;
  return (
    <svg viewBox="0 0 80 70" aria-hidden className={cn('inline-block h-[0.7em] w-[0.8em] align-[-0.02em]', className)} style={{ margin: '0 0.03em 0 0.05em' }}>
      <defs>
        <linearGradient id={`${id}oak`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#E8BE84" /><stop offset=".55" stopColor="#C98D50" /><stop offset="1" stopColor="#A46A36" /></linearGradient>
        <linearGradient id={`${id}leg`} x1="0" y1="0" x2="1" y2="0"><stop offset="0" stopColor="#A86C38" /><stop offset="1" stopColor="#70431E" /></linearGradient>
        <linearGradient id={`${id}wal`} x1="0" y1="0" x2="1" y2="1"><stop offset="0" stopColor="#A9734C" /><stop offset=".55" stopColor="#7C4C2F" /><stop offset="1" stopColor="#55321D" /></linearGradient>
        <clipPath id={`${id}dome`}><path d="M16 33 A24 25 0 0 1 64 33 Z" /></clipPath>
      </defs>
      <path d="M22 46 L37 46 L18 68 L2 68 Z" fill={u('leg')} />
      <path d="M43 46 L58 46 L78 68 L62 68 Z" fill={u('leg')} />
      <rect x="0" y="36" width="80" height="11" rx="3" fill={u('oak')} />
      <rect x="0" y="36" width="80" height="2.6" rx="1.3" fill="#F6D9A8" opacity=".8" />
      <path d="M5 42.5 Q28 40.6 50 42.8 T76 41.6" fill="none" stroke="#7A4A22" strokeOpacity=".4" strokeWidth=".9" />
      <rect x="10" y="32" width="60" height="4.5" rx="2.25" fill="#F7EFE6" />
      <path d="M16 33 A24 25 0 0 1 64 33 Z" fill={u('wal')} />
      <g clipPath={u('dome')} fill="none" stroke="#2E170A" strokeOpacity=".22" strokeWidth=".9">
        <path d="M12 32 Q40 15 68 32" />
        <path d="M12 25 Q40 8 68 25" />
        <path d="M12 18 Q40 1 68 18" />
      </g>
      <path d="M22 29 A19 20 0 0 1 32 15" fill="none" stroke="#FFFFFF" strokeOpacity=".25" strokeWidth="2.4" strokeLinecap="round" />
      <rect x="38" y="7" width="4" height="3" fill="#55321D" />
      <circle cx="40" cy="5" r="4.8" fill={u('wal')} />
    </svg>
  );
}

/** "Kabuor Dine" in words, with the table-and-dish A. `dine` colours the second word. */
export function DineWord({ dine, className }: { dine?: string; className?: string }) {
  return (
    <span className={className} aria-label="Kabuor Dine">
      <span aria-hidden>K<TableA />buor <span className={dine}>Dine</span></span>
    </span>
  );
}

/** Mark and wordmark. size: sm for headers, md for sign-in, lg for the landing page. */
export function DineLogo({ size = 'sm', dark = false, tagline = false, className }: { size?: 'sm' | 'md' | 'lg'; dark?: boolean; tagline?: boolean; className?: string }) {
  const s = { sm: { mark: 'h-8', word: 'text-[17px]', gap: 'gap-2' }, md: { mark: 'h-11', word: 'text-[22px]', gap: 'gap-2.5' }, lg: { mark: 'h-14', word: 'text-[28px]', gap: 'gap-3' } }[size];
  return (
    <span className={cn('inline-flex items-center', s.gap, className)} aria-label="Kabuor Dine">
      <DineMark className={s.mark} dark={dark} />
      <span className="leading-none">
        <DineWord className={cn('font-brand block font-extrabold tracking-[-0.04em]', s.word, dark ? 'text-cream' : 'text-espresso')} dine={dark ? 'text-[#D6B394]' : 'text-dine'} />
        {tagline && <span className={cn('font-tagline mt-1 block text-sm italic', dark ? 'text-[#E8D3C0]' : 'text-[#7A5038]')}>Karibu mezani.</span>}
      </span>
    </span>
  );
}
