using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Windows.Foundation;
using Windows.Media.Control;

namespace Pauto
{
    internal sealed class SessionSnapshot
    {
        public string AppId { get; private set; }
        public string Status { get; private set; }
        public SessionKind Kind { get; private set; }

        public SessionSnapshot(string appId, string status, SessionKind kind)
        {
            AppId = appId;
            Status = status;
            Kind = kind;
        }
    }

    internal sealed class MediaStateSnapshot
    {
        public bool AnyBrowserPlaying { get; private set; }
        public bool SpotifastPlaying { get; private set; }
        public bool SpotifastExists { get; private set; }

        public MediaStateSnapshot(bool anyBrowserPlaying, bool spotifastPlaying, bool spotifastExists)
        {
            AnyBrowserPlaying = anyBrowserPlaying;
            SpotifastPlaying = spotifastPlaying;
            SpotifastExists = spotifastExists;
        }
    }

    internal sealed class SmtcBridge : IDisposable
    {
        private readonly Action _onChanged;
        private readonly object _sessionsLock = new object();
        private readonly List<GlobalSystemMediaTransportControlsSession> _trackedSessions =
            new List<GlobalSystemMediaTransportControlsSession>();

        private GlobalSystemMediaTransportControlsSessionManager _manager;
        private bool _disposed;

        public SmtcBridge(Action onChanged)
        {
            _onChanged = onChanged;
        }

