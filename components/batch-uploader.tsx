"use client";

import JSZip from "jszip";
import { AlertCircle, Check, Download, FileAudio, FileVideo, LoaderCircle, RotateCcw, UploadCloud, X } from "lucide-react";
import { ChangeEvent, DragEvent, useRef, useState } from "react";

type Status = "queued" | "uploading" | "extracting" | "transcribing" | "ready" | "error";

type UploadItem = {
  id: string;
  file: File;
  status: Status;
  detail?: string;
  srtContent?: string;
  srtFilename?: string;
};

type ServerEvent = {
  type: "status" | "result" | "complete";
  id?: string;
  status?: Status;
  message?: string;
  filename?: string;
  content?: string;
  succeeded?: number;
  failed?: number;
};

const statusMeta: Record<Status, { label: string; progress: number }> = {
  queued: { label: "Queued", progress: 8 },
  uploading: { label: "Uploading", progress: 20 },
  extracting: { label: "Extracting audio", progress: 45 },
  transcribing: { label: "Transcribing", progress: 76 },
  ready: { label: "Ready", progress: 100 },
  error: { label: "Failed", progress: 100 },
};

function fileStem(name: string) {
  const dot = name.lastIndexOf(".");
  return dot > 0 ? name.slice(0, dot) : name;
}

function formatBytes(bytes: number) {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const power = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  return `${(bytes / 1024 ** power).toFixed(power ? 1 : 0)} ${units[power]}`;
}

function saveBlob(blob: Blob, filename: string) {
  const url = URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = filename;
  document.body.appendChild(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1_000);
}

function isAudioFile(file: File) {
  return file.type.startsWith("audio/") || /\.(mp3|wav|m4a|aac|ogg|flac|wma|opus|weba)$/i.test(file.name);
}

