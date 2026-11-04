// The Kabuor Dine logo: the cloche mark (a serving dome with a K, steam rising) and the stacked wordmark, in the
// same family as Kabuor Builds and Kabuor ERP (brand/logo.html holds the master artwork).
import { cn } from '../lib/format';

/** The full lockup as drawn in brand/logo.html: the cloche, "Kabuor / Dine" stacked, and "Karibu mezani." */
export function DineLockup({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-logo-on-dark.png' : '/brand/kabuor-dine-logo.png'} alt="Kabuor Dine. Karibu mezani." className={cn('w-auto', className)} />;
}

export function DineMark({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-mark-on-dark.svg' : '/brand/kabuor-dine-mark.svg'} alt="" aria-hidden className={cn('w-auto', className)} />;
}

/** The a of "Kabuor": a covered plate, the dome as tall as the lowercase letters and its knob as tall as the K. */
export function ClocheA({ className }: { className?: string }) {
  return (
    <svg viewBox="0 0 66 70" aria-hidden className={cn('inline-block h-[0.7em] w-[0.66em] fill-current align-[-0.02em]', className)} style={{ margin: '0 0.025em 0 0.075em' }}>
      <rect x="0" y="57" width="66" height="11" rx="5.5" />
      <path d="M2 54 A31 34 0 0 1 64 54 Z" />
      <rect x="28.5" y="12" width="9" height="10" rx="2" />
      <circle cx="33" cy="9.5" r="7" />
    </svg>
  );
}

/** "Kabuor Dine" in words, with the covered-plate a. `dine` colours the second word. */
export function DineWord({ dine, className }: { dine?: string; className?: string }) {
  return (
    <span className={className} aria-label="Kabuor Dine">
      <span aria-hidden>K<ClocheA />buor <span className={dine}>Dine</span></span>
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
