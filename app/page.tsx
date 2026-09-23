import Image from "next/image";
import { BatchUploader } from "@/components/batch-uploader";

export default function Home() {
  return (
    <main className="relative min-h-screen overflow-hidden px-5 py-8 sm:px-8 sm:py-12">
      <div className="glow glow-one" />
      <div className="glow glow-two" />
      <div className="relative mx-auto max-w-5xl">
        {/* Top Navbar */}
        <nav className="mb-12 flex items-center justify-between">
          <div className="flex items-center gap-3">
            <Image
              src="/icon.svg"
              alt="AutoSRT Logo"
              width={38}
              height={38}
              className="rounded-xl shadow-[0_0_20px_rgba(52,211,153,0.2)]"
              priority
            />
            <span className="text-xl font-extrabold tracking-tight text-slate-100">
              Auto<span className="bg-gradient-to-r from-emerald-400 to-cyan-400 bg-clip-text text-transparent">SRT</span>
            </span>
          </div>
          <div className="flex items-center gap-2">
            <span className="inline-flex items-center gap-1.5 rounded-full border border-emerald-500/20 bg-emerald-500/10 px-3 py-1 text-xs font-semibold text-emerald-400">
              <span className="size-1.5 rounded-full bg-emerald-400 animate-pulse" />
              Whisper v3 Turbo
            </span>
          </div>
        </nav>

        <header className="mb-10 max-w-3xl">
          <div className="eyebrow"><span /> Batch transcription workspace</div>
          <h1>Video & audio in. Subtitles out.</h1>
          <p className="mt-5 max-w-2xl text-base leading-7 text-slate-400 sm:text-lg">
            Drop a full batch of videos or audio recordings and AutoSRT will transcribe and package every subtitle with the original filename intact.
          </p>
        </header>

        <BatchUploader />

        <footer className="mt-6 flex flex-wrap items-center justify-between gap-3 text-xs text-slate-600">
          <span>Powered by Groq Whisper · Audio processed temporarily</span>
          <span>Original filenames preserved</span>
        </footer>
      </div>
    </main>
  );
}
