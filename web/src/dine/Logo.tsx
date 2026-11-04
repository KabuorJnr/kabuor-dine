// The Kabuor Dine logo, in the Kabuor family style (like Kabuor Technologies, Kabuor Builds and Kabuor ERP):
// KABUOR DINE in capitals, the family's own KABUOR with DINE in the family's product-name face, with a clean 3D
// finish. The mark is Dine's O. brand/logo.svg and brand/mark.svg are the masters; brand/logo.html makes the PNGs.
import { cn } from '../lib/format';

/** The logo, KABUOR DINE. Size it by height: it is about 8.7 times as wide as it is tall. */
export function DineLockup({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-logo-on-dark.svg' : '/brand/kabuor-dine-logo.svg'} alt="Kabuor Dine" className={cn('w-auto', className)} />;
}

/** The mark: Dine's O in 3D, for icons and small spaces. */
export function DineMark({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <img src={dark ? '/brand/kabuor-dine-mark-on-dark.svg' : '/brand/kabuor-dine-mark.svg'} alt="" aria-hidden className={cn('w-auto', className)} />;
}

/** The logo at the size of the text around it, for headers and footers. */
export function DineWord({ className, dark = false }: { className?: string; dark?: boolean }) {
  return <DineLockup dark={dark} className={cn('inline-block h-[1em] shrink-0 align-[-0.12em]', className)} />;
}

/** The logo with "Karibu mezani." under it. size: sm for headers, md for sign-in, lg for the landing page. */
export function DineLogo({ size = 'sm', dark = false, tagline = false, className }: { size?: 'sm' | 'md' | 'lg'; dark?: boolean; tagline?: boolean; className?: string }) {
  const h = { sm: 'h-4', md: 'h-6', lg: 'h-8' }[size];
  return (
    <span className={cn('inline-flex flex-col items-start gap-1.5', className)}>
      <DineLockup className={h} dark={dark} />
      {tagline && <span className={cn('font-tagline text-sm italic', dark ? 'text-[#E8D3C0]' : 'text-[#7A5038]')}>Karibu mezani.</span>}
    </span>
  );
}
