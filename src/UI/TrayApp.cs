using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Voltide.Core;

namespace Voltide.UI
{
    public class TrayApp : IDisposable
    {
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        private readonly DeviceManager _manager;
        private readonly NotifyIcon _tray;
        private readonly MainForm _form;
        private readonly System.Windows.Forms.Timer _timer;

        private bool _refreshing;
        private bool _refreshQueued;

        // A second right-click replaces the popup instead of stacking another copy.
        private TrayMenuForm _menuForm;

        // Hover peek: battery list under the pointer, no native "Voltide" tooltip.
        // NotifyIcon has no MouseEnter/MouseLeave on .NET Framework, so hover is
        // tracked via MouseMove + WindowFromPoint polling while the peek is up.
        private TrayMenuForm _hoverForm;
        private IntPtr _trayHwnd;
        private readonly System.Windows.Forms.Timer _hoverPollTimer;


        internal static readonly Color MenuMuted = Color.FromArgb(135, 135, 135); // card status line

        public TrayApp()
        {
            _manager = new DeviceManager();

            _form = new MainForm(_manager);
            _form.FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    _form.Hide();
                }
            };
            _form.RefreshRequested += delegate { RefreshNow(); };

            // force handle creation so cross-thread marshalling works immediately
            IntPtr handle = _form.Handle;

            _tray = new NotifyIcon();
            _tray.Icon = IconFactory.ToIcon(32);
            // No native tooltip: hover opens the battery peek instead of "Voltide".
            _tray.Text = "";
            _tray.Visible = true;

            _tray.MouseMove += delegate(object s, MouseEventArgs e)
            {
                // Over the icon: remember its window so a stationary pointer still
                // counts as hovering (MouseMove stops once the cursor stops).
                IntPtr hwnd = WindowFromPoint(Cursor.Position);
                if (hwnd != IntPtr.Zero) _trayHwnd = GetAncestor(hwnd, 2 /* GA_ROOT */);
                ShowHoverPeek();
            };

            _hoverPollTimer = new System.Windows.Forms.Timer();
            _hoverPollTimer.Interval = 120;
            _hoverPollTimer.Tick += delegate
            {
                if (_hoverForm == null || _hoverForm.IsDisposed)
                {
                    _hoverPollTimer.Stop();
                    return;
                }

                Point pt = Cursor.Position;
                bool overPeek = _hoverForm.Bounds.Contains(pt);
                bool overTray = false;
                if (_trayHwnd != IntPtr.Zero)
                {
                    IntPtr hwnd = WindowFromPoint(pt);
                    if (hwnd != IntPtr.Zero)
                        overTray = GetAncestor(hwnd, 2) == _trayHwnd;
                }

                if (overPeek)
                {
                    return;
                }

                if (!overTray) HideHoverPeek();
            };

            _tray.MouseUp += delegate(object s, MouseEventArgs e)
            {
                HideHoverPeek();
                if (e.Button == MouseButtons.Left) { _form.ShowPanel(); return; }
                ShowMenuAboveRight();
            };

            _form.RefreshSeconds = LoadRefreshSeconds();
            _form.RefreshRateChanged += delegate
            {
                SaveRefreshSeconds(_form.RefreshSeconds);
                ApplyInterval();
            };

            RefreshLogger.Load();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _form.RefreshSeconds * 1000;
            _timer.Tick += delegate
            {
                // Keep the footprint small: the receiver shares its input pipe with
                // G HUB, and every request we send is broadcast to it too. It also
                // stops answering for a while if it is polled too hard, which showed
                // up as cards flapping between "97%" and "asleep".
                ApplyInterval();
                RefreshNow();
            };
            _timer.Start();

