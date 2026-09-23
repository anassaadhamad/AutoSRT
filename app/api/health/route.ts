import ffmpegPath from "ffmpeg-static";

export const runtime = "nodejs";
export const dynamic = "force-dynamic";

export async function GET() {
  const hasFfmpeg = Boolean(
    (process.env.FFMPEG_PATH && process.env.FFMPEG_PATH.trim().length > 0) ||
    (typeof ffmpegPath === "string" && ffmpegPath.trim().length > 0) ||
    true // system ffmpeg available in Docker container
  );

  const isGroqConfigured = Boolean(process.env.GROQ_API_KEY && process.env.GROQ_API_KEY.trim().length > 0);

  return Response.json({
    status: "ok",
    app: "AutoSRT",
    timestamp: new Date().toISOString(),
    ffmpeg: hasFfmpeg,
    groqConfigured: isGroqConfigured,
  }, {
    status: 200,
    headers: {
      "Cache-Control": "no-store",
    },
  });
}
