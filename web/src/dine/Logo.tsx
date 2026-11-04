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

/** Mark and wordmark. size: sm for headers, md for sign-in, lg for the landing page. */
export function DineLogo({ size = 'sm', dark = false, tagline = false, className }: { size?: 'sm' | 'md' | 'lg'; dark?: boolean; tagline?: boolean; className?: string }) {
  const s = { sm: { mark: 'h-8', word: 'text-[17px]', gap: 'gap-2' }, md: { mark: 'h-11', word: 'text-[22px]', gap: 'gap-2.5' }, lg: { mark: 'h-14', word: 'text-[28px]', gap: 'gap-3' } }[size];
  return (
    <span className={cn('inline-flex items-center', s.gap, className)} aria-label="Kabuor Dine">
      <DineMark className={s.mark} dark={dark} />
      <span className="leading-none">
        <span className={cn('font-brand block font-extrabold tracking-[-0.04em]', s.word, dark ? 'text-cream' : 'text-espresso')}>Kabuor <span className={dark ? 'text-[#D6B394]' : 'text-dine'}>Dine</span></span>
        {tagline && <span className={cn('font-tagline mt-1 block text-sm italic', dark ? 'text-[#E8D3C0]' : 'text-[#7A5038]')}>Karibu mezani.</span>}
      </span>
    </span>
  );
}