        private static Task<T> ToTask<T>(IAsyncOperation<T> operation)
        {
            var tcs = new TaskCompletionSource<T>();
            operation.Completed = delegate(IAsyncOperation<T> asyncInfo, AsyncStatus status)
            {
                try
                {
                    if (status == AsyncStatus.Completed)
                    {
                        tcs.TrySetResult(asyncInfo.GetResults());
                    }
                    else if (status == AsyncStatus.Canceled)
                    {
                        tcs.TrySetCanceled();
                    }
                    else
                    {
                        tcs.TrySetException(asyncInfo.ErrorCode ?? new InvalidOperationException("WinRT async operation failed: " + status));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            };
            return tcs.Task;
        }

        public async Task InitializeAsync()
        {
            _manager = await ToTask(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            if (_disposed)
            {
                return;
            }

            _manager.SessionsChanged += OnSessionsChanged;
            RefreshTrackedSessions();
        }

        public MediaStateSnapshot CaptureState()
        {
            if (_manager == null || _disposed)
            {
                return new MediaStateSnapshot(false, false, false);
            }

            bool anyBrowserPlaying = false;
            bool spotifastPlaying = false;
            bool spotifastExists = false;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = SafeGetSessions();
            for (int i = 0; i < sessions.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession session = sessions[i];
                SessionKind kind = SessionClassifier.Classify(SafeGetAppId(session));
                bool isPlaying = IsPlaying(session);

                if (kind == SessionKind.Browser && isPlaying)
                {
                    anyBrowserPlaying = true;
                }
                else if (kind == SessionKind.Spotifast)
                {
                    spotifastExists = true;
                    if (isPlaying)
                    {
                        spotifastPlaying = true;
                    }
                }
            }

            return new MediaStateSnapshot(anyBrowserPlaying, spotifastPlaying, spotifastExists);
        }

        public List<SessionSnapshot> GetSnapshots()
        {
            var result = new List<SessionSnapshot>();
            if (_manager == null || _disposed)
            {
                return result;
            }

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = SafeGetSessions();
            for (int i = 0; i < sessions.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession session = sessions[i];
                string appId = SafeGetAppId(session);
                SessionKind kind = SessionClassifier.Classify(appId);
                string status = SafeGetPlaybackStatusText(session);
                result.Add(new SessionSnapshot(appId, status, kind));
            }

            return result;
        }

        public async Task<bool> PauseSpotifastAsync()
        {
            GlobalSystemMediaTransportControlsSession session = FindSpotifastSession(true);
            if (session == null)
            {
                return false;
            }

            try
            {
                bool paused = await ToTask(session.TryPauseAsync());
                if (!paused && IsPlaying(session))
                {
                    paused = await ToTask(session.TryTogglePlayPauseAsync());
                }

                return paused;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        public async Task<bool> ResumeSpotifastAsync()
        {
            GlobalSystemMediaTransportControlsSession session = FindSpotifastSession(false);
            if (session == null)
            {
                return false;
            }

            try
            {
                if (IsPlaying(session))
                {
                    return true;
                }

                bool resumed = await ToTask(session.TryPlayAsync());
                if (!resumed && !IsPlaying(session))
                {
                    resumed = await ToTask(session.TryTogglePlayPauseAsync());
                }

                return resumed;
            }
            catch (COMException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private GlobalSystemMediaTransportControlsSession FindSpotifastSession(bool preferPlaying)
        {
            if (_manager == null || _disposed)
            {
                return null;
            }

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = SafeGetSessions();
            GlobalSystemMediaTransportControlsSession fallback = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession session = sessions[i];
                if (SessionClassifier.Classify(SafeGetAppId(session)) != SessionKind.Spotifast)
                {
                    continue;
                }

                if (preferPlaying && IsPlaying(session))
                {
                    return session;
                }

                if (fallback == null)
                {
                    fallback = session;
                }
            }

            return fallback;
        }

        private IReadOnlyList<GlobalSystemMediaTransportControlsSession> SafeGetSessions()
        {
            try
            {
                return _manager.GetSessions();
            }
            catch (COMException)
            {
                return new List<GlobalSystemMediaTransportControlsSession>();
            }
            catch (InvalidOperationException)
            {
                return new List<GlobalSystemMediaTransportControlsSession>();
            }
        }

        private static string SafeGetAppId(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                return session.SourceAppUserModelId ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static bool IsPlaying(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                return info != null &&
                       info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string SafeGetPlaybackStatusText(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = session.GetPlaybackInfo();
                return info != null ? info.PlaybackStatus.ToString() : "Unknown";
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            RefreshTrackedSessions();
            _onChanged();
        }

        private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        {
            if (_disposed)
            {
                return;
            }

            _onChanged();
        }

        private void RefreshTrackedSessions()
        {
            lock (_sessionsLock)
            {
                DetachAllLocked();

                if (_manager == null || _disposed)
                {
                    return;
                }

                IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = SafeGetSessions();
                for (int i = 0; i < sessions.Count; i++)
                {
                    GlobalSystemMediaTransportControlsSession session = sessions[i];
                    try
                    {
                        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                        _trackedSessions.Add(session);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void DetachAllLocked()
        {
            for (int i = 0; i < _trackedSessions.Count; i++)
            {
                try
                {
                    _trackedSessions[i].PlaybackInfoChanged -= OnPlaybackInfoChanged;
                }
                catch (Exception)
                {
                }
            }

            _trackedSessions.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_manager != null)
            {
                try
                {
                    _manager.SessionsChanged -= OnSessionsChanged;
                }
                catch (Exception)
                {
                }
            }

            lock (_sessionsLock)
            {
                DetachAllLocked();
            }
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "pauto";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    object val = key.GetValue(ValueName);
                    return val != null && !string.IsNullOrWhiteSpace(val.ToString());
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, string.Format("\"{0}\"", Application.ExecutablePath));
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    internal static class TrayIconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public static Icon Create(Color fillColor, bool drawPauseBars)
        {
            using (var bmp = new Bitmap(16, 16))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    using (var fill = new SolidBrush(fillColor))
                    using (var border = new Pen(Color.FromArgb(40, 0, 0, 0), 1f))
                    {
                        g.FillEllipse(fill, 1, 1, 13, 13);
                        g.DrawEllipse(border, 1, 1, 13, 13);
                    }

                    using (var glyph = new SolidBrush(Color.White))
                    {
                        if (drawPauseBars)
                        {
                            g.FillRectangle(glyph, 5, 4, 2, 8);
                            g.FillRectangle(glyph, 9, 4, 2, 8);
                        }
                        else
                        {
                            Point[] triangle = new Point[]
                            {
                                new Point(6, 4),
                                new Point(6, 12),
                                new Point(12, 8)
                            };
                            g.FillPolygon(glyph, triangle);
                        }
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                try
                {
                    using (Icon temp = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temp.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly Control _marshalControl;
        private readonly AutoPauseController _controller;
        private readonly SmtcBridge _bridge;
        private readonly System.Windows.Forms.Timer _resumeTimer;
        private readonly System.Windows.Forms.Timer _safetyPollTimer;
        private readonly NotifyIcon _notifyIcon;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _enabledItem;
        private readonly ToolStripMenuItem _startupItem;

        private readonly Icon _activeIcon;
        private readonly Icon _pausedIcon;
        private readonly Icon _disabledIcon;

        private bool _initialized;
        private bool _commandInFlight;
        private bool _exiting;

        public TrayApplicationContext()
        {
            _marshalControl = new Control();
            IntPtr handle = _marshalControl.Handle;
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create UI marshalling handle.");
            }

            _controller = new AutoPauseController();
            _bridge = new SmtcBridge(ScheduleEvaluation);

            _resumeTimer = new System.Windows.Forms.Timer();
            _resumeTimer.Interval = AutoPauseController.ResumeDebounceMs;
            _resumeTimer.Tick += OnResumeTimerTick;

            // Safety fallback while Spotifast is paused by us in case a browser tab crashes without a final SMTC event.
            _safetyPollTimer = new System.Windows.Forms.Timer();
            _safetyPollTimer.Interval = 2000;
            _safetyPollTimer.Tick += OnSafetyPollTick;

            _activeIcon = TrayIconFactory.Create(Color.FromArgb(34, 197, 94), false);
            _pausedIcon = TrayIconFactory.Create(Color.FromArgb(245, 158, 11), true);
            _disabledIcon = TrayIconFactory.Create(Color.FromArgb(107, 114, 128), true);

            _statusItem = new ToolStripMenuItem("Status: Conectando...");
            _statusItem.Enabled = false;

            _enabledItem = new ToolStripMenuItem("Ativo");
            _enabledItem.Checked = true;
            _enabledItem.Click += OnToggleEnabledClick;

            _startupItem = new ToolStripMenuItem("Iniciar com o Windows");
            _startupItem.Checked = StartupManager.IsEnabled();
            _startupItem.Click += OnToggleStartupClick;

            var inspectItem = new ToolStripMenuItem("Ver sessões de mídia");
            inspectItem.Click += OnInspectSessionsClick;

            var exitItem = new ToolStripMenuItem("Sair");
            exitItem.Click += OnExitClick;

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_enabledItem);
            menu.Items.Add(_startupItem);
            menu.Items.Add(inspectItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _activeIcon;
            _notifyIcon.Text = "pauto — Conectando...";
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.Visible = true;
            _notifyIcon.DoubleClick += OnToggleEnabledClick;

            _marshalControl.BeginInvoke(new Action(StartMonitoring));
        }

        private async void StartMonitoring()
        {
            try
            {
                await _bridge.InitializeAsync();
                if (_exiting)
                {
                    return;
                }

                _initialized = true;
                MediaStateSnapshot initial = _bridge.CaptureState();
                // Seed browser state without triggering a retroactive action on startup.
                _controller.SetEnabled(false);
                _controller.Update(initial.AnyBrowserPlaying, initial.SpotifastPlaying, initial.SpotifastExists);
                _controller.SetEnabled(_enabledItem.Checked);
                RefreshTrayStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Não foi possível inicializar os controles de mídia do Windows (SMTC).\n\nDetalhes: " + ex.Message,
                    "pauto",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                ExitApp();
            }
        }

        private void ScheduleEvaluation()
        {
            if (_exiting || !_marshalControl.IsHandleCreated)
            {
                return;
            }

            try
            {
                _marshalControl.BeginInvoke(new Action(EvaluateNow));
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        private async void EvaluateNow()
        {
            if (_exiting || !_initialized || _commandInFlight)
            {
                return;
            }

            MediaStateSnapshot state = _bridge.CaptureState();
            PlaybackAction action = _controller.Update(
                state.AnyBrowserPlaying,
                state.SpotifastPlaying,
                state.SpotifastExists);

            await ExecuteActionAsync(action);
            RefreshTrayStatus();
        }

        private async void OnResumeTimerTick(object sender, EventArgs e)
        {
            _resumeTimer.Stop();

            if (_exiting || !_initialized || _commandInFlight)
            {
                return;
            }

            MediaStateSnapshot state = _bridge.CaptureState();
            PlaybackAction action = _controller.OnResumeTimerElapsed(
                state.AnyBrowserPlaying,
                state.SpotifastPlaying,
                state.SpotifastExists);

            await ExecuteActionAsync(action);
            RefreshTrayStatus();
        }

        private void OnSafetyPollTick(object sender, EventArgs e)
        {
            if (!_controller.PausedByUs)
            {
                _safetyPollTimer.Stop();
                return;
            }

            EvaluateNow();
        }

        private async Task ExecuteActionAsync(PlaybackAction action)
        {
            switch (action)
            {
                case PlaybackAction.PauseSpotifast:
                    _resumeTimer.Stop();
                    _commandInFlight = true;
                    try
                    {
                        bool paused = await _bridge.PauseSpotifastAsync();
                        if (_exiting)
                        {
                            return;
                        }

                        if (paused)
                        {
                            MediaStateSnapshot after = _bridge.CaptureState();
                            _controller.NotifyPauseSucceeded(after.SpotifastPlaying);
                        }
                        else
                        {
                            _controller.NotifyPauseFailed();
                        }
                    }
                    finally
                    {
                        _commandInFlight = false;
                    }
                    break;

                case PlaybackAction.ScheduleResume:
                    _resumeTimer.Stop();
                    _resumeTimer.Start();
                    break;

                case PlaybackAction.CancelPendingResume:
                    _resumeTimer.Stop();
                    break;

                case PlaybackAction.ResumeSpotifast:
                    _resumeTimer.Stop();
                    _commandInFlight = true;
                    try
                    {
                        await _bridge.ResumeSpotifastAsync();
                        if (!_exiting)
                        {
                            _controller.NotifyResumeCompleted();
                        }
                    }
                    finally
                    {
                        _commandInFlight = false;
                    }
                    break;
            }
        }

        private void RefreshTrayStatus()
        {
            if (_exiting)
            {
                return;
            }

            _safetyPollTimer.Enabled = _controller.Enabled && _controller.PausedByUs;

            if (!_controller.Enabled)
            {
                _statusItem.Text = "Status: Desativado";
                _notifyIcon.Icon = _disabledIcon;
                _notifyIcon.Text = "pauto — Desativado";
            }
            else if (_controller.PausedByUs)
            {
                _statusItem.Text = "Status: Spotifast pausado pelo navegador";
                _notifyIcon.Icon = _pausedIcon;
                _notifyIcon.Text = "pauto — Spotifast pausado";
            }
            else
            {
                _statusItem.Text = "Status: Monitorando";
                _notifyIcon.Icon = _activeIcon;
                _notifyIcon.Text = "pauto — Monitorando";
            }
        }

        private void OnToggleEnabledClick(object sender, EventArgs e)
        {
            bool nextEnabled = !_controller.Enabled;
            _enabledItem.Checked = nextEnabled;

            PlaybackAction action = _controller.SetEnabled(nextEnabled);
            if (action == PlaybackAction.CancelPendingResume)
            {
                _resumeTimer.Stop();
            }

            if (nextEnabled && _initialized)
            {
                MediaStateSnapshot state = _bridge.CaptureState();
                _controller.SetEnabled(false);
                _controller.Update(state.AnyBrowserPlaying, state.SpotifastPlaying, state.SpotifastExists);
                _controller.SetEnabled(true);
            }

            RefreshTrayStatus();
        }

        private void OnToggleStartupClick(object sender, EventArgs e)
        {
            bool target = !_startupItem.Checked;
            if (StartupManager.SetEnabled(target))
            {
                _startupItem.Checked = target;
            }
            else
            {
                MessageBox.Show(
                    "Não foi possível alterar a inicialização automática no Registro do Windows.",
                    "pauto",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void OnInspectSessionsClick(object sender, EventArgs e)
        {
            List<SessionSnapshot> sessions = _bridge.GetSnapshots();
            var sb = new StringBuilder();

            if (sessions.Count == 0)
            {
                sb.AppendLine("Nenhuma sessão de mídia ativa no Windows (SMTC) agora.");
                sb.AppendLine();
                sb.AppendLine("Dê play em uma música no Spotifast ou em um vídeo no navegador para aparecer aqui.");
            }
            else
            {
                sb.AppendLine("Sessões detectadas no Windows (SMTC):");
                sb.AppendLine();
                for (int i = 0; i < sessions.Count; i++)
                {
                    SessionSnapshot s = sessions[i];
                    sb.AppendLine(string.Format("- {0}  [{1}]  ({2})", s.AppId, s.Kind, s.Status));
                }
            }

            MessageBox.Show(sb.ToString(), "pauto — Sessões de mídia", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void OnExitClick(object sender, EventArgs e)
        {
            ExitApp();
        }

        private void ExitApp()
        {
            if (_exiting)
            {
                return;
            }

            _exiting = true;
            _resumeTimer.Stop();
            _safetyPollTimer.Stop();
            _resumeTimer.Dispose();
            _safetyPollTimer.Dispose();
            _bridge.Dispose();

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _activeIcon.Dispose();
            _pausedIcon.Dispose();
            _disabledIcon.Dispose();
            _marshalControl.Dispose();

            ExitThread();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\pauto-single-instance", out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
                GC.KeepAlive(mutex);
            }
        }
    }
}
