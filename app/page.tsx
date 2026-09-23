import { BatchUploader } from "@/components/batch-uploader";

export default function Home() {
  return (
    <main className="relative min-h-screen overflow-hidden px-5 py-10 sm:px-8 sm:py-16">
      <div className="glow glow-one" />
      <div className="glow glow-two" />
      <div className="relative mx-auto max-w-5xl">
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
