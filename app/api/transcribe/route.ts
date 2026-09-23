import { spawn } from "node:child_process";
import { createReadStream } from "node:fs";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import ffmpegPath from "ffmpeg-static";
import Groq from "groq-sdk";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";
export const maxDuration = 300;

const VIDEO_EXTENSIONS = new Set([".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".mpeg", ".mpg", ".wmv"]);
const MAX_FILES = positiveInt(process.env.MAX_BATCH_FILES, 10, 1, 50);
const MAX_FILE_BYTES = positiveInt(process.env.MAX_FILE_SIZE_MB, 500, 1, 5_000) * 1024 * 1024;
const CONCURRENCY = positiveInt(process.env.TRANSCRIPTION_CONCURRENCY, 2, 1, 5);
const MAX_RETRIES = 3;

type Segment = { start: number; end: number; text: string };
type BatchFile = { id: string; file: File };
type StreamEvent =
  | { type: "status"; id: string; status: "extracting" | "transcribing" | "ready" | "error"; message?: string }
  | { type: "result"; id: string; filename: string; content: string }
  | { type: "complete"; succeeded: number; failed: number };

function positiveInt(value: string | undefined, fallback: number, min: number, max: number) {
  const parsed = Number.parseInt(value ?? "", 10);
  return Number.isFinite(parsed) ? Math.min(max, Math.max(min, parsed)) : fallback;
}

function subtitleName(videoName: string) {
  const base = path.parse(path.basename(videoName)).name.trim();
  return `${base || "subtitles"}.srt`;
}

function srtTimestamp(seconds: number) {
  const milliseconds = Math.max(0, Math.round(seconds * 1000));
  const hours = Math.floor(milliseconds / 3_600_000);
  const minutes = Math.floor((milliseconds % 3_600_000) / 60_000);
  const secs = Math.floor((milliseconds % 60_000) / 1000);
  const millis = milliseconds % 1000;
  return `${String(hours).padStart(2, "0")}:${String(minutes).padStart(2, "0")}:${String(secs).padStart(2, "0")},${String(millis).padStart(3, "0")}`;
}

function toSrt(segments: Segment[]) {
  const valid = segments.filter((segment) => Number.isFinite(segment.start) && Number.isFinite(segment.end) && segment.text?.trim());
  if (!valid.length) throw new Error("The transcription returned no timed speech segments.");
  return `${valid.map((segment, index) => `${index + 1}\n${srtTimestamp(segment.start)} --> ${srtTimestamp(Math.max(segment.end, segment.start + 0.001))}\n${segment.text.trim().replace(/\s+/g, " ")}`).join("\n\n")}\n`;
}

function getFfmpegBinary(): string | undefined {
  if (process.env.FFMPEG_PATH && process.env.FFMPEG_PATH.trim().length > 0) {
    return process.env.FFMPEG_PATH;
  }
  if (typeof ffmpegPath === "string" && ffmpegPath.trim().length > 0) {
    return ffmpegPath;
  }
  return "ffmpeg";
}

function runFfmpeg(input: string, output: string) {
  const binary = getFfmpegBinary();
  if (!binary) throw new Error("The FFmpeg binary is unavailable on this platform.");
  return new Promise<void>((resolve, reject) => {
    const child = spawn(/*turbopackIgnore: true*/ binary, [
      "-hide_banner", "-loglevel", "error", "-y", "-i", input,
      "-vn", "-ac", "1", "-ar", "16000",
      "-c:a", "libmp3lame", "-b:a", "48k", output,
    ], { windowsHide: true });
    let stderr = "";
    child.stderr.on("data", (chunk: Buffer) => { stderr = `${stderr}${chunk}`.slice(-4_000); });
    child.once("error", reject);
    child.once("close", (code: number | null) => {
      if (code === 0) return resolve();
      const err = stderr.trim();
      if (
        err.includes("does not contain any stream") ||
        err.includes("matches no streams") ||
        err.includes("no audio stream")
      ) {
        return reject(new Error("الفيديو لا يحتوي على أي مسار صوتي (Audio track) لتفريغه."));
      }
      return reject(new Error(err || `FFmpeg exited with code ${code}.`));
    });
  });
}

function errorStatus(error: unknown) {
  if (typeof error === "object" && error && "status" in error && typeof error.status === "number") return error.status;
  return undefined;
}

function retryAfterMs(error: unknown, attempt: number) {
  if (typeof error === "object" && error && "headers" in error) {
    const headers = error.headers;
    if (headers instanceof Headers) {
      const seconds = Number(headers.get("retry-after"));
      if (Number.isFinite(seconds) && seconds >= 0) return seconds * 1000;
    }
  }
  return Math.min(15_000, 1_000 * 2 ** attempt) + Math.floor(Math.random() * 300);
}

async function transcribeWithRetry(groq: Groq, audioPath: string, onRetry: (message: string) => void) {
  for (let attempt = 0; ; attempt += 1) {
    try {
      const response = await groq.audio.transcriptions.create({
        file: createReadStream(audioPath),
        model: process.env.GROQ_WHISPER_MODEL || "whisper-large-v3-turbo",
        response_format: "verbose_json",
        timestamp_granularities: ["segment"],
        temperature: 0,
      });
      return response as typeof response & { segments?: Segment[] };
    } catch (error) {
      const status = errorStatus(error);
      const retryable = status === 429 || (status !== undefined && status >= 500);
      if (!retryable || attempt >= MAX_RETRIES) throw error;
      const waitMs = retryAfterMs(error, attempt);
      onRetry(`Rate limited or temporarily unavailable. Retrying in ${Math.ceil(waitMs / 1000)}s (${attempt + 1}/${MAX_RETRIES})…`);
      await new Promise((resolve) => setTimeout(resolve, waitMs));
    }
  }
}

function readableError(error: unknown) {
  if (typeof error === "object" && error && "error" in error) {
    const nested = error.error;
    if (typeof nested === "object" && nested && "message" in nested && typeof nested.message === "string") return nested.message;
  }
  return error instanceof Error ? error.message : "Unexpected processing failure.";
}

async function mapConcurrent<T>(values: T[], limit: number, worker: (value: T) => Promise<void>) {
  let cursor = 0;
  async function run() {
    while (cursor < values.length) {
      const index = cursor++;
      await worker(values[index]);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, values.length) }, run));
}