export function BatchUploader() {
  const [items, setItems] = useState<UploadItem[]>([]);
  const [dragging, setDragging] = useState(false);
  const [running, setRunning] = useState(false);
  const [notice, setNotice] = useState<string>();
  const inputRef = useRef<HTMLInputElement>(null);

  function addFiles(incoming: File[]) {
    const mediaFiles = incoming.filter((file) =>
      file.type.startsWith("video/") ||
      file.type.startsWith("audio/") ||
      /\.(mp4|mov|mkv|avi|webm|m4v|mpeg|mpg|wmv|flv|3gp|mp3|wav|m4a|aac|ogg|flac|wma|opus|weba)$/i.test(file.name)
    );
    setItems((current) => {
      const known = new Set(current.map(({ file }) => `${file.name}:${file.size}:${file.lastModified}`));
      return [...current, ...mediaFiles.filter((file) => !known.has(`${file.name}:${file.size}:${file.lastModified}`)).map((file) => ({
        id: crypto.randomUUID(),
        file,
        status: "queued" as const,
      }))];
    });
    if (mediaFiles.length !== incoming.length) setNotice("Some files were skipped because they do not appear to be video or audio files.");
    else setNotice(undefined);
  }

  function onInput(event: ChangeEvent<HTMLInputElement>) {
    addFiles(Array.from(event.target.files ?? []));
    event.target.value = "";
  }

  function onDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault();
    setDragging(false);
    if (!running) addFiles(Array.from(event.dataTransfer.files));
  }

  function patchItem(id: string, patch: Partial<UploadItem>) {
    setItems((current) => current.map((item) => item.id === id ? { ...item, ...patch } : item));
  }

  function retryItem(id: string) {
    if (running) return;
    patchItem(id, { status: "queued", detail: undefined });
    setNotice(undefined);
  }

  function downloadItem(item: UploadItem) {
    if (!item.srtContent) return;
    const filename = item.srtFilename || `${fileStem(item.file.name)}.srt`;
    saveBlob(new Blob([item.srtContent], { type: "application/x-subrip;charset=utf-8" }), filename);
  }

  async function downloadAllReady() {
    const readyItems = items.filter((item) => item.status === "ready" && item.srtContent);
    if (!readyItems.length) return;

    if (readyItems.length === 1) {
      downloadItem(readyItems[0]);
    } else {
      const zip = new JSZip();
      readyItems.forEach((item) => {
        const filename = item.srtFilename || `${fileStem(item.file.name)}.srt`;
        zip.file(filename, item.srtContent ?? "");
      });
      saveBlob(await zip.generateAsync({ type: "blob", compression: "DEFLATE" }), "subtitles.zip");
    }
  }

  async function start() {
    if (running) return;

    // Determine pending files that actually need transcription
    const targets = items.filter((item) => item.status === "queued" || item.status === "error");

    // If no pending files but ready items exist, allow redownloading all
    if (targets.length === 0) {
      const readyCount = items.filter((i) => i.status === "ready").length;
      if (readyCount > 0) {
        await downloadAllReady();
      }
      return;
    }

    // Check for duplicate stems among pending targets and against existing completed items
    const duplicateStems = new Set<string>();
    const seen = new Set<string>();
    for (const { file } of targets) {
      const stem = fileStem(file.name).toLocaleLowerCase();
      if (seen.has(stem)) duplicateStems.add(stem);
      seen.add(stem);
    }
    if (duplicateStems.size) {
      setNotice("Two selected files would produce the same .srt name. Rename or remove one of them first.");
      return;
    }

    setRunning(true);
    setNotice(undefined);

    // Only transition pending targets to "uploading"
    const targetIds = new Set(targets.map((t) => t.id));
    setItems((current) =>
      current.map((item) =>
        targetIds.has(item.id)
          ? { ...item, status: "uploading", detail: undefined, srtContent: undefined, srtFilename: undefined }
          : item
      )
    );

    const form = new FormData();
    targets.forEach(({ id, file }) => {
      form.append("ids", id);
      form.append("files", file, file.name);
    });

    const newResults: Array<{ id?: string; filename: string; content: string }> = [];

    try {
      const response = await fetch("/api/transcribe", { method: "POST", body: form });
      if (!response.ok) {
        const body = await response.json().catch(() => ({ error: "The server rejected the batch." }));
        throw new Error(body.error ?? "The server rejected the batch.");
      }
      if (!response.body) throw new Error("The server returned no response stream.");

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      while (true) {
        const { value, done } = await reader.read();
        buffer += decoder.decode(value, { stream: !done });
        const lines = buffer.split("\n");
        buffer = lines.pop() ?? "";
        for (const line of lines) {
          if (!line.trim()) continue;
          const event = JSON.parse(line) as ServerEvent;
          if (event.type === "status" && event.id && event.status) {
            patchItem(event.id, { status: event.status, detail: event.message });
          } else if (event.type === "result" && event.filename && event.content !== undefined) {
            newResults.push({ id: event.id, filename: event.filename, content: event.content });
            if (event.id) {
              patchItem(event.id, {
                status: "ready",
                srtContent: event.content,
                srtFilename: event.filename,
              });
            }
          } else if (event.type === "complete") {
            setNotice(
              event.failed
                ? `${event.succeeded} completed; ${event.failed} failed. New subtitles were downloaded.`
                : `${event.succeeded} subtitle file${event.succeeded === 1 ? "" : "s"} ready and downloaded.`
            );
          }
        }
        if (done) break;
      }

      // Auto-download only newly generated results from this specific run
      if (newResults.length === 1) {
        saveBlob(
          new Blob([newResults[0].content], { type: "application/x-subrip;charset=utf-8" }),
          newResults[0].filename
        );
      } else if (newResults.length > 1) {
        const zip = new JSZip();
        newResults.forEach(({ filename, content }) => zip.file(filename, content));
        saveBlob(await zip.generateAsync({ type: "blob", compression: "DEFLATE" }), "subtitles.zip");
      }
    } catch (error) {
      const message = error instanceof Error ? error.message : "Unexpected processing error.";
      setNotice(message);
      // Only mark targets that didn't finish as error
      setItems((current) =>
        current.map((item) =>
          targetIds.has(item.id) && item.status !== "ready"
            ? { ...item, status: "error", detail: message }
            : item
        )
      );
    } finally {
      setRunning(false);
    }
  }

  const completedCount = items.filter((item) => item.status === "ready").length;
  const pendingCount = items.filter((item) => item.status === "queued" || item.status === "error").length;

  return (
    <section className="panel overflow-hidden rounded-3xl">
      <div
        role="button"
        tabIndex={0}
        onClick={() => !running && inputRef.current?.click()}
        onKeyDown={(event) => {
          if ((event.key === "Enter" || event.key === " ") && !running) inputRef.current?.click();
        }}
        onDragOver={(event) => {
          event.preventDefault();
          if (!running) setDragging(true);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
        className={`drop-grid m-3 flex min-h-64 cursor-pointer flex-col items-center justify-center rounded-2xl border border-dashed px-6 py-12 text-center transition ${
          dragging
            ? "border-emerald-300 bg-emerald-400/8"
            : "border-slate-700/80 bg-slate-950/25 hover:border-slate-500 hover:bg-slate-900/35"
        } ${running ? "pointer-events-none opacity-60" : ""}`}
      >
        <div className="mb-5 grid size-14 place-items-center rounded-2xl border border-emerald-300/20 bg-emerald-300/10 text-emerald-300 shadow-[0_0_35px_rgba(52,211,153,.08)]">
          <UploadCloud size={25} strokeWidth={1.7} />
        </div>
        <p className="text-lg font-semibold tracking-tight text-slate-100">Drop your video or audio batch here</p>
        <p className="mt-2 text-sm text-slate-500">or click to browse · MP4, MP3, WAV, MOV, M4A, MKV, AAC, WebM and more</p>
        <input
          ref={inputRef}
          type="file"
          accept="video/*,audio/*,.mkv,.avi,.m4v,.mp4,.mov,.webm,.mp3,.wav,.m4a,.aac,.ogg,.flac,.opus"
          multiple
          onChange={onInput}
          className="hidden"
        />
      </div>

      {items.length > 0 && (
        <div className="border-t border-slate-800/80">
          <div className="flex items-center justify-between px-5 py-4 sm:px-7">
            <div>
              <p className="text-sm font-semibold text-slate-200">Processing queue</p>
              <p className="mt-0.5 text-xs text-slate-500">
                {items.length} file{items.length === 1 ? "" : "s"} · {completedCount} completed
                {pendingCount > 0 ? ` · ${pendingCount} pending` : ""}
              </p>
            </div>
            {!running && (
              <div className="flex items-center gap-3">
                {completedCount > 0 && (
                  <button
                    onClick={downloadAllReady}
                    className="text-xs font-medium text-emerald-400 transition hover:text-emerald-300"
                  >
                    Download all ready
                  </button>
                )}
                <button
                  onClick={() => {
                    setItems([]);
                    setNotice(undefined);
                  }}
                  className="text-xs font-medium text-slate-500 transition hover:text-slate-200"
                >
                  Clear all
                </button>
              </div>
            )}
          </div>

          <div className="max-h-[28rem] divide-y divide-slate-800/70 overflow-y-auto border-y border-slate-800/70">
            {items.map((item) => {
              const meta = statusMeta[item.status];
              return (
                <div key={item.id} className="group px-5 py-4 sm:px-7">
                  <div className="flex items-start gap-4">
                    <div className="mt-0.5 grid size-10 shrink-0 place-items-center rounded-xl bg-slate-800/70 text-slate-400">
                      {isAudioFile(item.file) ? <FileAudio size={18} /> : <FileVideo size={18} />}
                    </div>
                    <div className="min-w-0 flex-1">
                      <div className="flex items-start justify-between gap-4">
                        <div className="min-w-0">
                          <p className="truncate text-sm font-medium text-slate-200">{item.file.name}</p>
                          <p className="mt-1 text-xs text-slate-600">
                            {formatBytes(item.file.size)} → {fileStem(item.file.name)}.srt
                          </p>
                        </div>
                        <div className="flex shrink-0 items-center gap-2">
                          <span
                            className={`flex items-center gap-1.5 text-xs font-medium ${
                              item.status === "ready"
                                ? "text-emerald-300"
                                : item.status === "error"
                                ? "text-rose-300"
                                : "text-slate-400"
                            }`}
                          >
                            {item.status === "ready" ? (
                              <Check size={14} />
                            ) : item.status === "error" ? (
                              <AlertCircle size={14} />
                            ) : item.status !== "queued" ? (
                              <LoaderCircle className="spinner" size={14} />
                            ) : null}
                            {meta.label}
                          </span>

                          {/* Quick download button for individual ready file */}
                          {item.status === "ready" && item.srtContent && (
                            <button
                              aria-label={`Download ${fileStem(item.file.name)}.srt`}
                              title="Download subtitle"
                              onClick={() => downloadItem(item)}
                              className="rounded-md p-1.5 text-emerald-400 transition hover:bg-emerald-400/10 hover:text-emerald-300"
                            >
                              <Download size={14} />
                            </button>
                          )}

                          {/* Retry button for failed item */}
                          {item.status === "error" && !running && (
                            <button
                              aria-label={`Retry ${item.file.name}`}
                              title="Retry transcription"
                              onClick={() => retryItem(item.id)}
                              className="rounded-md p-1.5 text-amber-400 transition hover:bg-amber-400/10 hover:text-amber-300"
                            >
                              <RotateCcw size={14} />
                            </button>
                          )}

                          {/* Remove button */}
                          {!running && (
                            <button
                              aria-label={`Remove ${item.file.name}`}
                              title="Remove"
                              onClick={() => setItems((current) => current.filter(({ id }) => id !== item.id))}
                              className="rounded-md p-1 text-slate-600 opacity-0 transition hover:bg-slate-800 hover:text-slate-300 group-hover:opacity-100 focus:opacity-100"
                            >
                              <X size={14} />
                            </button>
                          )}
                        </div>
                      </div>
                      <div className="mt-3 h-1 overflow-hidden rounded-full bg-slate-800">
                        <div
                          className={`progress h-full rounded-full ${
                            item.status === "error" ? "bg-rose-400" : "bg-emerald-400"
                          }`}
                          style={{ width: `${meta.progress}%` }}
                        />
                      </div>
                      {item.detail && (
                        <p className={`mt-2 text-xs ${item.status === "error" ? "text-rose-300/80" : "text-slate-500"}`}>
                          {item.detail}
                        </p>
                      )}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>

          <div className="flex flex-col gap-3 px-5 py-5 sm:flex-row sm:items-center sm:justify-between sm:px-7">
            <div className="min-h-5 text-xs text-slate-500">{notice}</div>
            <button
              onClick={start}
              disabled={running || items.length === 0}
              className="flex min-w-48 items-center justify-center gap-2 rounded-xl bg-emerald-300 px-5 py-3 text-sm font-bold text-emerald-950 shadow-[0_8px_30px_rgba(52,211,153,.15)] transition hover:bg-emerald-200 disabled:cursor-not-allowed disabled:opacity-50"
            >
              {running ? (
                <>
                  <LoaderCircle className="spinner" size={16} /> Processing batch ({pendingCount})
                </>
              ) : pendingCount > 0 ? (
                <>
                  <Download size={16} /> Process pending ({pendingCount})
                </>
              ) : completedCount > 0 ? (
                <>
                  <Download size={16} /> Download all ({completedCount})
                </>
              ) : (
                <>
                  <Download size={16} /> Generate subtitles
                </>
              )}
            </button>
          </div>
        </div>
      )}
    </section>
  );
}
