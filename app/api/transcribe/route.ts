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

const MEDIA_EXTENSIONS = new Set([
  // Video
  ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".mpeg", ".mpg", ".wmv", ".flv", ".3gp",
  // Audio
  ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac", ".wma", ".opus", ".weba",
]);
const MAX_FILES = positiveInt(process.env.MAX_BATCH_FILES, 10, 1, 50);
const MAX_FILE_BYTES = positiveInt(process.env.MAX_FILE_SIZE_MB, 500, 1, 5_000) * 1024 * 1024;
const CONCURRENCY = positiveInt(process.env.TRANSCRIPTION_CONCURRENCY, 2, 1, 5);
const MAX_RETRIES = 3;

// Rate limiting configuration
type RateLimitRecord = { count: number; resetTime: number };
const rateLimitMap = new Map<string, RateLimitRecord>();
const RATE_LIMIT_WINDOW_MS = 60 * 1000; // 1 minute
const MAX_REQUESTS_PER_WINDOW = positiveInt(process.env.RATE_LIMIT_PER_MINUTE, 30, 5, 120);

function isRateLimited(ip: string): boolean {
  const now = Date.now();
  const record = rateLimitMap.get(ip);
  if (!record || now > record.resetTime) {
    rateLimitMap.set(ip, { count: 1, resetTime: now + RATE_LIMIT_WINDOW_MS });
    return false;
  }
  record.count += 1;
  return record.count > MAX_REQUESTS_PER_WINDOW;
}

// Clean up stale rate limit entries every 5 minutes
if (typeof setInterval !== "undefined") {
  setInterval(() => {
    const now = Date.now();
    for (const [ip, record] of rateLimitMap.entries()) {
      if (now > record.resetTime) rateLimitMap.delete(ip);
    }
  }, 5 * 60 * 1000).unref?.();
}

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

function sanitizeFilename(name: string): string {
  const base = path.basename(name).replace(/[\0\x00-\x1f\x7f\\/:]/g, "");
  const parsed = path.parse(base).name.trim();
  const sanitized = parsed.replace(/[^\p{L}\p{N}\s._-]/gu, "").replace(/\s+/g, " ").trim().slice(0, 100);
  return sanitized || "media";
}

