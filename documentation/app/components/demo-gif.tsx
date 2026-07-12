import { useEffect, useState } from 'react';

function resolve(src: string): string {
  return `${import.meta.env.BASE_URL}${src.replace(/^\//, '')}`;
}

interface DemoGifProps {
  /** Path under /public, e.g. "images/app/demo__redlining.gif". */
  src: string;
  alt: string;
}

/**
 * An animated demo GIF with a fullscreen lightbox: click the image (or the
 * expand button in its corner) to view it large over a dark backdrop; click
 * anywhere or press Escape to close.
 */
export function DemoGif({ src, alt }: DemoGifProps) {
  const [full, setFull] = useState(false);
  const url = resolve(src);

  useEffect(() => {
    if (!full) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setFull(false);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [full]);

  return (
    <>
      <span className="group relative my-4 block">
        <img
          src={url}
          alt={alt}
          className="w-full cursor-zoom-in rounded-lg border border-fd-border"
          onClick={() => setFull(true)}
        />
        <button
          type="button"
          aria-label="View fullscreen"
          onClick={() => setFull(true)}
          className="absolute right-2 top-2 rounded-md bg-black/60 p-1.5 text-white opacity-0 transition-opacity group-hover:opacity-100"
        >
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
            <path d="M8 3H5a2 2 0 0 0-2 2v3" />
            <path d="M21 8V5a2 2 0 0 0-2-2h-3" />
            <path d="M3 16v3a2 2 0 0 0 2 2h3" />
            <path d="M16 21h3a2 2 0 0 0 2-2v-3" />
          </svg>
        </button>
      </span>
      {full && (
        <span
          role="dialog"
          aria-label={alt}
          className="fixed inset-0 z-50 flex cursor-zoom-out items-center justify-center bg-black/90 p-4 sm:p-10"
          onClick={() => setFull(false)}
        >
          <img src={url} alt={alt} className="h-full w-full rounded-lg object-contain" />
        </span>
      )}
    </>
  );
}
