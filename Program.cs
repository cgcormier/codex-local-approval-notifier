using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexApprovalTray
{
    internal static class Program
    {
        internal const string AppName = "Codex Approval Tray";
        internal static readonly string Sid = WindowsIdentity.GetCurrent().User.Value;
        internal static readonly string PipeName = "CodexApprovalTray-" + Sid;
        internal static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 4194304 };

        [STAThread]
        private static int Main(string[] args)
        {
            // Hook errors must never block Codex or change an approval decision.
            if (args.Length > 0 && args[0] == "--hook")
            {
                try
                {
                    var read = Task.Run(() => ReadBounded(Console.In, 4194304));
                    if (read.Wait(700))
                    {
                        var message = Message.FromHook(read.Result);
                        if (message != null) Send(message, PipeName, false);
                    }
                }
                catch { }
                Console.WriteLine("{}");
                return 0;
            }
            if (args.Length > 0 && new[] { "--test", "--status", "--exit", "--pause", "--resume", "--dismiss-all" }.Contains(args[0]))
            {
                string reply = Send(new Message { Kind = args[0].Substring(2) }, PipeName, true);
                Console.WriteLine(reply ?? "{\"running\":false}");
                return reply == null ? 1 : 0;
            }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--self-test") return SelfTests.Run(args[1]);
            bool first;
            using (var mutex = new Mutex(true, "Local\\" + PipeName, out first))
            {
                if (!first) { Send(new Message { Kind = "show" }, PipeName, false); return 0; }
                Application.Run(new TrayContext());
            }
            return 0;
        }

        internal static string ReadBounded(TextReader reader, int max)
        {
            var chars = new char[4096];
            var result = new System.Text.StringBuilder();
            int count;
            while ((count = reader.Read(chars, 0, chars.Length)) != 0)
            {
                if (result.Length + count > max) throw new InvalidDataException("Input too large");
                result.Append(chars, 0, count);
            }
            return result.ToString();
        }

        internal static string Send(Message message, string pipeName, bool reply)
        {
            string result = null;
            var operation = Task.Run(() =>
            {
                try
                {
                    using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                    {
                        pipe.Connect(200);
                        using (var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, true))
                        using (var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, true))
                        {
                            writer.WriteLine(new JavaScriptSerializer().Serialize(message));
                            writer.Flush();
                            if (reply) result = reader.ReadLine();
                        }
                    }
                }
                catch { }
            });
            operation.Wait(reply ? 1200 : 450);
            return result;
        }
    }

    public sealed class Message
    {
        public string Kind { get; set; }
        public string Session { get; set; }
        public string Turn { get; set; }
        public string Folder { get; set; }
        public string Tool { get; set; }
        public long Stamp { get; set; }

        internal static Message FromHook(string json)
        {
            var obj = Program.Json.Deserialize<Dictionary<string, object>>(json);
            string kind = Value(obj, "hook_event_name", 50);
            string session = Value(obj, "session_id", 200);
            if (String.IsNullOrWhiteSpace(session) || !new[] { "PermissionRequest", "Stop", "Interrupt", "SessionEnd" }.Contains(kind)) return null;
            return new Message { Kind = kind, Session = session, Turn = Value(obj, "turn_id", 200),
                Folder = Value(obj, "cwd", 1024), Tool = Value(obj, "tool_name", 120), Stamp = DateTime.UtcNow.Ticks };
        }

        private static string Value(Dictionary<string, object> obj, string key, int max)
        {
            object value;
            if (obj == null || !obj.TryGetValue(key, out value) || !(value is string)) return "";
            var clean = new string(((string)value).Where(c => !Char.IsControl(c)).ToArray());
            return clean.Substring(0, Math.Min(clean.Length, max));
        }

        internal bool ValidHook()
        {
            return !String.IsNullOrWhiteSpace(Session) && Session.Length <= 200 && (Turn ?? "").Length <= 200 &&
                (Folder ?? "").Length <= 1024 && (Tool ?? "").Length <= 120 && Stamp > 0 &&
                new[] { "PermissionRequest", "Stop", "Interrupt", "SessionEnd" }.Contains(Kind);
        }
    }

    internal sealed class Notice
    {
        internal Message Message;
        internal string Title
        {
            get
            {
                string folder = (Message.Folder ?? "").TrimEnd('\\', '/');
                int last = Math.Max(folder.LastIndexOf('\\'), folder.LastIndexOf('/'));
                string name = last >= 0 ? folder.Substring(last + 1) : folder;
                if (String.IsNullOrWhiteSpace(name)) name = "Codex session";
                string session = Message.Session;
                return name + "  ·  " + session.Substring(Math.Max(0, session.Length - 8));
            }
        }
    }

    internal sealed class AlertState
    {
        internal readonly Dictionary<string, Notice> Notices = new Dictionary<string, Notice>();
        private readonly Dictionary<string, long> cleared = new Dictionary<string, long>();

        private static string Key(string session, string turn) { return session + "\n" + (turn ?? ""); }

        internal bool Apply(Message message)
        {
            if (!message.ValidHook()) return false;
            string key = Key(message.Session, message.Turn);
            if (message.Kind != "PermissionRequest")
            {
                string clearKey = message.Kind == "SessionEnd" ? Key(message.Session, "*") : key;
                long existing;
                cleared[clearKey] = cleared.TryGetValue(clearKey, out existing) ? Math.Max(existing, message.Stamp) : message.Stamp;
                foreach (string item in Notices.Where(p => p.Value.Message.Session == message.Session &&
                    (message.Kind == "SessionEnd" || p.Value.Message.Turn == message.Turn) &&
                    p.Value.Message.Stamp <= message.Stamp).Select(p => p.Key).ToArray()) Notices.Remove(item);
                // Bounded in-memory tombstones prevent delayed notices from resurrecting closed turns.
                if (cleared.Count > 1024) cleared.Remove(cleared.OrderBy(p => p.Value).First().Key);
                return false;
            }
            long stamp;
            if ((cleared.TryGetValue(key, out stamp) && stamp >= message.Stamp) ||
                (cleared.TryGetValue(Key(message.Session, "*"), out stamp) && stamp >= message.Stamp)) return false;
            Notice old;
            if (Notices.TryGetValue(key, out old) && old.Message.Stamp >= message.Stamp) return false;
            Notices[key] = new Notice { Message = message };
            if (Notices.Count > 100) Notices.Remove(Notices.OrderBy(p => p.Value.Message.Stamp).First().Key);
            return true;
        }

        internal void Dismiss(string key)
        {
            Notice notice;
            if (Notices.TryGetValue(key, out notice))
                Apply(new Message { Kind = "Stop", Session = notice.Message.Session, Turn = notice.Message.Turn, Stamp = DateTime.UtcNow.Ticks });
        }
        internal void DismissAll() { foreach (string key in Notices.Keys.ToArray()) Dismiss(key); }
    }

    internal sealed class PipeListener : IDisposable
    {
        private readonly string name;
        private readonly Func<Message, string> receive;
        private volatile bool stopped;
        private readonly List<NamedPipeServerStream> active = new List<NamedPipeServerStream>();
        internal static PipeSecurity Security()
        {
            var security = new PipeSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(Program.Sid), PipeAccessRights.FullControl, AccessControlType.Allow));
            // Reject network logons, including this same user connecting from another machine.
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            return security;
        }
        internal PipeListener(string name, Func<Message, string> receive)
        {
            this.name = name; this.receive = receive;
            for (int i = 0; i < 4; i++)
                new Thread(Listen) { IsBackground = true, Name = "Codex approval pipe " + i }.Start();
        }
        private void Listen()
        {
            while (!stopped)
            {
                try
                {
                    using (var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 4096, 4096, Security()))
                    {
                        lock (active) { if (stopped) break; active.Add(pipe); }
                        try
                        {
                            pipe.WaitForConnection();
                            using (var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, true))
                            using (var writer = new StreamWriter(pipe, new System.Text.UTF8Encoding(false), 1024, true))
                            {
                                // Read one small frame, bounded in time and size even for a broken client.
                                var read = ReadFrame(reader);
                                if (!read.Wait(400)) continue;
                                var message = new JavaScriptSerializer().Deserialize<Message>(read.Result);
                                string reply = message == null ? "{}" : receive(message);
                                writer.WriteLine(reply ?? "{}");
                                var flush = writer.FlushAsync();
                                flush.Wait(200);
                            }
                        }
                        finally { lock (active) active.Remove(pipe); }
                    }
                }
                catch { if (!stopped) Thread.Sleep(25); }
            }
        }
        private static async Task<string> ReadFrame(TextReader reader)
        {
            var result = new System.Text.StringBuilder();
            var ch = new char[1];
            while (await reader.ReadAsync(ch, 0, 1) != 0)
            {
                if (ch[0] == '\n') return result.ToString();
                if (result.Length >= 8192) throw new InvalidDataException("Frame too large");
                result.Append(ch[0]);
            }
            return result.ToString();
        }
        public void Dispose()
        {
            lock (active) { stopped = true; foreach (var pipe in active.ToArray()) pipe.Dispose(); }
        }
    }

    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(24, 29, 36);
        internal static readonly Color Muted = Color.FromArgb(174, 186, 199);
        internal static readonly Color Amber = Color.FromArgb(250, 195, 87);
        internal static Label Label(string text, int x, int y, int w, int h, float size, Color color)
        {
            return new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = color,
                Font = new Font("Segoe UI", size), AutoEllipsis = true, UseMnemonic = false };
        }
        internal static Button Button(string text, int x, int y, int w, EventHandler action)
        {
            var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, 32),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(43, 52, 63), ForeColor = Color.White,
                Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9) };
            button.FlatAppearance.BorderColor = Color.FromArgb(80, 95, 110);
            button.Click += action;
            return button;
        }
        internal static Icon Icon(Color color)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(bitmap))
            using (var pen = new Pen(color, 3))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.DrawLines(pen, new[] { new Point(11, 8), new Point(4, 16), new Point(11, 24) });
                g.DrawLines(pen, new[] { new Point(21, 8), new Point(28, 16), new Point(21, 24) });
                g.DrawLine(pen, 18, 7, 14, 25);
                IntPtr handle = bitmap.GetHicon();
                try { using (var icon = System.Drawing.Icon.FromHandle(handle)) return (Icon)icon.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr icon);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        internal static void DarkCaption(Form form)
        {
            try { int enabled = 1; DwmSetWindowAttribute(form.Handle, 20, ref enabled, 4); } catch { }
        }
    }

    internal sealed class Popup : Form
    {
        private readonly System.Windows.Forms.Timer timer;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x08000000 | 0x00000080; return p; }
        }
        internal Popup(string title, string detail, Action dismiss, Action view)
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Theme.Background; ForeColor = Color.White;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            ClientSize = new Size(394, 164); StartPosition = FormStartPosition.Manual;
            AccessibleName = "Codex approval notification";
            Controls.Add(Theme.Label("CODEX  /  APPROVAL NEEDED", 18, 14, 355, 24, 9, Theme.Amber));
            Controls.Add(Theme.Label(title, 18, 43, 355, 30, 13, Color.White));
            Controls.Add(Theme.Label(detail, 18, 80, 355, 24, 9, Theme.Muted));
            Controls.Add(Theme.Button("View alerts", 18, 116, 113, (s, e) => { Close(); view(); }));
            Controls.Add(Theme.Button("Dismiss", 267, 116, 109, (s, e) => { Close(); dismiss(); }));
            timer = new System.Windows.Forms.Timer { Interval = 9000 };
            timer.Tick += (s, e) => Close();
            Shown += (s, e) =>
            {
                Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
                Location = new Point(area.Right - Width - 16, area.Bottom - Height - 16);
                timer.Start();
            };
            FormClosed += (s, e) => timer.Dispose();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(81, 96, 111))) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class AlertCenter : Form
    {
        private readonly ListBox list;
        private readonly Label path;
        private readonly Label hint;
        private readonly AlertState state;
        private readonly Action changed;
        internal AlertCenter(AlertState state, Action changed)
        {
            this.state = state; this.changed = changed;
            Text = Program.AppName; AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(492, 350); FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false; MinimizeBox = false; BackColor = Theme.Background;
            ForeColor = Color.White; Font = new Font("Segoe UI", 10); KeyPreview = true;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.Icon(Color.FromArgb(115, 220, 196));
            HandleCreated += (s, e) => Theme.DarkCaption(this);
            Controls.Add(Theme.Label("Approval alerts", 18, 17, 450, 32, 17, Color.White));
            hint = Theme.Label("Review approvals in your Codex window.", 18, 55, 455, 25, 9, Theme.Muted);
            Controls.Add(hint);
            list = new ListBox { Location = new Point(18, 88), Size = new Size(456, 132), BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(34, 41, 50), ForeColor = Color.White, Font = new Font("Segoe UI", 11),
                DisplayMember = "Text", IntegralHeight = false, AccessibleName = "Sessions with approval alerts" };
            list.SelectedIndexChanged += (s, e) => UpdatePath();
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 28;
            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool selected = (e.State & DrawItemState.Selected) != 0;
                using (var brush = new SolidBrush(selected ? Color.FromArgb(43, 80, 78) : list.BackColor)) e.Graphics.FillRectangle(brush, e.Bounds);
                var row = (Row)list.Items[e.Index];
                TextRenderer.DrawText(e.Graphics, row.Text, list.Font, new Rectangle(e.Bounds.X + 7, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height),
                    Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                e.DrawFocusRectangle();
            };
            Controls.Add(list);
            path = Theme.Label("", 18, 233, 456, 48, 9, Theme.Muted); Controls.Add(path);
            Controls.Add(Theme.Button("Dismiss", 18, 298, 142, (s, e) => DismissSelected()));
            Controls.Add(Theme.Button("Dismiss all", 172, 298, 116, (s, e) => { state.DismissAll(); changed(); RefreshNotices(); }));
            Controls.Add(Theme.Button("Close", 376, 298, 98, (s, e) => Close()));
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
                if (e.KeyCode == Keys.Delete) { e.Handled = true; DismissSelected(); }
            };
            RefreshNotices();
        }
        internal sealed class Row { public string Text { get; set; } public string Key; public Notice Notice; }
        internal void RefreshNotices()
        {
            int selected = Math.Max(0, list.SelectedIndex);
            list.BeginUpdate(); list.Items.Clear();
            foreach (var pair in state.Notices.OrderByDescending(p => p.Value.Message.Stamp))
                list.Items.Add(new Row { Text = pair.Value.Title, Key = pair.Key, Notice = pair.Value });
            list.EndUpdate();
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(selected, list.Items.Count - 1);
            hint.Text = list.Items.Count == 0 ? "All caught up. New approval requests will appear here." : "Approve in Codex. Delete dismisses an alert.";
            UpdatePath();
        }
        private void UpdatePath()
        {
            var row = list.SelectedItem as Row;
            path.Text = row == null ? "" : row.Notice.Message.Folder + "\n" + row.Notice.Message.Tool + "  ·  " + row.Notice.Message.Session;
        }
        private void DismissSelected()
        {
            var row = list.SelectedItem as Row;
            if (row != null) { state.Dismiss(row.Key); changed(); RefreshNotices(); }
        }
    }

    internal sealed class TrayContext : ApplicationContext
    {
        internal readonly AlertState State = new AlertState();
        private readonly Control dispatch = new Control();
        private readonly NotifyIcon tray;
        private readonly PipeListener pipe;
        private readonly Icon idleIcon = Theme.Icon(Color.FromArgb(115, 220, 196));
        private readonly Icon alertIcon = Theme.Icon(Theme.Amber);
        private readonly Icon pausedIcon = Theme.Icon(Color.FromArgb(150, 157, 165));
        private readonly ToolStripMenuItem pauseItem;
        private readonly ToolStripMenuItem showItem;
        private Popup popup;
        private string popupKey;
        private AlertCenter center;
        private bool paused;
        private bool exiting;
        private readonly string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexApprovalTray", "paused.txt");

        internal TrayContext()
        {
            dispatch.CreateControl();
            paused = File.Exists(settings) && File.ReadAllText(settings).Trim() == "true";
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem(Program.AppName) { Enabled = false });
            showItem = new ToolStripMenuItem("Show alerts", null, (s, e) => ShowCenter()); menu.Items.Add(showItem);
            pauseItem = new ToolStripMenuItem("Pause notifications", null, (s, e) => TogglePause()) { Checked = paused }; menu.Items.Add(pauseItem);
            var startup = new ToolStripMenuItem("Start with Windows") { Checked = StartupEnabled() };
            startup.Click += (s, e) =>
            {
                try { SetStartup(!startup.Checked); startup.Checked = StartupEnabled(); }
                catch { MessageBox.Show("Windows could not update the startup setting.", Program.AppName); }
            };
            menu.Items.Add(startup); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Send test notification", null, (s, e) => TestPopup());
            menu.Items.Add("Dismiss all", null, (s, e) => { State.DismissAll(); ClosePopup(); UpdateUi(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitThread());
            tray = new NotifyIcon { Icon = idleIcon, Text = Program.AppName, ContextMenuStrip = menu, Visible = true };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowCenter(); };
            pipe = new PipeListener(Program.PipeName, Receive);
            UpdateUi();
        }
        private string Receive(Message message)
        {
            if (exiting) return "{}";
            if (message.Kind == "status")
                return (string)dispatch.Invoke(new Func<string>(() => new JavaScriptSerializer().Serialize(new { running = true, paused = paused, alerts = State.Notices.Count, version = "1.0" })));
            dispatch.BeginInvoke(new Action(() =>
            {
                if (exiting) return;
                if (message.Kind == "exit") { ExitThread(); return; }
                if (message.Kind == "show") { ShowCenter(); return; }
                if (message.Kind == "test") { TestPopup(); return; }
                if (message.Kind == "pause" || message.Kind == "resume")
                { if (paused != (message.Kind == "pause")) TogglePause(); return; }
                if (message.Kind == "dismiss-all") { State.DismissAll(); ClosePopup(); UpdateUi(); return; }
                bool added = State.Apply(message);
                if (popupKey != null && !State.Notices.ContainsKey(popupKey)) ClosePopup();
                UpdateUi();
                if (added && !paused) ShowPopup();
            }));
            return "{\"running\":true}";
        }
        private void ShowCenter()
        {
            ClosePopup();
            if (center == null || center.IsDisposed) center = new AlertCenter(State, () => { ClosePopup(); UpdateUi(); });
            center.RefreshNotices(); center.Show(); center.Activate();
        }
        private void UpdateUi()
        {
            int count = State.Notices.Count;
            tray.Icon = paused ? pausedIcon : count > 0 ? alertIcon : idleIcon;
            tray.Text = Program.AppName + (paused ? " — paused" : count == 0 ? " — listening" : " — " + count + " alert(s)");
            showItem.Text = count == 0 ? "Show alerts" : "Show alerts (" + count + ")";
            if (center != null && !center.IsDisposed) center.RefreshNotices();
        }
        private void TogglePause()
        {
            paused = !paused; pauseItem.Checked = paused; ClosePopup();
            try { Directory.CreateDirectory(Path.GetDirectoryName(settings)); File.WriteAllText(settings, paused ? "true" : "false"); }
            catch { }
            UpdateUi(); if (!paused && State.Notices.Count > 0) ShowPopup();
        }
        private void ShowPopup()
        {
            ClosePopup();
            var latest = State.Notices.OrderByDescending(p => p.Value.Message.Stamp).First();
            string detail = State.Notices.Count > 1 ? State.Notices.Count + " session alerts · open the list to review" : "Approval requested for " + latest.Value.Message.Tool;
            string key = latest.Key;
            popupKey = key;
            popup = new Popup(latest.Value.Title, detail, () => { State.Dismiss(key); UpdateUi(); }, ShowCenter);
            popup.Show();
        }
        private void TestPopup()
        {
            ClosePopup();
            popup = new Popup("You're ready for the next request", "Test notification · no approval is pending", () => { }, ShowCenter);
            popup.Show();
        }
        private void ClosePopup() { if (popup != null && !popup.IsDisposed) popup.Close(); popup = null; popupKey = null; }
        internal static bool StartupEnabled()
        {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key != null && key.GetValue("CodexApprovalTray") != null;
        }
        internal static void SetStartup(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (enabled) key.SetValue("CodexApprovalTray", "\"" + Application.ExecutablePath + "\"");
                else key.DeleteValue("CodexApprovalTray", false);
            }
        }
        protected override void ExitThreadCore()
        {
            exiting = true; pipe.Dispose(); ClosePopup();
            if (center != null) center.Dispose();
            tray.Visible = false; tray.ContextMenuStrip.Dispose(); tray.Dispose();
            idleIcon.Dispose(); alertIcon.Dispose(); pausedIcon.Dispose(); dispatch.Dispose();
            base.ExitThreadCore();
        }
    }

    internal static class SelfTests
    {
        private static void Check(bool condition, string name, List<string> passed)
        { if (!condition) throw new Exception(name); passed.Add(name); }
        internal static int Run(string output)
        {
            var passed = new List<string>();
            try
            {
                var state = new AlertState();
                long tick = DateTime.UtcNow.Ticks;
                var a = new Message { Kind = "PermissionRequest", Session = "session-A", Turn = "turn-A", Folder = @"C:\same", Tool = "Bash", Stamp = tick };
                var b = new Message { Kind = "PermissionRequest", Session = "session-B", Turn = "turn-B", Folder = @"C:\same", Tool = "Bash", Stamp = tick };
                state.Apply(a); state.Apply(b);
                Check(state.Notices.Count == 2, "same-folder sessions stay independent", passed);
                state.Apply(new Message { Kind = "Stop", Session = a.Session, Turn = a.Turn, Stamp = tick + 1 });
                Check(state.Notices.Count == 1, "turn cleanup preserves other sessions", passed);
                Check(!state.Apply(a) && state.Notices.Count == 1, "delayed permission cannot resurrect a cleared turn", passed);
                state.Apply(new Message { Kind = "PermissionRequest", Session = b.Session, Turn = "next", Folder = b.Folder, Tool = "apply_patch", Stamp = tick + 2 });
                state.Apply(new Message { Kind = "SessionEnd", Session = b.Session, Stamp = tick + 3 });
                Check(state.Notices.Count == 0 && !state.Apply(b), "session-end clears all turns and rejects old delivery", passed);
                state.Apply(new Message { Kind = "Stop", Session = a.Session, Turn = a.Turn, Stamp = tick });
                Check(!state.Apply(a), "older cleanup cannot replace newer tombstone", passed);
                Check(Message.FromHook("{\"hook_event_name\":\"PermissionRequest\"}") == null, "missing session ignored", passed);
                Check(Message.FromHook("{\"session_id\":\"x\",\"hook_event_name\":\"PostToolUse\"}") == null, "unsupported events ignored", passed);
                string reduced = Program.Json.Serialize(Message.FromHook("{\"session_id\":\"x\",\"hook_event_name\":\"PermissionRequest\",\"tool_input\":{\"command\":\"PRIVATE_COMMAND\"}}"));
                Check(!reduced.Contains("PRIVATE_COMMAND") && !reduced.Contains("tool_input"), "commands and tool input never forwarded", passed);
                bool bounded = false; try { Program.ReadBounded(new StringReader("12345"), 4); } catch (InvalidDataException) { bounded = true; }
                Check(bounded, "input size bounded", passed);
                state.Apply(new Message { Kind = "PermissionRequest", Session = "dismiss", Turn = "turn", Stamp = tick });
                state.DismissAll(); Check(state.Notices.Count == 0, "dismiss all", passed);
                var rules = PipeListener.Security().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToArray();
                Check(rules.Count(r => r.AccessControlType == AccessControlType.Allow) == 1 && rules.Any(r => r.IdentityReference.Value == Program.Sid && r.AccessControlType == AccessControlType.Allow) && rules.Any(r => r.AccessControlType == AccessControlType.Deny), "pipe ACL allows current user and denies network logons", passed);
                int received = 0;
                string pipeName = Program.PipeName + "-test-" + Guid.NewGuid().ToString("N");
                using (var pipe = new PipeListener(pipeName, m => { Interlocked.Increment(ref received); return "{\"ok\":true}"; }))
                {
                    // Retry only the test listener's startup race; production hooks never retry.
                    string reply = null;
                    for (int i = 0; i < 3 && reply == null; i++) reply = Program.Send(a, pipeName, true);
                    Check(reply == "{\"ok\":true}" && received > 0, "real named-pipe delivery and reply", passed);
                }
                state = new AlertState(); state.Apply(a); state.Apply(b);
                using (var center = new AlertCenter(state, () => { }))
                using (var popup = new Popup("same  ·  session-A", "Approval requested for Bash", () => { }, () => { }))
                using (var image = new Bitmap(920, 410))
                {
                    center.ShowInTaskbar = false; center.Opacity = 0; popup.Opacity = 0;
                    center.Show(); popup.Show();
                    Application.DoEvents();
                    using (var g = Graphics.FromImage(image)) g.Clear(Color.FromArgb(14, 18, 23));
                    center.DrawToBitmap(image, new Rectangle(12, 12, center.Width, center.Height));
                    popup.DrawToBitmap(image, new Rectangle(520, 12, popup.Width, popup.Height));
                    image.Save(Path.ChangeExtension(output, ".png"));
                    Check(center.Controls.OfType<ListBox>().Single().Items.Count == 2, "native alert center renders both sessions", passed);
                }
                File.WriteAllText(output, Program.Json.Serialize(new { passed = passed.ToArray() }));
                return 0;
            }
            catch (Exception e) { File.WriteAllText(output, Program.Json.Serialize(new { passed = passed.ToArray(), error = e.ToString() })); return 1; }
        }
    }
}