function subtitleName(mediaName: string): string {
  return `${sanitizeFilename(mediaName)}.srt`;
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

function isValidMediaHeader(buffer: Buffer): boolean {
  if (buffer.length < 12) return false;

  // 1. MP4 / M4A / MOV (contains "ftyp", "moov", "mdat", "free" within first 32 bytes)
  const asciiHeader = buffer.subarray(0, 32).toString("ascii");
  if (
    asciiHeader.includes("ftyp") ||
    asciiHeader.includes("moov") ||
    asciiHeader.includes("mdat") ||
    asciiHeader.includes("free")
  ) return true;

  // 2. Matroska / WebM (0x1A 0x45 0xDF 0xA3)
  if (buffer[0] === 0x1A && buffer[1] === 0x45 && buffer[2] === 0xDF && buffer[3] === 0xA3) return true;

  // 3. RIFF (WAV, AVI)
  if (buffer[0] === 0x52 && buffer[1] === 0x49 && buffer[2] === 0x46 && buffer[3] === 0x46) {
    const riffType = buffer.subarray(8, 12).toString("ascii");
    if (riffType === "WAVE" || riffType === "AVI ") return true;
  }

  // 4. MP3 with ID3v2 tag ('ID3')
  if (buffer[0] === 0x49 && buffer[1] === 0x44 && buffer[2] === 0x33) return true;

  // 5. Raw MP3 sync frame (0xFF 0xFB, 0xFF 0xF3, etc.)
  if (buffer[0] === 0xFF && (buffer[1] & 0xE0) === 0xE0) return true;

  // 6. Ogg / Opus / Vorbis ('OggS')
  if (buffer[0] === 0x4F && buffer[1] === 0x67 && buffer[2] === 0x67 && buffer[3] === 0x53) return true;

  // 7. FLAC ('fLaC')
  if (buffer[0] === 0x66 && buffer[1] === 0x4C && buffer[2] === 0x61 && buffer[3] === 0x43) return true;

  // 8. ASF / WMV / WMA (0x30 0x26 0xB2 0x75)
  if (buffer[0] === 0x30 && buffer[1] === 0x26 && buffer[2] === 0xB2 && buffer[3] === 0x75) return true;

  // 9. FLV ('FLV')
  if (buffer[0] === 0x46 && buffer[1] === 0x4C && buffer[2] === 0x56) return true;

  // 10. AAC ADTS (0xFF 0xF1 or 0xFF 0xF9)
  if (buffer[0] === 0xFF && (buffer[1] === 0xF1 || buffer[1] === 0xF9)) return true;

  return false;
}

function runFfmpeg(input: string, output: string, signal?: AbortSignal) {
  const binary = getFfmpegBinary();
  if (!binary) throw new Error("The FFmpeg binary is unavailable on this platform.");
  if (signal?.aborted) return Promise.reject(new Error("Operation cancelled by user."));

  return new Promise<void>((resolve, reject) => {
    // Security flags:
    // -nostdin: prevents reading terminal input
    // -protocol_whitelist "file,crypto,data": strictly prevents SSRF and network access inside media
    const child = spawn(/*turbopackIgnore: true*/ binary, [
      "-nostdin",
      "-protocol_whitelist", "file,crypto,data",
      "-hide_banner",
      "-loglevel", "error",
      "-y",
      "-i", input,
      "-vn",
      "-ac", "1",
      "-ar", "16000",
      "-c:a", "libmp3lame",
      "-b:a", "48k",
      output,
    ], {
      windowsHide: true,
      timeout: 240_000, // 4-minute hard limit to prevent endless hanging
    });

    if (signal) {
      const onAbort = () => {
        child.kill("SIGTERM");
        reject(new Error("Operation cancelled by user."));
      };
      signal.addEventListener("abort", onAbort, { once: true });
      child.once("close", () => signal.removeEventListener("abort", onAbort));
    }

    let stderr = "";
    child.stderr.on("data", (chunk: Buffer) => { stderr = `${stderr}${chunk}`.slice(-4_000); });
    child.once("error", reject);
    child.once("close", (code: number | null) => {
      if (signal?.aborted) return reject(new Error("Operation cancelled by user."));
      if (code === 0) return resolve();
      const err = stderr.trim();
      if (
        err.includes("does not contain any stream") ||
        err.includes("matches no streams") ||
        err.includes("no audio stream")
      ) {
        return reject(new Error("الملف لا يحتوي على أي مسار صوتي (Audio track) لتفريغه."));
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

function readableError(error: unknown): string {
  let message = "Unexpected processing failure.";
  if (typeof error === "object" && error && "error" in error) {
    const nested = error.error;
    if (typeof nested === "object" && nested && "message" in nested && typeof nested.message === "string") {
      message = nested.message;
    }
  } else if (error instanceof Error) {
    message = error.message;
  }

  // Mask sensitive server internals and paths
  return message
    .replace(/[A-Za-z]:\\[^\s]+/g, "[internal path]")
    .replace(/\/(?:[a-zA-Z0-9._-]+\/)+[a-zA-Z0-9._-]+/g, "[internal path]")
    .replace(/gsk_[a-zA-Z0-9_-]+/g, "[redacted]");
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

function isOriginAllowed(request: Request): boolean {
  const secFetchSite = request.headers.get("sec-fetch-site");
  if (secFetchSite === "cross-site") {
    return false;
  }

  const origin = request.headers.get("origin");
  const host = request.headers.get("host") || request.headers.get("x-forwarded-host");

  if (!origin || !host) return true;

  try {
    const originUrl = new URL(origin);
    const hostWithoutPort = host.split(":")[0];
    const originWithoutPort = originUrl.hostname;
    return (
      originWithoutPort === hostWithoutPort ||
      originWithoutPort === "autosrt.anas.lol" ||
      originWithoutPort.endsWith(".anas.lol") ||
      originWithoutPort === "localhost" ||
      originWithoutPort === "127.0.0.1"
    );
  } catch {
    return false;
  }
}

export async function POST(request: Request) {
  // 1. CSRF and Cross-Site Leeching protection
  if (!isOriginAllowed(request)) {
    return Response.json({ error: "Cross-site request blocked." }, { status: 403 });
  }

  // 2. Rate Limiting Protection (DoS / API draining prevention)
  const clientIp =
    request.headers.get("x-forwarded-for")?.split(",")[0]?.trim() ||
    request.headers.get("x-real-ip") ||
    "127.0.0.1";

  if (isRateLimited(clientIp)) {
    return Response.json(
      { error: "Too many requests. Please wait a moment before processing more files." },
      {
        status: 429,
        headers: { "Retry-After": "60" },
      }
    );
  }

  // 3. Server configuration validation
  if (!process.env.GROQ_API_KEY) {
    return Response.json({ error: "GROQ_API_KEY is not configured on the server." }, { status: 500 });
  }
  if (!getFfmpegBinary()) {
    return Response.json({ error: "FFmpeg is unavailable on this server." }, { status: 500 });
  }

  // 4. Multipart parsing with error handling
  let form: FormData;
  try {
    form = await request.formData();
  } catch {
    return Response.json({ error: "Could not read the multipart upload." }, { status: 400 });
  }

  const files = form.getAll("files").filter((entry): entry is File => entry instanceof File);
  const ids = form.getAll("ids").map(String);

  if (!files.length) return Response.json({ error: "Add at least one video or audio file." }, { status: 400 });
  if (files.length > MAX_FILES) return Response.json({ error: `A batch can contain at most ${MAX_FILES} files.` }, { status: 413 });
  if (files.some((file) => file.size > MAX_FILE_BYTES)) {
    return Response.json({ error: `Each file must be no larger than ${Math.round(MAX_FILE_BYTES / 1024 / 1024)} MB.` }, { status: 413 });
  }

  // Extension validation
  if (files.some((file) => {
    const ext = path.extname(file.name).toLowerCase();
    return !MEDIA_EXTENSIONS.has(ext);
  })) {
    return Response.json({ error: "The batch contains an unsupported file type. Please upload valid video or audio files." }, { status: 415 });
  }

  const outputNames = files.map((file) => subtitleName(file.name).toLocaleLowerCase());
  if (new Set(outputNames).size !== outputNames.length) {
    return Response.json({ error: "Two files would produce the same subtitle filename. Rename one before uploading." }, { status: 400 });
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
        const ext = path.extname(file.name).toLowerCase().slice(0, 12) || (file.type.startsWith("audio/") ? ".audio" : ".video");
        const inputPath = path.join(workDir, `input${ext}`);
        const audioPath = path.join(workDir, "audio.mp3");

        try {
          if (request.signal.aborted) return;
          send({ type: "status", id, status: "extracting" });

          const buffer = Buffer.from(await file.arrayBuffer());

          // Magic Bytes verification: verify it's an actual media container, not a malicious disguised payload
          if (!isValidMediaHeader(buffer)) {
            throw new Error("Invalid file content: The file does not appear to be a valid audio or video container.");
          }

          await writeFile(inputPath, buffer);

          if (request.signal.aborted) return;
          await runFfmpeg(inputPath, audioPath, request.signal);

          if (request.signal.aborted) return;
          send({ type: "status", id, status: "transcribing" });
          const transcript = await transcribeWithRetry(groq, audioPath, (message) => send({ type: "status", id, status: "transcribing", message }));

          if (request.signal.aborted) return;
          const srt = toSrt(transcript.segments ?? []);
          send({ type: "result", id, filename: subtitleName(file.name), content: srt });
          send({ type: "status", id, status: "ready" });
          succeeded += 1;
        } catch (error) {
          if (request.signal.aborted) return;
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
