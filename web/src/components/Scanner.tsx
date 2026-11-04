// Scanning with the phone's camera: barcodes on products and QR labels on assets. Uses the browser's own barcode
// reader (Chrome and Edge on Android and desktop). A USB or Bluetooth scanner needs none of this: it types the code
// into any search box and presses Enter.
import { useEffect, useRef, useState } from 'react';
import { Camera, X } from 'lucide-react';

type Detected = { rawValue: string };
type Detector = { detect: (source: HTMLVideoElement) => Promise<Detected[]> };
declare global {
  interface Window { BarcodeDetector?: new (o?: { formats: string[] }) => Detector }
}

const FORMATS = ['qr_code', 'ean_13', 'ean_8', 'upc_a', 'upc_e', 'code_128', 'code_39', 'itf'];
export const cameraScanWorks = () => typeof window !== 'undefined' && 'BarcodeDetector' in window && !!navigator.mediaDevices?.getUserMedia;

function ScanView({ onCode, onClose, title }: { onCode: (code: string) => void; onClose: () => void; title: string }) {
  const video = useRef<HTMLVideoElement>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let stream: MediaStream | null = null;
    let timer = 0;
    let stopped = false;
    (async () => {
      try {
        stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' }, audio: false });
        if (stopped || !video.current) return;
        video.current.srcObject = stream;
        await video.current.play();
        const detector = new window.BarcodeDetector!({ formats: FORMATS });
        const tick = async () => {
          if (stopped || !video.current) return;
          try {
            const [hit] = await detector.detect(video.current);
            if (hit?.rawValue) {
              navigator.vibrate?.(60);
              onCode(hit.rawValue.trim());
              return;
            }
          } catch { /* a frame that couldn't be read */ }
          timer = window.setTimeout(tick, 180);
        };
        tick();
      } catch {
        setError('The camera couldn’t start. Allow camera access for this site, or type the code instead.');
      }
    })();
    return () => {
      stopped = true;
      window.clearTimeout(timer);
      stream?.getTracks().forEach((t) => t.stop());
    };
  }, [onCode]);

  return (
    <div className="fixed inset-0 z-50 flex flex-col bg-ink/95 p-4 text-white" role="dialog" aria-label={title}>
      <div className="flex items-center justify-between">
        <p className="font-semibold">{title}</p>
        <button className="grid h-10 w-10 place-items-center rounded-full bg-white/10" onClick={onClose} aria-label="Close the scanner"><X className="h-5 w-5" /></button>
      </div>
      <div className="relative mx-auto mt-6 w-full max-w-md overflow-hidden rounded-lg bg-black">
        <video ref={video} className="aspect-[3/4] w-full object-cover" muted playsInline />
        <div className="pointer-events-none absolute inset-x-10 top-1/2 h-40 -translate-y-1/2 rounded-lg ring-2 ring-white/80" />
      </div>
      <p className="mt-4 text-center text-sm text-white/80">{error || 'Point the camera at the barcode or QR label.'}</p>
    </div>
  );
}

/** A camera button for a search or code field. Hidden where the browser can't read barcodes. */
export function ScanButton({ onCode, title = 'Scan', className }: { onCode: (code: string) => void; title?: string; className?: string }) {
  const [open, setOpen] = useState(false);
  if (!cameraScanWorks()) return null;
  return (
    <>
      <button type="button" className={className ?? 'btn btn-ghost px-3'} onClick={() => setOpen(true)} aria-label={title} title={title}>
        <Camera className="h-4 w-4" />
      </button>
      {open && <ScanView title={title} onClose={() => setOpen(false)} onCode={(c) => { setOpen(false); onCode(c); }} />}
    </>
  );
}
