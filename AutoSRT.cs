using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace AutoSRTDesktop
{
    // =========================================================================
    // AutoSRT Desktop (Local Edition)
    // 100% Standalone Windows Desktop Application
    // Multi-Language Support (English as Default, Arabic, etc.)
    // Zero external servers - Direct local transcription via user's Groq API Key
    // =========================================================================

    #region Internationalization (i18n)
    public static class I18n
    {
        public static string CurrentLanguage = "en"; // Default primary language is English

        public static bool IsArabic
        {
            get { return string.Equals(CurrentLanguage, "ar", StringComparison.OrdinalIgnoreCase); }
        }

        public static string T(string en, string ar)
        {
            return IsArabic ? ar : en;
        }
    }
    #endregion

    public static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        public static void Main()
        {
            try { SetProcessDPIAware(); } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Enable TLS 1.2 for modern API endpoints
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            }
            catch { }

            Application.Run(new MainForm());
        }
    }

    #region Configuration & Settings
    public class AppSettings
    {
        public string UiLanguage = "en"; // English by default
        public string GroqApiKey = "";
        public string Model = "whisper-large-v3-turbo";
        public string Language = "auto";
        public bool SaveAlongsideMedia = true;
        public string CustomOutputDir = "";
        public string CustomFfmpegPath = "";

        private static string ConfigDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoSRT");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        private static string ConfigFile
        {
            get { return Path.Combine(ConfigDir, "settings.ini"); }
        }

        public static AppSettings Load()
        {
            AppSettings settings = new AppSettings();
            try
            {
                if (File.Exists(ConfigFile))
                {
                    string[] lines = File.ReadAllLines(ConfigFile, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string key = line.Substring(0, eq).Trim();
                            string val = line.Substring(eq + 1).Trim();
                            switch (key)
                            {
                                case "UiLanguage": settings.UiLanguage = val; break;
                                case "GroqApiKey": settings.GroqApiKey = val; break;
                                case "Model": settings.Model = val; break;
                                case "Language": settings.Language = val; break;
                                case "SaveAlongsideMedia": settings.SaveAlongsideMedia = (val.ToLower() == "true"); break;
                                case "CustomOutputDir": settings.CustomOutputDir = val; break;
                                case "CustomFfmpegPath": settings.CustomFfmpegPath = val; break;
                            }
                        }
                    }
                }
            }
            catch { }
            return settings;
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# AutoSRT Local Desktop Settings");
                sb.AppendLine("UiLanguage=" + (UiLanguage ?? "en"));
                sb.AppendLine("GroqApiKey=" + (GroqApiKey ?? ""));
                sb.AppendLine("Model=" + (Model ?? "whisper-large-v3-turbo"));
                sb.AppendLine("Language=" + (Language ?? "auto"));
                sb.AppendLine("SaveAlongsideMedia=" + (SaveAlongsideMedia ? "true" : "false"));
                sb.AppendLine("CustomOutputDir=" + (CustomOutputDir ?? ""));
                sb.AppendLine("CustomFfmpegPath=" + (CustomFfmpegPath ?? ""));
                File.WriteAllText(ConfigFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
    #endregion

    #region Media Item Model
    public enum MediaStatus
    {
        Pending,
        ExtractingAudio,
        Transcribing,
        Completed,
        Failed,
        Cancelled
    }

    public class MediaItem
    {
        public string FilePath { get; set; }
        public string FileName { get { return Path.GetFileName(FilePath); } }
        public long FileSizeBytes { get; set; }
        public string FormattedSize
        {
            get
            {
                double mb = FileSizeBytes / (1024.0 * 1024.0);
                if (mb < 1.0) return string.Format("{0:0.0} KB", FileSizeBytes / 1024.0);
                return string.Format("{0:0.1} MB", mb);
            }
        }
        public bool IsAudioOnly
        {
            get
            {
                string ext = Path.GetExtension(FilePath).ToLowerInvariant();
                return ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".aac" || ext == ".flac" || ext == ".ogg" || ext == ".opus";
            }
        }
        public MediaStatus Status { get; set; }
        public string StatusMessage { get; set; }
        public string OutputSrtPath { get; set; }
        public string ErrorMessage { get; set; }

        public MediaItem(string path)
        {
            FilePath = path;
            FileInfo fi = new FileInfo(path);
            FileSizeBytes = fi.Exists ? fi.Length : 0;
            Status = MediaStatus.Pending;
            StatusMessage = I18n.T("Queued", "بالانتظار");
        }
    }
    #endregion

    #region FFmpeg Helper
    public static class FFmpegHelper
    {
        public static string AppDataBinDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoSRT", "bin");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static string ResolveFfmpegPath(string customPath)
        {
            if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
                return customPath;

            // 1. Check next to application exe
            string appDirFfmpeg = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(appDirFfmpeg)) return appDirFfmpeg;

            // 2. Check LocalAppData AutoSRT bin
            string localBin = Path.Combine(AppDataBinDir, "ffmpeg.exe");
            if (File.Exists(localBin)) return localBin;

            // 3. Check system PATH
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string[] paths = pathEnv.Split(';');
            foreach (string p in paths)
            {
                try
                {
                    string candidate = Path.Combine(p.Trim(), "ffmpeg.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }

            return null;
        }

        public static bool IsFfmpegAvailable(string customPath)
        {
            return ResolveFfmpegPath(customPath) != null;
        }

        public static async Task<string> ExtractAudioAsync(string ffmpegExe, string inputPath, CancellationToken ct, Action<string> logCallback)
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AutoSRT");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

            string outputMp3 = Path.Combine(tempDir, "audio_" + Guid.NewGuid().ToString("N") + ".mp3");

            // Optimized speech compression: 16kHz mono, ~48-64kbps MP3
            // Dramatic size reduction (e.g. 1 hour video -> ~20 MB audio), ideal for Whisper
            string args = string.Format("-y -nostdin -protocol_whitelist \"file,crypto,data\" -i \"{0}\" -vn -acodec libmp3lame -q:a 5 -ac 1 -ar 16000 \"{1}\"", inputPath, outputMp3);

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            Process process = new Process { StartInfo = psi };
            try
            {
                process.Start();

                var tcs = new TaskCompletionSource<bool>();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        process.WaitForExit();
                        tcs.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                });

                using (ct.Register(delegate
                {
                    try { if (!process.HasExited) process.Kill(); } catch { }
                    tcs.TrySetCanceled();
                }))
                {
                    await tcs.Task;
                }

                if (process.ExitCode != 0)
                {
                    string error = process.StandardError.ReadToEnd();
                    if (error.Contains("matches no streams") || error.Contains("does not contain any stream"))
                    {
                        throw new Exception(I18n.T("The media file has no audio stream to transcribe.", "الملف لا يحتوي على أي مسار صوتي (Audio track) لتفريغه."));
                    }
                    throw new Exception(I18n.T("FFmpeg audio extraction failed: exit code " + process.ExitCode, "فشل استخراج الصوت عبر FFmpeg: كود الخطأ " + process.ExitCode));
                }

                if (!File.Exists(outputMp3) || new FileInfo(outputMp3).Length == 0)
                {
                    throw new Exception(I18n.T("Failed to produce a valid audio file.", "لم يتم إنتاج ملف صوتي صالح."));
                }

                return outputMp3;
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                process.Dispose();
            }
        }
    }
    #endregion

    #region Groq Whisper Client
    public static class GroqClient
    {
        private static readonly HttpClient client = new HttpClient();

        static GroqClient()
        {
            client.Timeout = TimeSpan.FromMinutes(10);
        }

        public static async Task<string> TranscribeAsync(
            string apiKey,
            string audioFilePath,
            string model,
            string language,
            CancellationToken ct,
            Action<string> statusCallback)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception(I18n.T(
                    "Groq API Key is missing. Please enter your API key in the top settings bar.",
                    "مفتاح Groq API غير محدد. يرجى إدخال المفتاح أولاً من شريط الإعدادات بالأعلى."
                ));
            }

            if (!File.Exists(audioFilePath))
            {
                throw new Exception(I18n.T("Audio file not found: ", "ملف الصوت غير موجود: ") + audioFilePath);
            }

            FileInfo fi = new FileInfo(audioFilePath);
            // Groq max size is 25MB (26,214,400 bytes)
            if (fi.Length > 25 * 1024 * 1024)
            {
                throw new Exception(string.Format(
                    I18n.T(
                        "Audio file size ({0:0.1} MB) exceeds Groq's maximum 25 MB limit. Please shorten or compress the file.",
                        "حجم الملف الصوتي ({0:0.1} MB) يتجاوز الحد الأقصى لواجهة Groq (25 MB). يرجى تقليل مدة الملف أو ضغطه."
                    ),
                    fi.Length / (1024.0 * 1024.0)
                ));
            }

            statusCallback(I18n.T("Connecting to Groq Whisper...", "جاري الاتصال بـ Groq Whisper..."));

            using (var form = new MultipartFormDataContent())
            {
                form.Add(new StringContent(string.IsNullOrEmpty(model) ? "whisper-large-v3-turbo" : model), "model");
                form.Add(new StringContent("verbose_json"), "response_format");
                form.Add(new StringContent("0"), "temperature");

                if (!string.IsNullOrEmpty(language) && language != "auto")
                {
                    form.Add(new StringContent(language), "language");
                }

                byte[] fileBytes = File.ReadAllBytes(audioFilePath);
                var fileContent = new ByteArrayContent(fileBytes);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(GetMimeType(audioFilePath));
                form.Add(fileContent, "file", Path.GetFileName(audioFilePath));

                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions"))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
                    request.Content = form;

                    statusCallback(I18n.T("Transcribing speech to text with Groq AI...", "جاري التفريغ وتحويل الكلام إلى نص عبر الذكاء الاصطناعي..."));

                    using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct))
                    {
                        if (response.IsSuccessStatusCode)
                        {
                            string jsonContent = await response.Content.ReadAsStringAsync();
                            return ConvertVerboseJsonToSrt(jsonContent);
                        }

                        string errBody = await response.Content.ReadAsStringAsync();
                        if (response.StatusCode == HttpStatusCode.Unauthorized)
                        {
                            throw new Exception(I18n.T("Invalid Groq API Key (Unauthorized). Please verify your key.", "مفتاح Groq API غير صالح (Unauthorized). تأكد من صحة المفتاح."));
                        }
                        else if ((int)response.StatusCode == 429)
                        {
                            throw new Exception(I18n.T("Groq API rate limit reached. Please wait a few seconds and try again.", "تم تجاوز الحد المسموح للطلبات (Rate Limit) في Groq. انتظر بضع ثوانٍ وأعد المحاولة."));
                        }
                        else
                        {
                            throw new Exception(I18n.T("Groq API Error (", "خطأ من Groq API (") + (int)response.StatusCode + "): " + errBody);
                        }
                    }
                }
            }
        }

        private static string ConvertVerboseJsonToSrt(string json)
        {
            var jss = new JavaScriptSerializer();
            jss.MaxJsonLength = int.MaxValue;
            var result = jss.Deserialize<GroqTranscriptionResult>(json);

            if (result == null || result.segments == null || result.segments.Count == 0)
            {
                if (result != null && !string.IsNullOrWhiteSpace(result.text))
                {
                    return "1\r\n00:00:00,000 --> 00:00:10,000\r\n" + result.text.Trim() + "\r\n";
                }
                throw new Exception(I18n.T(
                    "The transcription returned no speech segments (audio might be silent or music only).",
                    "لم يتم العثور على أي كلام منطوق في الملف (قد يكون الملف صامتاً أو يحتوي موسيقى فقط)."
                ));
            }

            StringBuilder sb = new StringBuilder();
            int index = 1;
            for (int i = 0; i < result.segments.Count; i++)
            {
                var seg = result.segments[i];
                if (seg == null || string.IsNullOrWhiteSpace(seg.text)) continue;

                double start = Math.Max(0, seg.start);
                double end = Math.Max(seg.end, start + 0.001);

                sb.AppendLine(index.ToString());
                sb.AppendLine(FormatSrtTime(start) + " --> " + FormatSrtTime(end));
                sb.AppendLine(seg.text.Trim());
                sb.AppendLine();
                index++;
            }

            if (index == 1)
            {
                throw new Exception(I18n.T("No speech segments found in file.", "لم يتم العثور على أي كلام منطوق لتفريغه."));
            }

            return sb.ToString();
        }

        private static string FormatSrtTime(double seconds)
        {
            long totalMillis = (long)Math.Round(seconds * 1000.0);
            if (totalMillis < 0) totalMillis = 0;
            long hours = totalMillis / 3600000;
            long minutes = (totalMillis % 3600000) / 60000;
            long secs = (totalMillis % 60000) / 1000;
            long millis = totalMillis % 1000;
            return string.Format("{0:00}:{1:00}:{2:00},{3:000}", hours, minutes, secs, millis);
        }

        private static string GetMimeType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".mp3": return "audio/mpeg";
                case ".m4a": return "audio/m4a";
                case ".wav": return "audio/wav";
                case ".ogg": return "audio/ogg";
                case ".opus": return "audio/opus";
                case ".flac": return "audio/flac";
                case ".aac": return "audio/aac";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                default: return "application/octet-stream";
            }
        }
    }

    public class GroqSegment
    {
        public double start { get; set; }
        public double end { get; set; }
        public string text { get; set; }
    }

    public class GroqTranscriptionResult
    {
        public string text { get; set; }
        public List<GroqSegment> segments { get; set; }
    }
    #endregion

    #region Main GUI Form
    public class MainForm : Form
    {
        private AppSettings settings;
        private List<MediaItem> queue = new List<MediaItem>();
        private CancellationTokenSource cts;
        private bool isProcessing = false;

        // UI Colors (Slate & Indigo Modern Dark Theme)
        private Color colorBg = Color.FromArgb(15, 23, 42);          // Slate 900
        private Color colorCard = Color.FromArgb(30, 41, 59);        // Slate 800
        private Color colorCardBorder = Color.FromArgb(51, 65, 85);  // Slate 700
        private Color colorPrimary = Color.FromArgb(99, 102, 241);    // Indigo 500
        private Color colorPrimaryHover = Color.FromArgb(79, 70, 229);// Indigo 600
        private Color colorSuccess = Color.FromArgb(16, 185, 129);   // Emerald 500
        private Color colorDanger = Color.FromArgb(239, 68, 68);     // Red 500
        private Color colorText = Color.FromArgb(248, 250, 252);     // Slate 50
        private Color colorTextMuted = Color.FromArgb(148, 163, 184);// Slate 400

        // Header Controls
        private Panel pnlHeader;
        private Label lblTitle;
        private Label lblSub;
        private LinkLabel lnkGetFreeKey;
        private ComboBox cmbUiLang;

        // Settings Controls
        private Panel pnlSettings;
        private Label lblKey;
        private TextBox txtApiKey;
        private Button btnToggleKey;
        private Label lblModel;
        private ComboBox cmbModel;
        private Label lblLang;
        private ComboBox cmbLang;
        private CheckBox chkAlongside;
        private TextBox txtOutputDir;
        private Button btnBrowseOutput;
        private Label lblFfmpegStatus;
        private Button btnFfmpegAction;

        // Actions Controls
        private Panel pnlActions;
        private Button btnAddFiles;
        private Button btnAddFolder;
        private Button btnClear;
        private Label lblDragTip;

        // Grid & Footer Controls
        private DataGridView grid;
        private Panel pnlFooter;
        private ProgressBar prgActive;
        private Label lblStatus;
        private Button btnStart;
        private Button btnCancel;

        public MainForm()
        {
            settings = AppSettings.Load();
            I18n.CurrentLanguage = string.IsNullOrEmpty(settings.UiLanguage) ? "en" : settings.UiLanguage;

            InitializeComponent();
            ApplyLocalization();
            RefreshFfmpegStatus();
        }

        private void InitializeComponent()
        {
            this.Size = new Size(1120, 760);
            this.MinimumSize = new Size(980, 620);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = colorBg;
            this.ForeColor = colorText;
            this.Font = new Font("Segoe UI", 9.5f, FontStyle.Regular);

            // Load app icon if exists
            try
            {
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                if (File.Exists(iconPath)) this.Icon = new Icon(iconPath);
            }
            catch { }

            // Allow Drag & Drop on entire window
            this.AllowDrop = true;
            this.DragEnter += MainForm_DragEnter;
            this.DragDrop += MainForm_DragDrop;

            // Main Layout Containers
            pnlHeader = CreateHeaderPanel();
            pnlSettings = CreateSettingsPanel();
            pnlActions = CreateActionsBar();
            grid = CreateDataGridView();
            pnlFooter = CreateFooterPanel();

            this.Controls.Add(grid);
            this.Controls.Add(pnlActions);
            this.Controls.Add(pnlSettings);
            this.Controls.Add(pnlHeader);
            this.Controls.Add(pnlFooter);

            // Docking order
            pnlHeader.Dock = DockStyle.Top;
            pnlSettings.Dock = DockStyle.Top;
            pnlActions.Dock = DockStyle.Top;
            pnlFooter.Dock = DockStyle.Bottom;
            grid.Dock = DockStyle.Fill;
            grid.BringToFront();
        }

        #region Localization Engine
        private void ApplyLocalization()
        {
            bool isAr = I18n.IsArabic;
            this.RightToLeft = isAr ? RightToLeft.Yes : RightToLeft.No;
            this.RightToLeftLayout = isAr;

            this.Text = I18n.T(
                "AutoSRT Desktop - AI Video & Audio Subtitle Generator (Local Edition)",
                "AutoSRT Desktop - تفريغ وترجمة الفيديوهات بالذكاء الاصطناعي (Local Edition)"
            );

            // Header Texts
            lblTitle.Text = I18n.T(
                "⚡ AutoSRT Desktop (Local Edition)",
                "⚡ AutoSRT Desktop (نسخة سطح المكتب المستقلة)"
            );
            lblSub.Text = I18n.T(
                "100% Standalone local desktop app • Zero intermediate servers • Ultra-fast Groq Whisper AI",
                "تفريغ وترجمة الفيديوهات والملفات الصوتية إلى ملفات SRT محلياً 100% بدون أي خادم وسيط وبأعلى سرعة"
            );
            lnkGetFreeKey.Text = I18n.T(
                "🔑 Get Free Groq API Key (No Card)",
                "🔑 احصل على مفتاح Groq مجاني (بدون فيزا)"
            );

            // Settings Texts
            lblKey.Text = I18n.T("Groq API Key:", "مفتاح Groq API:");
            lblModel.Text = I18n.T("Model:", "النموذج:");
            lblLang.Text = I18n.T("Audio Lang:", "لغة الصوت:");

            chkAlongside.Text = I18n.T(
                "Save .srt file alongside original media file",
                "حفظ ملف SRT بجانب ملف الفيديو الأصلي مباشرة"
            );
            btnBrowseOutput.Text = I18n.T("Custom Folder...", "مجلد مخصص...");

            // Populate Model ComboBox
            int prevModelIdx = cmbModel.SelectedIndex >= 0 ? cmbModel.SelectedIndex : (settings.Model.Contains("turbo") ? 0 : 1);
            cmbModel.Items.Clear();
            cmbModel.Items.Add(I18n.T("whisper-large-v3-turbo (Ultra Fast & Free)", "whisper-large-v3-turbo (فائق السرعة ومجاني)"));
            cmbModel.Items.Add(I18n.T("whisper-large-v3 (Maximum Accuracy & Free)", "whisper-large-v3 (أعلى دقة ومجاني)"));
            cmbModel.SelectedIndex = prevModelIdx;

            // Populate Audio Language ComboBox
            int prevLangIdx = cmbLang.SelectedIndex >= 0 ? cmbLang.SelectedIndex : GetLanguageIndex(settings.Language);
            cmbLang.Items.Clear();
            cmbLang.Items.Add(I18n.T("Auto Detect", "تلقائي (Auto)"));
            cmbLang.Items.Add(I18n.T("Arabic (ar)", "العربية (ar)"));
            cmbLang.Items.Add(I18n.T("English (en)", "الإنجليزية (en)"));
            cmbLang.Items.Add(I18n.T("French (fr)", "الفرنسية (fr)"));
            cmbLang.Items.Add(I18n.T("German (de)", "الألمانية (de)"));
            cmbLang.Items.Add(I18n.T("Spanish (es)", "الإسبانية (es)"));
            cmbLang.Items.Add(I18n.T("Turkish (tr)", "التركية (tr)"));
            cmbLang.SelectedIndex = prevLangIdx >= 0 && prevLangIdx < cmbLang.Items.Count ? prevLangIdx : 0;

            // Action Bar Texts
            btnAddFiles.Text = I18n.T("➕ Add Video / Audio Files", "➕ إضافة ملفات فيديو / صوت");
            btnAddFolder.Text = I18n.T("📁 Add Entire Folder", "📁 إضافة مجلد كامل");
            btnClear.Text = I18n.T("🗑️ Clear Queue", "🗑️ مسح القائمة");
            lblDragTip.Text = I18n.T(
                "💡 Drag & drop video or audio files/folders anywhere in this window",
                "💡 يمكنك سحب وإفلات الملفات أو المجلدات مباشرة داخل هذا الجدول"
            );

            // Grid Column Headers
            grid.Columns[0].HeaderText = "#";
            grid.Columns[1].HeaderText = I18n.T("File Name", "اسم الملف");
            grid.Columns[2].HeaderText = I18n.T("Type / Size", "النوع / الحجم");
            grid.Columns[3].HeaderText = I18n.T("Status & Pipeline Progress", "الحالة ومراحل المعالجة");
            grid.Columns[4].HeaderText = I18n.T("Folder", "المجلد");
            grid.Columns[5].HeaderText = I18n.T("Preview", "معاينة");
            grid.Columns[6].HeaderText = I18n.T("Remove", "حذف");

            ((DataGridViewButtonColumn)grid.Columns[4]).Text = I18n.T("📂 Open", "📂 فتح");
            ((DataGridViewButtonColumn)grid.Columns[5]).Text = I18n.T("👁️ View", "👁️ عرض");
            ((DataGridViewButtonColumn)grid.Columns[6]).Text = "✖";

            // Footer Texts
            btnStart.Text = I18n.T("🚀 Start Transcription", "🚀 بدء التفريغ والترجمة");
            btnCancel.Text = I18n.T("⏹️ Cancel", "⏹️ إلغاء");

            RefreshFfmpegStatus();
            RefreshGrid();
        }

        private int GetLanguageIndex(string code)
        {
            switch (code)
            {
                case "ar": return 1;
                case "en": return 2;
                case "fr": return 3;
                case "de": return 4;
                case "es": return 5;
                case "tr": return 6;
                default: return 0;
            }
        }

        private string GetSelectedLanguageCode()
        {
            switch (cmbLang.SelectedIndex)
            {
                case 1: return "ar";
                case 2: return "en";
                case 3: return "fr";
                case 4: return "de";
                case 5: return "es";
                case 6: return "tr";
                default: return "auto";
            }
        }
        #endregion

        #region Header Panel
        private Panel CreateHeaderPanel()
        {
            Panel pnl = new Panel
            {
                Height = 68,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(11, 17, 32),
                Padding = new Padding(20, 10, 20, 10)
            };

            pnl.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(colorCardBorder))
                {
                    e.Graphics.DrawLine(pen, 0, pnl.Height - 1, pnl.Width, pnl.Height - 1);
                }
            };

            lblTitle = new Label
            {
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                ForeColor = colorText,
                AutoSize = true,
                Location = new Point(20, 12)
            };

            lblSub = new Label
            {
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = colorTextMuted,
                AutoSize = true,
                Location = new Point(20, 38)
            };

            // Language Selector Dropdown
            cmbUiLang = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120,
                Location = new Point(680, 20),
                BackColor = colorCard,
                ForeColor = colorText,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            cmbUiLang.Items.Add("English 🇬🇧");
            cmbUiLang.Items.Add("العربية 🇸🇦");
            cmbUiLang.SelectedIndex = I18n.IsArabic ? 1 : 0;
            cmbUiLang.SelectedIndexChanged += delegate
            {
                string newLang = cmbUiLang.SelectedIndex == 1 ? "ar" : "en";
                if (I18n.CurrentLanguage != newLang)
                {
                    I18n.CurrentLanguage = newLang;
                    settings.UiLanguage = newLang;
                    settings.Save();
                    ApplyLocalization();
                }
            };

            lnkGetFreeKey = new LinkLabel
            {
                LinkColor = Color.FromArgb(129, 140, 248),
                ActiveLinkColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                AutoSize = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(815, 23)
            };
            lnkGetFreeKey.LinkClicked += delegate
            {
                try { Process.Start("https://console.groq.com/keys"); } catch { }
            };

            pnl.Controls.Add(lblTitle);
            pnl.Controls.Add(lblSub);
            pnl.Controls.Add(cmbUiLang);
            pnl.Controls.Add(lnkGetFreeKey);

            return pnl;
        }
        #endregion

        #region Settings Panel
        private Panel CreateSettingsPanel()
        {
            Panel pnl = new Panel
            {
                Height = 118,
                Dock = DockStyle.Top,
                BackColor = colorCard,
                Padding = new Padding(15, 10, 15, 10)
            };

            pnl.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(colorCardBorder))
                {
                    e.Graphics.DrawLine(pen, 0, pnl.Height - 1, pnl.Width, pnl.Height - 1);
                }
            };

            // Row 1: API Key & Model & Audio Language
            lblKey = new Label { AutoSize = true, Location = new Point(15, 16), ForeColor = colorTextMuted };
            txtApiKey = new TextBox
            {
                Text = settings.GroqApiKey,
                UseSystemPasswordChar = true,
                Location = new Point(125, 13),
                Width = 260,
                BackColor = colorBg,
                ForeColor = colorText,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Consolas", 9.5f)
            };
            txtApiKey.TextChanged += delegate
            {
                settings.GroqApiKey = txtApiKey.Text.Trim();
                settings.Save();
            };

            btnToggleKey = new Button
            {
                Text = "👁",
                Width = 32,
                Height = 25,
                Location = new Point(390, 12),
                FlatStyle = FlatStyle.Flat,
                BackColor = colorCardBorder,
                ForeColor = colorText,
                Cursor = Cursors.Hand
            };
            btnToggleKey.FlatAppearance.BorderSize = 0;
            btnToggleKey.Click += delegate
            {
                txtApiKey.UseSystemPasswordChar = !txtApiKey.UseSystemPasswordChar;
                btnToggleKey.Text = txtApiKey.UseSystemPasswordChar ? "👁" : "🔒";
            };

            lblModel = new Label { AutoSize = true, Location = new Point(440, 16), ForeColor = colorTextMuted };
            cmbModel = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(500, 13),
                Width = 220,
                BackColor = colorBg,
                ForeColor = colorText,
                FlatStyle = FlatStyle.Flat
            };
            cmbModel.SelectedIndexChanged += delegate
            {
                settings.Model = cmbModel.SelectedIndex == 0 ? "whisper-large-v3-turbo" : "whisper-large-v3";
                settings.Save();
            };

            lblLang = new Label { AutoSize = true, Location = new Point(735, 16), ForeColor = colorTextMuted };
            cmbLang = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(815, 13),
                Width = 140,
                BackColor = colorBg,
                ForeColor = colorText,
                FlatStyle = FlatStyle.Flat
            };
            cmbLang.SelectedIndexChanged += delegate
            {
                settings.Language = GetSelectedLanguageCode();
                settings.Save();
            };

            // Row 2: Output Options & FFmpeg Status
            chkAlongside = new CheckBox
            {
                Checked = settings.SaveAlongsideMedia,
                AutoSize = true,
                Location = new Point(15, 52),
                ForeColor = colorText
            };
            chkAlongside.CheckedChanged += delegate
            {
                settings.SaveAlongsideMedia = chkAlongside.Checked;
                txtOutputDir.Enabled = !chkAlongside.Checked;
                btnBrowseOutput.Enabled = !chkAlongside.Checked;
                settings.Save();
            };

            txtOutputDir = new TextBox
            {
                Text = settings.CustomOutputDir,
                Location = new Point(340, 50),
                Width = 210,
                BackColor = colorBg,
                ForeColor = colorText,
                BorderStyle = BorderStyle.FixedSingle,
                Enabled = !chkAlongside.Checked
            };
            txtOutputDir.TextChanged += delegate
            {
                settings.CustomOutputDir = txtOutputDir.Text.Trim();
                settings.Save();
            };

            btnBrowseOutput = new Button
            {
                Width = 110,
                Height = 25,
                Location = new Point(555, 49),
                FlatStyle = FlatStyle.Flat,
                BackColor = colorCardBorder,
                ForeColor = colorText,
                Cursor = Cursors.Hand,
                Enabled = !chkAlongside.Checked
            };
            btnBrowseOutput.FlatAppearance.BorderSize = 0;
            btnBrowseOutput.Click += delegate
            {
                using (FolderBrowserDialog fbd = new FolderBrowserDialog())
                {
                    fbd.Description = I18n.T("Select SRT Subtitles Output Folder", "اختر مجلد حفظ ملفات الترجمة SRT");
                    if (fbd.ShowDialog() == DialogResult.OK)
                    {
                        txtOutputDir.Text = fbd.SelectedPath;
                    }
                }
            };

            // FFmpeg Status Pill & Action
            lblFfmpegStatus = new Label
            {
                AutoSize = true,
                Location = new Point(680, 54),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            btnFfmpegAction = new Button
            {
                Width = 140,
                Height = 25,
                Location = new Point(940, 50),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = colorText,
                Cursor = Cursors.Hand,
                Visible = false
            };
            btnFfmpegAction.FlatAppearance.BorderSize = 0;
            btnFfmpegAction.Click += BtnFfmpegAction_Click;

            pnl.Controls.Add(lblKey);
            pnl.Controls.Add(txtApiKey);
            pnl.Controls.Add(btnToggleKey);
            pnl.Controls.Add(lblModel);
            pnl.Controls.Add(cmbModel);
            pnl.Controls.Add(lblLang);
            pnl.Controls.Add(cmbLang);

            pnl.Controls.Add(chkAlongside);
            pnl.Controls.Add(txtOutputDir);
            pnl.Controls.Add(btnBrowseOutput);
            pnl.Controls.Add(lblFfmpegStatus);
            pnl.Controls.Add(btnFfmpegAction);

            return pnl;
        }

        private void RefreshFfmpegStatus()
        {
            bool ready = FFmpegHelper.IsFfmpegAvailable(settings.CustomFfmpegPath);
            if (ready)
            {
                lblFfmpegStatus.Text = I18n.T("🟢 FFmpeg: Ready & available locally", "🟢 FFmpeg: متوفر ومفعل محلياً");
                lblFfmpegStatus.ForeColor = colorSuccess;
                btnFfmpegAction.Visible = false;
            }
            else
            {
                lblFfmpegStatus.Text = I18n.T("🟡 FFmpeg: Not found (Required for video)", "🟡 FFmpeg: غير متوفر (مطلوب للفيديوهات)");
                lblFfmpegStatus.ForeColor = Color.FromArgb(251, 191, 36);
                btnFfmpegAction.Text = I18n.T("Download FFmpeg", "تنزيل FFmpeg تلقائياً");
                btnFfmpegAction.Visible = true;
            }
        }

        private async void BtnFfmpegAction_Click(object sender, EventArgs e)
        {
            btnFfmpegAction.Enabled = false;
            btnFfmpegAction.Text = I18n.T("Downloading...", "جاري التنزيل...");
            lblFfmpegStatus.Text = I18n.T("⏳ Setting up portable FFmpeg in background...", "⏳ جاري إعداد وتنزيل FFmpeg في الخلفية...");

            try
            {
                await Task.Run(delegate
                {
                    string zipUrl = "https://github.com/GyanD/codexffmpeg/releases/download/7.1/ffmpeg-7.1-essentials_build.zip";
                    string tempZip = Path.Combine(Path.GetTempPath(), "ffmpeg_setup.zip");

                    using (WebClient wc = new WebClient())
                    {
                        wc.DownloadFile(zipUrl, tempZip);
                    }

                    string binDir = FFmpegHelper.AppDataBinDir;
                    using (ZipArchive archive = ZipFile.OpenRead(tempZip))
                    {
                        foreach (ZipArchiveEntry entry in archive.Entries)
                        {
                            if (entry.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                string destPath = Path.Combine(binDir, "ffmpeg.exe");
                                entry.ExtractToFile(destPath, true);
                                break;
                            }
                        }
                    }

                    try { File.Delete(tempZip); } catch { }
                });

                MessageBox.Show(
                    I18n.T("FFmpeg has been downloaded and configured successfully!", "تم تنزيل وتثبيت FFmpeg محلياً بنجاح!"),
                    "AutoSRT",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    I18n.T("Failed to download FFmpeg automatically (" + ex.Message + "). You can copy ffmpeg.exe next to AutoSRT.exe or add it to PATH.", "تعذر تنزيل FFmpeg تلقائياً (" + ex.Message + "). يمكنك نسخ ffmpeg.exe إلى نفس مجلد البرنامج أو إضافته إلى PATH."),
                    I18n.T("Notice", "تنبيه"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
            finally
            {
                RefreshFfmpegStatus();
                btnFfmpegAction.Enabled = true;
            }
        }
        #endregion

        #region Actions Bar
        private Panel CreateActionsBar()
        {
            Panel pnl = new Panel
            {
                Height = 54,
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(17, 24, 39),
                Padding = new Padding(15, 10, 15, 10)
            };

            btnAddFiles = new Button
            {
                Width = 200,
                Height = 34,
                Location = new Point(15, 10),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(79, 70, 229),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnAddFiles.FlatAppearance.BorderSize = 0;
            btnAddFiles.Click += BtnAddFiles_Click;

            btnAddFolder = new Button
            {
                Width = 150,
                Height = 34,
                Location = new Point(225, 10),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnAddFolder.FlatAppearance.BorderSize = 0;
            btnAddFolder.Click += BtnAddFolder_Click;

            btnClear = new Button
            {
                Width = 120,
                Height = 34,
                Location = new Point(385, 10),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(51, 65, 85),
                ForeColor = Color.White,
                Cursor = Cursors.Hand
            };
            btnClear.FlatAppearance.BorderSize = 0;
            btnClear.Click += delegate
            {
                if (isProcessing) return;
                queue.Clear();
                RefreshGrid();
            };

            lblDragTip = new Label
            {
                ForeColor = colorTextMuted,
                AutoSize = true,
                Location = new Point(520, 18)
            };

            pnl.Controls.Add(btnAddFiles);
            pnl.Controls.Add(btnAddFolder);
            pnl.Controls.Add(btnClear);
            pnl.Controls.Add(lblDragTip);

            return pnl;
        }

        private void BtnAddFiles_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Multiselect = true;
                ofd.Filter = I18n.T(
                    "All Media Files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus|Video Files|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv|Audio Files|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus|All Files|*.*",
                    "جميع ملفات الميديا|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv;*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus|فيديو|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.flv;*.wmv|صوت|*.mp3;*.wav;*.m4a;*.aac;*.flac;*.ogg;*.opus|كل الملفات|*.*"
                );
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    AddFilesToQueue(ofd.FileNames);
                }
            }
        }

        private void BtnAddFolder_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fbd = new FolderBrowserDialog())
            {
                fbd.Description = I18n.T("Select folder containing video or audio files", "اختر المجلد الذي يحتوي على ملفات الفيديو أو الصوت");
                if (fbd.ShowDialog() == DialogResult.OK)
                {
                    string[] exts = new string[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.flv", "*.wmv", "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus" };
                    List<string> found = new List<string>();
                    foreach (string ext in exts)
                    {
                        found.AddRange(Directory.GetFiles(fbd.SelectedPath, ext, SearchOption.TopDirectoryOnly));
                    }
                    AddFilesToQueue(found.ToArray());
                }
            }
        }

        private void AddFilesToQueue(string[] filePaths)
        {
            foreach (string path in filePaths)
            {
                if (File.Exists(path))
                {
                    bool exists = false;
                    foreach (var item in queue)
                    {
                        if (string.Equals(item.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (!exists)
                    {
                        queue.Add(new MediaItem(path));
                    }
                }
            }
            RefreshGrid();
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Length > 0)
            {
                List<string> collected = new List<string>();
                foreach (string f in files)
                {
                    if (File.Exists(f)) collected.Add(f);
                    else if (Directory.Exists(f))
                    {
                        string[] exts = new string[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.flv", "*.wmv", "*.mp3", "*.wav", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.opus" };
                        foreach (string ext in exts)
                        {
                            collected.AddRange(Directory.GetFiles(f, ext, SearchOption.TopDirectoryOnly));
                        }
                    }
                }
                AddFilesToQueue(collected.ToArray());
            }
        }
        #endregion

        #region DataGridView
        private DataGridView CreateDataGridView()
        {
            DataGridView dgv = new DataGridView
            {
                BackgroundColor = colorBg,
                GridColor = colorCardBorder,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                AutoGenerateColumns = false,
                EnableHeadersVisualStyles = false,
                AllowDrop = true
            };

            dgv.DragEnter += MainForm_DragEnter;
            dgv.DragDrop += MainForm_DragDrop;

            dgv.ColumnHeadersDefaultCellStyle.BackColor = colorCard;
            dgv.ColumnHeadersDefaultCellStyle.ForeColor = colorText;
            dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            dgv.ColumnHeadersHeight = 38;

            dgv.DefaultCellStyle.BackColor = Color.FromArgb(20, 30, 48);
            dgv.DefaultCellStyle.ForeColor = colorText;
            dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(67, 56, 202);
            dgv.DefaultCellStyle.SelectionForeColor = Color.White;
            dgv.DefaultCellStyle.Font = new Font("Segoe UI", 9f);
            dgv.RowTemplate.Height = 36;

            // Columns
            dgv.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "#", Width = 45, ReadOnly = true });

            var colName = new DataGridViewTextBoxColumn { HeaderText = "File Name", Width = 260, ReadOnly = true };
            colName.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            dgv.Columns.Add(colName);

            dgv.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Type / Size", Width = 120, ReadOnly = true });
            dgv.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status & Pipeline Progress", Width = 310, ReadOnly = true });

            var colFolder = new DataGridViewButtonColumn
            {
                HeaderText = "Folder",
                Width = 85,
                Text = "📂 Open",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat
            };
            dgv.Columns.Add(colFolder);

            var colView = new DataGridViewButtonColumn
            {
                HeaderText = "Preview",
                Width = 85,
                Text = "👁️ View",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat
            };
            dgv.Columns.Add(colView);

            var colDelete = new DataGridViewButtonColumn
            {
                HeaderText = "Remove",
                Width = 65,
                Text = "✖",
                UseColumnTextForButtonValue = true,
                FlatStyle = FlatStyle.Flat
            };
            dgv.Columns.Add(colDelete);

            dgv.CellContentClick += Dgv_CellContentClick;

            return dgv;
        }

        private void Dgv_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= queue.Count) return;
            var item = queue[e.RowIndex];

            // Col 4: Open Folder
            if (e.ColumnIndex == 4)
            {
                string target = !string.IsNullOrEmpty(item.OutputSrtPath) && File.Exists(item.OutputSrtPath)
                    ? item.OutputSrtPath
                    : item.FilePath;
                try
                {
                    Process.Start("explorer.exe", string.Format("/select,\"{0}\"", target));
                }
                catch { }
            }
            // Col 5: View SRT
            else if (e.ColumnIndex == 5)
            {
                if (!string.IsNullOrEmpty(item.OutputSrtPath) && File.Exists(item.OutputSrtPath))
                {
                    ShowSrtViewer(item.FileName, item.OutputSrtPath);
                }
                else
                {
                    MessageBox.Show(
                        I18n.T("No SRT subtitle file has been generated yet for this item.", "لم يتم إنتاج ملف الترجمة SRT بعد لهذا العنصر."),
                        I18n.T("Subtitle Preview", "معاينة الترجمة"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information
                    );
                }
            }
            // Col 6: Delete item
            else if (e.ColumnIndex == 6)
            {
                if (isProcessing && (item.Status == MediaStatus.ExtractingAudio || item.Status == MediaStatus.Transcribing))
                {
                    MessageBox.Show(
                        I18n.T("Cannot remove an item currently being processed. Please cancel first.", "لا يمكن حذف عنصر قيد المعالجة حالياً. قم بإلغاء المعالجة أولاً."),
                        I18n.T("Notice", "تنبيه"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning
                    );
                    return;
                }
                queue.RemoveAt(e.RowIndex);
                RefreshGrid();
            }
        }

        private void ShowSrtViewer(string fileName, string srtPath)
        {
            Form viewer = new Form
            {
                Text = I18n.T("Subtitle Preview: ", "معاينة ملف الترجمة: ") + fileName,
                Size = new Size(720, 560),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = colorBg,
                ForeColor = colorText,
                Font = new Font("Segoe UI", 9.5f),
                RightToLeft = RightToLeft.No // Subtitle timestamps are always LTR
            };

            TextBox txt = new TextBox
            {
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                Dock = DockStyle.Fill,
                BackColor = colorCard,
                ForeColor = colorText,
                Font = new Font("Consolas", 10.5f),
                ReadOnly = true,
                Text = File.ReadAllText(srtPath, Encoding.UTF8)
            };

            Panel pnlBottom = new Panel { Height = 48, Dock = DockStyle.Bottom, BackColor = Color.FromArgb(17, 24, 39), Padding = new Padding(10) };
            Button btnOpenNotepad = new Button
            {
                Text = I18n.T("Open in Notepad", "فتح في المفكرة (Notepad)"),
                Width = 180,
                Height = 30,
                Dock = DockStyle.Right,
                FlatStyle = FlatStyle.Flat,
                BackColor = colorPrimary,
                ForeColor = Color.White
            };
            btnOpenNotepad.FlatAppearance.BorderSize = 0;
            btnOpenNotepad.Click += delegate
            {
                try { Process.Start("notepad.exe", srtPath); } catch { }
            };

            pnlBottom.Controls.Add(btnOpenNotepad);
            viewer.Controls.Add(txt);
            viewer.Controls.Add(pnlBottom);
            viewer.ShowDialog(this);
        }

        private void RefreshGrid()
        {
            grid.Rows.Clear();
            for (int i = 0; i < queue.Count; i++)
            {
                var item = queue[i];
                string typeIcon = item.IsAudioOnly ? I18n.T("🎵 Audio", "🎵 صوت") : I18n.T("🎬 Video", "🎬 فيديو");
                string typeLabel = typeIcon + " (" + item.FormattedSize + ")";
                grid.Rows.Add(
                    (i + 1).ToString(),
                    item.FileName,
                    typeLabel,
                    item.StatusMessage
                );

                // Color coding for status
                var cell = grid.Rows[i].Cells[3];
                switch (item.Status)
                {
                    case MediaStatus.Completed:
                        cell.Style.ForeColor = colorSuccess;
                        break;
                    case MediaStatus.Failed:
                        cell.Style.ForeColor = colorDanger;
                        break;
                    case MediaStatus.ExtractingAudio:
                    case MediaStatus.Transcribing:
                        cell.Style.ForeColor = Color.FromArgb(129, 140, 248);
                        break;
                    default:
                        cell.Style.ForeColor = colorTextMuted;
                        break;
                }
            }

            UpdateSummaryLabel();
        }

        private void UpdateSummaryLabel()
        {
            if (isProcessing) return;
            int readyCount = 0;
            foreach (var it in queue) if (it.Status == MediaStatus.Completed) readyCount++;
            lblStatus.Text = string.Format(
                I18n.T("Total Files: {0} | Completed: {1} | Queued: {2}", "إجمالي الملفات: {0} | المكتمل: {1} | بالانتظار: {2}"),
                queue.Count,
                readyCount,
                queue.Count - readyCount
            );
        }
        #endregion

        #region Footer Panel
        private Panel CreateFooterPanel()
        {
            Panel pnl = new Panel
            {
                Height = 78,
                Dock = DockStyle.Bottom,
                BackColor = Color.FromArgb(11, 17, 32),
                Padding = new Padding(20, 12, 20, 12)
            };

            pnl.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(colorCardBorder))
                {
                    e.Graphics.DrawLine(pen, 0, 0, pnl.Width, 0);
                }
            };

            lblStatus = new Label
            {
                Text = I18n.T("Ready. Add media files and click 'Start Transcription'.", "جاهز. أضف الملفات واضغط على 'بدء التفريغ والترجمة'."),
                ForeColor = colorTextMuted,
                AutoSize = true,
                Location = new Point(20, 15),
                Font = new Font("Segoe UI", 9f)
            };

            prgActive = new ProgressBar
            {
                Width = 420,
                Height = 12,
                Location = new Point(20, 42),
                Style = ProgressBarStyle.Continuous
            };

            btnStart = new Button
            {
                Text = I18n.T("🚀 Start Transcription", "🚀 بدء التفريغ والترجمة"),
                Width = 200,
                Height = 44,
                Location = new Point(660, 16),
                FlatStyle = FlatStyle.Flat,
                BackColor = colorPrimary,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnStart.FlatAppearance.BorderSize = 0;
            btnStart.Click += BtnStart_Click;

            btnCancel = new Button
            {
                Text = I18n.T("⏹️ Cancel", "⏹️ إلغاء"),
                Width = 100,
                Height = 44,
                Location = new Point(870, 16),
                FlatStyle = FlatStyle.Flat,
                BackColor = colorDanger,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Enabled = false
            };
            btnCancel.FlatAppearance.BorderSize = 0;
            btnCancel.Click += BtnCancel_Click;

            pnl.Controls.Add(lblStatus);
            pnl.Controls.Add(prgActive);
            pnl.Controls.Add(btnStart);
            pnl.Controls.Add(btnCancel);

            return pnl;
        }
        #endregion

        #region Execution & Batch Pipeline
        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (isProcessing) return;

            string apiKey = settings.GroqApiKey.Trim();
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show(
                    I18n.T("Please enter your Groq API Key in the field above to start.\nYou can get a free key instantly from the link at the top.", "يرجى إدخال مفتاح Groq API الخاص بك في الحقل بالأعلى للبدء.\nيمكنك الحصول عليه مجاناً فوراً عبر الرابط بأعلى الشاشة."),
                    I18n.T("API Key Required", "مفتاح API مطلوب"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                txtApiKey.Focus();
                return;
            }

            // Find items that need processing
            List<MediaItem> toProcess = new List<MediaItem>();
            foreach (var item in queue)
            {
                if (item.Status != MediaStatus.Completed)
                {
                    toProcess.Add(item);
                }
            }

            if (toProcess.Count == 0)
            {
                MessageBox.Show(
                    I18n.T("No pending files to process. Please add video or audio files first.", "لا توجد ملفات بالانتظار للمعالجة. قم بإضافة ملفات فيديو أو صوت أولاً."),
                    I18n.T("Notice", "تنبيه"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }

            // Check if any item needs FFmpeg
            bool needsFfmpeg = false;
            foreach (var it in toProcess)
            {
                if (!it.IsAudioOnly) { needsFfmpeg = true; break; }
            }

            string ffmpegPath = FFmpegHelper.ResolveFfmpegPath(settings.CustomFfmpegPath);
            if (needsFfmpeg && string.IsNullOrEmpty(ffmpegPath))
            {
                var dr = MessageBox.Show(
                    I18n.T("The queue includes video files that require FFmpeg audio extraction, but FFmpeg was not found on your system.\n\nWould you like to automatically download it locally now?", "تتضمن القائمة ملفات فيديو تحتاج إلى استخراج الصوت عبر FFmpeg، ولكن FFmpeg غير مثبت على جهازك.\n\nهل ترغب في تنزيله محلياً تلقائياً الآن؟"),
                    I18n.T("FFmpeg Required", "FFmpeg مطلوب"),
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );

                if (dr == DialogResult.Yes)
                {
                    BtnFfmpegAction_Click(null, null);
                }
                return;
            }

            // Set UI State to Processing
            isProcessing = true;
            btnStart.Enabled = false;
            btnCancel.Enabled = true;
            btnAddFiles.Enabled = false;
            btnAddFolder.Enabled = false;
            btnClear.Enabled = false;
            prgActive.Value = 0;
            cts = new CancellationTokenSource();

            int completedInThisRun = 0;

            try
            {
                for (int i = 0; i < toProcess.Count; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;

                    var item = toProcess[i];
                    int rowIndex = queue.IndexOf(item);

                    lblStatus.Text = string.Format(
                        I18n.T("Processing file ({0} of {1}): {2}", "جاري معالجة الملف ({0} من {1}): {2}"),
                        i + 1,
                        toProcess.Count,
                        item.FileName
                    );
                    prgActive.Value = (int)(((double)i / toProcess.Count) * 100);

                    string audioToSend = null;
                    bool isTemporaryAudio = false;

                    try
                    {
                        // Phase 1: Prepare audio
                        if (item.IsAudioOnly && item.FileSizeBytes <= 25 * 1024 * 1024)
                        {
                            audioToSend = item.FilePath;
                        }
                        else
                        {
                            item.Status = MediaStatus.ExtractingAudio;
                            item.StatusMessage = I18n.T("🎵 Extracting & compressing audio (FFmpeg)...", "🎵 جاري استخراج وضغط الصوت (FFmpeg)...");
                            UpdateGridRowStatus(rowIndex, item.StatusMessage, Color.FromArgb(129, 140, 248));

                            audioToSend = await FFmpegHelper.ExtractAudioAsync(
                                ffmpegPath,
                                item.FilePath,
                                cts.Token,
                                delegate(string msg) { }
                            );
                            isTemporaryAudio = true;
                        }

                        // Phase 2: Transcribe via Groq Whisper API
                        item.Status = MediaStatus.Transcribing;
                        item.StatusMessage = I18n.T("⚡ Transcribing speech to text (Groq AI)...", "⚡ جاري التفريغ وتحويل الكلام إلى نص (Groq AI)...");
                        UpdateGridRowStatus(rowIndex, item.StatusMessage, Color.FromArgb(129, 140, 248));

                        string srtContent = await GroqClient.TranscribeAsync(
                            apiKey,
                            audioToSend,
                            settings.Model,
                            settings.Language,
                            cts.Token,
                            delegate(string stepMsg)
                            {
                                this.BeginInvoke(new Action(delegate
                                {
                                    item.StatusMessage = stepMsg;
                                    UpdateGridRowStatus(rowIndex, stepMsg, Color.FromArgb(129, 140, 248));
                                }));
                            }
                        );

                        // Phase 3: Save SRT file
                        string outputDir = settings.SaveAlongsideMedia
                            ? Path.GetDirectoryName(item.FilePath)
                            : (string.IsNullOrEmpty(settings.CustomOutputDir) ? Path.GetDirectoryName(item.FilePath) : settings.CustomOutputDir);

                        if (!Directory.Exists(outputDir)) Directory.CreateDirectory(outputDir);

                        string baseName = Path.GetFileNameWithoutExtension(item.FilePath);
                        string srtPath = Path.Combine(outputDir, baseName + ".srt");

                        File.WriteAllText(srtPath, srtContent, Encoding.UTF8);

                        item.OutputSrtPath = srtPath;
                        item.Status = MediaStatus.Completed;
                        item.StatusMessage = I18n.T("✅ Completed - SRT created", "✅ تم بنجاح - تم إنشاء ملف SRT");
                        UpdateGridRowStatus(rowIndex, item.StatusMessage, colorSuccess);

                        completedInThisRun++;
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = MediaStatus.Cancelled;
                        item.StatusMessage = I18n.T("⏹️ Cancelled", "⏹️ تم الإلغاء");
                        UpdateGridRowStatus(rowIndex, item.StatusMessage, Color.FromArgb(251, 191, 36));
                        break;
                    }
                    catch (Exception ex)
                    {
                        item.Status = MediaStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        item.StatusMessage = I18n.T("❌ Error: ", "❌ خطأ: ") + ex.Message;
                        UpdateGridRowStatus(rowIndex, item.StatusMessage, colorDanger);
                    }
                    finally
                    {
                        if (isTemporaryAudio && !string.IsNullOrEmpty(audioToSend))
                        {
                            try { if (File.Exists(audioToSend)) File.Delete(audioToSend); } catch { }
                        }
                    }
                }

                prgActive.Value = 100;
                lblStatus.Text = string.Format(
                    I18n.T("Process completed! Successfully transcribed {0} file(s).", "اكتملت العملية! تم تفريغ {0} ملف(ات) بنجاح."),
                    completedInThisRun
                );
            }
            finally
            {
                isProcessing = false;
                btnStart.Enabled = true;
                btnCancel.Enabled = false;
                btnAddFiles.Enabled = true;
                btnAddFolder.Enabled = true;
                btnClear.Enabled = true;
                if (cts != null) { cts.Dispose(); cts = null; }
                RefreshGrid();
            }
        }

        private void BtnCancel_Click(object sender, EventArgs e)
        {
            if (cts != null && !cts.IsCancellationRequested)
            {
                btnCancel.Enabled = false;
                lblStatus.Text = I18n.T("Aborting active tasks...", "جاري إيقاف وإلغاء العمليات الحالية...");
                cts.Cancel();
            }
        }

        private void UpdateGridRowStatus(int rowIndex, string message, Color color)
        {
            if (rowIndex >= 0 && rowIndex < grid.Rows.Count)
            {
                var cell = grid.Rows[rowIndex].Cells[3];
                cell.Value = message;
                cell.Style.ForeColor = color;
            }
        }
        #endregion
    }
    #endregion
}
