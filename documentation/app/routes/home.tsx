import type { Route } from './+types/home';
import { HomeLayout } from 'fumadocs-ui/layouts/home';
import { Link } from 'react-router';
import { baseOptions } from '@/lib/layout.shared';
import { storeUrl, extrabbitUrl } from '@/lib/shared';

export function meta({}: Route.MetaArgs) {
  return [
    { title: 'ExtrabbitCode Inventor MetaReader' },
    {
      name: 'description',
      content:
        'Read metadata out of Autodesk Inventor .ipt / .iam / .idw / .ipn files straight from their bytes - no Inventor installation required.',
    },
  ];
}

export default function Home() {
  return (
    <HomeLayout {...baseOptions()}>
      <main className="flex flex-col items-center justify-center flex-1 px-6 py-20 text-center gap-6">
        <img
          src={`${import.meta.env.BASE_URL}images/branding/metareader.svg`}
          alt="MetaReader icon"
          className="w-24 h-24"
        />

        <div className="flex flex-col items-center gap-3 max-w-xl">
          <h1 className="text-3xl font-bold tracking-tight">
            ExtrabbitCode Inventor MetaReader
          </h1>
          <p className="text-fd-muted-foreground text-base leading-relaxed">
            Read iProperties, references, model states and the preview thumbnail
            out of Autodesk Inventor part, assembly, drawing and presentation
            files - directly from their bytes, with no Autodesk Inventor
            installed. A zero-dependency .NET library, a CLI, and a Windows
            desktop viewer over the same parser.
          </p>
        </div>

        <div className="flex flex-wrap items-center justify-center gap-3">
          <a
            className="bg-fd-primary text-fd-primary-foreground rounded-full font-medium px-6 py-2.5 text-sm hover:opacity-90 transition-opacity"
            href={storeUrl}
            target="_blank"
            rel="noreferrer"
          >
            Get it from the Microsoft Store
          </a>
          <Link
            className="border border-fd-border rounded-full font-medium px-6 py-2.5 text-sm hover:bg-fd-muted transition-colors"
            to="/docs"
          >
            Read the docs
          </Link>
        </div>

        <div className="mt-16 flex flex-col items-center gap-2">
          <span className="text-xs text-fd-muted-foreground uppercase tracking-widest">
            Made by
          </span>
          <a
            href={extrabbitUrl}
            target="_blank"
            rel="noreferrer"
            className="opacity-80 hover:opacity-100 transition-opacity"
          >
            <img
              src={`${import.meta.env.BASE_URL}images/branding/extrabbit.png`}
              alt="ExtrabbitCode logo"
              className="h-8"
            />
          </a>
        </div>
      </main>
    </HomeLayout>
  );
}
