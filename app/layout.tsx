import type { Metadata } from "next";
import "./globals.css";

export const metadata: Metadata = {
  title: "AutoSRT — Batch video transcription",
  description: "Turn a batch of videos into accurately named SRT subtitle files.",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