export async function POST(request: Request) {
  if (!process.env.GROQ_API_KEY) return Response.json({ error: "GROQ_API_KEY is not configured on the server." }, { status: 500 });
  if (!getFfmpegBinary()) return Response.json({ error: "FFmpeg is unavailable on this server." }, { status: 500 });

  let form: FormData;
  try {
    form = await request.formData();
  } catch {
    return Response.json({ error: "Could not read the multipart upload." }, { status: 400 });
  }
  const files = form.getAll("files").filter((entry): entry is File => entry instanceof File);
  const ids = form.getAll("ids").map(String);
  if (!files.length) return Response.json({ error: "Add at least one video file." }, { status: 400 });
  if (files.length > MAX_FILES) return Response.json({ error: `A batch can contain at most ${MAX_FILES} files.` }, { status: 413 });
  if (files.some((file) => file.size > MAX_FILE_BYTES)) return Response.json({ error: `Each video must be no larger than ${Math.round(MAX_FILE_BYTES / 1024 / 1024)} MB.` }, { status: 413 });
  if (files.some((file) => !file.type.startsWith("video/") && !VIDEO_EXTENSIONS.has(path.extname(file.name).toLowerCase()))) {
    return Response.json({ error: "The batch contains an unsupported file type." }, { status: 415 });
  }
  const outputNames = files.map((file) => subtitleName(file.name).toLocaleLowerCase());
  if (new Set(outputNames).size !== outputNames.length) {
    return Response.json({ error: "Two videos would produce the same subtitle filename. Rename one before uploading." }, { status: 400 });
  }

  const batch: BatchFile[] = files.map((file, index) => ({ id: ids[index] || crypto.randomUUID(), file }));
  const encoder = new TextEncoder();
  const groq = new Groq({ apiKey: process.env.GROQ_API_KEY, maxRetries: 0 });
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      let succeeded = 0;
      let failed = 0;
      const send = (event: StreamEvent) => controller.enqueue(encoder.encode(`${JSON.stringify(event)}\n`));

      void mapConcurrent(batch, CONCURRENCY, async ({ id, file }) => {
        const workDir = await mkdtemp(path.join(tmpdir(), "autosrt-"));
        const inputPath = path.join(workDir, `input${path.extname(file.name).toLowerCase().slice(0, 12) || ".video"}`);
        const audioPath = path.join(workDir, "audio.mp3");
        try {
          send({ type: "status", id, status: "extracting" });
          await writeFile(inputPath, Buffer.from(await file.arrayBuffer()));
          await runFfmpeg(inputPath, audioPath);
          send({ type: "status", id, status: "transcribing" });
          const transcript = await transcribeWithRetry(groq, audioPath, (message) => send({ type: "status", id, status: "transcribing", message }));
          const srt = toSrt(transcript.segments ?? []);
          send({ type: "result", id, filename: subtitleName(file.name), content: srt });
          send({ type: "status", id, status: "ready" });
          succeeded += 1;
        } catch (error) {
          failed += 1;
          send({ type: "status", id, status: "error", message: readableError(error) });
        } finally {
          await rm(workDir, { recursive: true, force: true }).catch(() => undefined);
        }
      }).then(() => {
        send({ type: "complete", succeeded, failed });
        controller.close();
      }).catch((error) => controller.error(error));
    },
  });

  return new Response(stream, {
    headers: {
      "Content-Type": "application/x-ndjson; charset=utf-8",
      "Cache-Control": "no-store, no-transform",
      "X-Content-Type-Options": "nosniff",
    },
  });
}
