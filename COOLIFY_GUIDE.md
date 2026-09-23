# دليل النشر على منصة Coolify 🚀

هذا الدليل يوضح خطوات نشر تطبيق **AutoSRT** على منصة **Coolify** بسلاسة واحترافية وبدون أي أخطاء.

---

## 1. المتطلبات المسبقة
- سيرفر مثبت عليه [Coolify](https://coolify.io).
- مفتاح API من Groq (`GROQ_API_KEY`) من [Groq Console](https://console.groq.com/keys).

---

## 2. خطوات النشر عبر Coolify

### الطريقة الموصى بها: عبر GitHub (مباشرة)

1. **إضافة المشروع**:
   - داخل لوحة تحكم Coolify، اذهب إلى مشروعك ثم اضغط **+ New Resource**.
   - اختر **Public Repository** أو **Private Repository** (إذا كان الحساب مربوطاً).
   - ضع رابط المستودع:
     ```
     https://github.com/anassaadhamad/AutoSRT
     ```
   - الفرع (Branch): `main`.

2. **نوع البناء (Build Pack)**:
   - سيتعرف Coolify تلقائياً على وجود ملف `Dockerfile`.
   - تأكد من اختيار **Dockerfile** كنوع البناء.
   - مسار الـ Dockerfile: `/Dockerfile`.

3. **المتغيرات البيئية (Environment Variables)**:
   في تبويب **Environment Variables** في Coolify، أضف المتغير الإلزامي:
   - `GROQ_API_KEY`: مفتاح Groq الخاص بك (مثال: `gsk_...`).

   *(اختياري) يمكنك تخصيص الإعدادات التالية حسب رغبتك:*
   - `GROQ_WHISPER_MODEL`: `whisper-large-v3-turbo` (افتراضي).
   - `TRANSCRIPTION_CONCURRENCY`: `2` (عدد الملفات المتزامنة).
   - `MAX_BATCH_FILES`: `10` (أقصى عدد ملفات في الدفعة).
   - `MAX_FILE_SIZE_MB`: `500` (الحد الأقصى لحجم الفيديو بالميجابايت).

4. **إعدادات المنفذ والصحة (Port & Healthcheck)**:
   - المنفذ الداخلي (Internal Port): `3000`.
   - مسار فحص الجاهزية (Health Check Path): `/api/health`.

5. **بدء النشر**:
   - اضغط على **Deploy**.
   - سيقوم Coolify ببناء صورة Docker متعددة المراحل وتشغيل التطبيق بحجم فائق الصغر مع دعم FFmpeg الأصلي بنجاح تام!

---

## 3. التحقق من عمل التطبيق
- بعد انتهاء النشر، افتح النطاق (Domain) الذي وفره لك Coolify.
- يمكنك زيارة `/api/health` للتأكد من أن السيرفر يعمل بكامل طاقته وأن FFmpeg جاهز ومفتاح Groq مهيأ:
  ```json
  {
    "status": "ok",
    "app": "AutoSRT",
    "timestamp": "2026-09-23T...",
    "ffmpeg": true,
    "groqConfigured": true
  }
  ```

---

## 4. المزايا التقنية المدمجة للنشر
- **Standalone Build**: تم تفعيل بناء Next.js المستقل لتخفيض حجم الحاوية وتسريع الإقلاع.
- **Native FFmpeg**: تم تثبيت ثنائي FFmpeg رسمياً داخل نظام Debian Bookworm في الحاوية لضمان استخراج الصوت بسرعة وثبات.
- **Non-Root User**: الحاوية تعمل تحت المستخدم الآمن `nextjs:nodejs` (UID/GID 1001) لمنع أي ثغرات أمنية على مستوى السيرفر.
- **Graceful Healthcheck**: نقطة فحص `/api/health` تمنع انقطاع الخدمة أثناء التحديثات (Zero-downtime rolling deploys).
