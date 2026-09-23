import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  metadataBase: new URL("https://autosrt.anas.lol"),
  title: "AutoSRT — Batch Video & Audio Transcription to Subtitles",
  description: "High-performance batch video and audio to SRT transcription powered by Groq Whisper and FFmpeg.",
  openGraph: {
    title: "AutoSRT — Batch Video & Audio to Subtitles",
    description: "High-performance batch video and audio to SRT transcription powered by Groq Whisper and FFmpeg.",
    url: "https://autosrt.anas.lol",
    siteName: "AutoSRT",
    locale: "en_US",
    type: "website",
  },
  twitter: {
    card: "summary_large_image",
    title: "AutoSRT — Batch Video & Audio to Subtitles",
    description: "High-performance batch video and audio to SRT transcription powered by Groq Whisper and FFmpeg.",
  },
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
