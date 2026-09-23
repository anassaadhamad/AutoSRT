# AutoSRT 🎬 ➡️ 📝

Batch video-to-SRT transcription web application built with **Next.js App Router**, **Tailwind CSS**, **FFmpeg**, and **Groq Whisper API**.

---

## ⚡ Features

- **Batch Processing**: Upload multiple video files simultaneously (`.mp4`, `.mov`, `.mkv`, `.avi`, `.webm`, etc.).
- **Real-Time Streaming**: Live streaming status per file (`extracting`, `transcribing`, `ready`, `error`).
- **Groq Whisper AI**: Ultra-fast AI speech-to-text with automatic retry on rate limits.
- **Direct & Zip Downloads**: Single files download as `.srt` directly; batch files bundle into a clean `subtitles.zip`.
- **Production & Coolify Ready**: Includes optimized multi-stage `Dockerfile`, `docker-compose.yml`, health checks (`/api/health`), and non-root security.

---

## 🚀 Quick Start (Local Development)

1. **Install dependencies**:
   ```bash
   npm install
   ```

2. **Configure environment**:
   ```bash
   cp .env.example .env.local
   ```
   Add your `GROQ_API_KEY` in `.env.local`.

3. **Run development server**:
   ```bash
   npm run dev
   ```
   Open [http://localhost:3000](http://localhost:3000) in your browser.

---

## 🐳 Docker & Coolify Deployment

This repository is pre-configured for one-click deployment on **Coolify** or any Docker-compatible VPS.

### Deploying on Coolify
See the detailed guide in [COOLIFY_GUIDE.md](./COOLIFY_GUIDE.md).

- **Build Type**: `Dockerfile`
- **Internal Port**: `3000`
- **Health Check Path**: `/api/health`
- **Environment Variables**: Set `GROQ_API_KEY` in Coolify dashboard.

### Local Docker Run
```bash
docker compose up --build -d
```

---

## ⚙️ Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| `GROQ_API_KEY` | **Yes** | - | Groq API Key for Whisper transcription |
| `GROQ_WHISPER_MODEL` | No | `whisper-large-v3-turbo` | Whisper model to use |
| `TRANSCRIPTION_CONCURRENCY` | No | `2` | Number of videos processed concurrently |
| `MAX_BATCH_FILES` | No | `10` | Maximum number of files in a single batch |
| `MAX_FILE_SIZE_MB` | No | `500` | Max file size per video (in MB) |
| `FFMPEG_PATH` | No | `/usr/bin/ffmpeg` or auto | Path to ffmpeg binary |

---

## 📄 License
MIT