            RefreshNow();
        }

        /// <summary>
        /// The panel's own cadence while it is open; a slow trickle while hidden in the
        /// tray so a sleeping mouse is not woken every 15 seconds for nothing.
        /// </summary>
        private void ApplyInterval()
        {
            int seconds = _form.Visible ? _form.RefreshSeconds : 300;
            if (_timer.Interval != seconds * 1000) _timer.Interval = seconds * 1000;
        }

        // ------------------------------------------------------- refresh rate

        private const string SettingsKey = @"Software\Voltide";

        private static int LoadRefreshSeconds()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) return 90;
                    object raw = key.GetValue("RefreshSeconds");
                    if (raw == null) return 90;

                    int seconds = Convert.ToInt32(raw);
                    return (seconds == 15 || seconds == 30 || seconds == 90) ? seconds : 90;
                }
            }
            catch
            {
                return 30;
            }
        }

        private static void SaveRefreshSeconds(int seconds)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey))
                {
                    if (key != null) key.SetValue("RefreshSeconds", seconds);
                }
            }
            catch
            {
            }
        }

        // ----------------------------------------------------------- hover peek

        private void StartHoverPoll()
        {
            if (_hoverPollTimer != null && !_hoverPollTimer.Enabled)
                _hoverPollTimer.Start();
        }

        private void StopHoverPoll()
        {
            if (_hoverPollTimer != null) _hoverPollTimer.Stop();
        }

        /// <summary>
        /// Device battery list on tray hover — the same rows as the right-click menu,
        /// without autostart / log / quit. Positioned like the click menu.
        /// </summary>
        private void ShowHoverPeek()
        {
            if (_hoverForm != null && !_hoverForm.IsDisposed) { StartHoverPoll(); return; }
            // A right-click menu owns the screen; don't stack a peek under it.
            if (_menuForm != null && !_menuForm.IsDisposed) return;

            MenuPanel panel = BuildMenu();
            if (panel == null) return;

            panel.ShowSettingsRows = false;

            TrayMenuForm form = new TrayMenuForm();
            form.CloseOnDeactivate = false;
            form.Panel = panel;
            form.ClientSize = panel.Size;
            form.Controls.Add(panel);

            Point p = Cursor.Position;
            Rectangle screen = Screen.FromPoint(p).WorkingArea;

            int x = p.X + 4;
            int y = p.Y - panel.Height - 4;

            if (x + panel.Width > screen.Right) x = p.X - panel.Width - 4;
            if (y < screen.Top) y = p.Y + 4;

            form.Location = new Point(x, y);
            _hoverForm = form;

            // Show without Activate: hovering must not steal focus from the app below.
            form.Show();
            StartHoverPoll();
        }

        private void HideHoverPeek()
        {
            StopHoverPoll();
            if (_hoverForm != null && !_hoverForm.IsDisposed)
            {
                try { _hoverForm.Close(); } catch { }
            }
            _hoverForm = null;
        }

        /// <summary>
        /// Opens the popup to the upper-right of the cursor, in a borderless window of
        /// our own so the size, the corners and the dismissal are all under our control.
        /// </summary>
        private void ShowMenuAboveRight()
        {
            // One popup at a time. Without this, each right-click built a new window
            // and the old one stayed alive until it lost focus — repeated clicks piled
            // up as a stack of menus.
            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            MenuPanel panel = BuildMenu();
            if (panel == null) return;

            TrayMenuForm form = new TrayMenuForm();
            form.Panel = panel;
            form.ClientSize = panel.Size;

            panel.Dock = DockStyle.Fill;
            panel.QuitRequested += delegate { Application.Exit(); };

            panel.AutostartEnabled = Autostart.IsSupported();
            panel.AutostartOn = Autostart.IsSupported() && Autostart.IsEnabled();

            MenuPanel captured = panel;
            panel.AutostartToggled += delegate { Autostart.Set(captured.AutostartOn); };
            panel.LogOn = RefreshLogger.Enabled;
            panel.LogToggled += delegate { RefreshLogger.Enabled = captured.LogOn; };

            form.Controls.Add(panel);

            Point p = Cursor.Position;
            Rectangle screen = Screen.FromPoint(p).WorkingArea;

            int x = p.X + 4;
            int y = p.Y - panel.Height - 4;

            if (x + panel.Width > screen.Right) x = p.X - panel.Width - 4;
            if (y < screen.Top) y = p.Y + 4;

            form.Location = new Point(x, y);
            _menuForm = form;
            form.Show();
            form.Activate();
            panel.Focus();
        }

        // ---------------------------------------------------------------- menu

        /// <summary>
        /// Rebuilt on every open so the list is never stale. Order matches the panel:
        /// reporting a level, then awake-but-waiting, then asleep.
        /// </summary>
        private MenuPanel BuildMenu()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                float scale = g.DpiX / 96f;

                // Sizes straight from the layered artboards: 20 px names and levels,
                // 17 px Chinese, 9.76 px wordmark.
                Font nameFont = FontManager.Get(FontKind.MontserratMedium, 20f * scale);
                Font valueFont = FontManager.Get(FontKind.MontserratMedium, 20f * scale);
                Font cnFont = FontManager.Get(FontKind.Chinese, 17f * scale);
                Font markFont = FontManager.Get(FontKind.MontserratSemiBold, 9f * scale);

                List<MenuRow> rows = new List<MenuRow>();

                foreach (DeviceSnapshot s in OrderedForMenu())
                {
                    MenuRow row = new MenuRow();

                    // The tray row is narrow, so devices may publish a shorter label
                    // than the one the card uses.
                    if (!string.IsNullOrEmpty(s.Info.ShortModel)) row.Name = s.Info.ShortModel;
                    else if (!string.IsNullOrEmpty(s.Info.Model)) row.Name = s.Info.Model;
                    else row.Name = s.Info.Brand;

                    if (HasLevel(s))
                    {
                        row.Kind = MenuRowKind.Level;
                        row.Percent = s.Reading.Percent.Value;
                        row.Value = s.Reading.Percent.Value + "%";
                    }
                    else
                    {
                        row.Kind = MenuRowKind.Asleep;

                        int? last = _form.LastPercent(s.Info.Id);
                        if (last.HasValue)
                        {
                            // Still paired, just idle: show the last known level in grey
                            // instead of a blank, same rule as the card.
                            row.Percent = last.Value;
                            row.Value = last.Value >= 100 ? "Full" : last.Value + "%";
                            row.GreyValue = true;
                        }
                        else
                        {
                            row.Value = "休眠中";
                        }
                    }

                    rows.Add(row);
                }

                return new MenuPanel(rows, scale, nameFont, valueFont, cnFont, markFont);
            }
        }
        private List<DeviceSnapshot> OrderedForMenu()
        {
            List<DeviceSnapshot> live = new List<DeviceSnapshot>();
            List<DeviceSnapshot> asleep = new List<DeviceSnapshot>();

            foreach (DeviceSnapshot s in _manager.Snapshot())
            {
                if (s.Reading == null) { asleep.Add(s); continue; }

                if (s.Reading.Online && s.Reading.Percent.HasValue) live.Add(s);
                else asleep.Add(s);
            }

            live.AddRange(asleep);
            return live;
        }

        private static bool HasLevel(DeviceSnapshot s)
        {
            return s != null && s.Reading != null && s.Reading.Online && s.Reading.Percent.HasValue;
        }

        // ------------------------------------------------------------- refresh

        private void RefreshNow()
        {
            if (_refreshing)
            {
                // A read is already running. Queue one more instead of dropping the
                // request — dropping it is what made a click on the refresh icon spin
                // the arrow and change nothing at all.
                _refreshQueued = true;
                return;
            }

            _refreshing = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    _manager.Refresh();
                }
                catch
                {
                }
                finally
                {
                    _refreshing = false;
                }

                try
                {
                    RefreshLogger.Write(_manager.Snapshot());
                }
                catch
                {
                }

                try
                {
                    _form.BeginInvoke(new MethodInvoker(delegate
                    {
                        _form.UpdateSnapshot(_manager.Snapshot());
                    }));
                }
                catch
                {
                }

                if (_refreshQueued)
                {
                    _refreshQueued = false;
                    RefreshNow();
                }
            });
        }

        public void ShowPanel()
        {
            _form.ShowPanel();
        }

        public void Dispose()
        {
            // The popup is a separate top-level window: closing the tray app must not
            // leave it on screen.
            HideHoverPeek();

            if (_menuForm != null && !_menuForm.IsDisposed)
            {
                try { _menuForm.Close(); } catch { }
                _menuForm = null;
            }

            if (_hoverPollTimer != null)
            {
                _hoverPollTimer.Stop();
                _hoverPollTimer.Dispose();
            }

            if (_timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

        }
    }
}
