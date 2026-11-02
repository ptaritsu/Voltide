using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Voltide.Core
{
    /// <summary>
    /// Appends one line per device after each GUI refresh to
    /// &lt;exe dir&gt;/log/yyMMdd.log. The switch lives in the tray menu and
    /// persists in the same registry key as the refresh interval.
    /// </summary>
    internal static class RefreshLogger
    {
        private const string SettingsKey = @"Software\Voltide";
        private static readonly object _lock = new object();
        private static bool _enabled;

        public static bool Enabled
        {
            get { return _enabled; }
            set
            {
                _enabled = value;
                try
                {
                    using (Microsoft.Win32.RegistryKey key =
                           Microsoft.Win32.Registry.CurrentUser.CreateSubKey(SettingsKey))
                    {
                        if (key != null) key.SetValue("LogEnabled", value ? 1 : 0);
                    }
                }
                catch
                {
                }
            }
        }

        public static void Load()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                       Microsoft.Win32.Registry.CurrentUser.OpenSubKey(SettingsKey, false))
                {
                    if (key == null) { _enabled = false; return; }
                    object raw = key.GetValue("LogEnabled");
                    _enabled = raw != null && Convert.ToInt32(raw) != 0;
                }
            }
            catch
            {
                _enabled = false;
            }
        }

        /// <summary>One line per device; no-op when disabled or the list is empty.</summary>
        public static void Write(List<DeviceSnapshot> snapshots)
        {
            if (!_enabled || snapshots == null || snapshots.Count == 0) return;

            try
            {
                DateTime now = DateTime.Now;
                string dir = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory ?? ".", "log");
                Directory.CreateDirectory(dir);

                string path = Path.Combine(dir, now.ToString("yyMMdd") + ".log");
                string stamp = now.ToString("yyyy-MM-dd HH:mm:ss");

                StringBuilder sb = new StringBuilder();
                foreach (DeviceSnapshot s in snapshots)
                {
                    if (s == null || s.Info == null || s.Reading == null) continue;

                    DeviceInfo info = s.Info;
                    DeviceReading r = s.Reading;

                    string name = info.Name;
                    if (string.IsNullOrEmpty(name))
                        name = (info.Brand ?? "") + " " + (info.Model ?? "");

                    string pct;
                    if (r.Percent.HasValue)
                        pct = r.Percent.Value + "%";
                    else
                        pct = "--";

                    string state;
                    if (r.Sleeping || !r.Online)
                        state = "sleeping";
                    else if (r.Charging)
                        state = "charging";
                    else if (r.Percent.HasValue)
                        state = "discharging";
                    else
                        state = "unknown";

                    string detail = r.Detail ?? "";
                    string error = r.Error ?? "";

                    sb.Append(stamp);
                    sb.Append(" | ");
                    sb.Append(name);
                    sb.Append(" | ");
                    sb.Append(pct);
                    sb.Append(" | ");
                    sb.Append(state);
                    sb.Append(" | ");
                    sb.Append(detail);
                    if (error.Length > 0)
                    {
                        sb.Append(" | ");
                        sb.Append(error);
                    }
                    sb.AppendLine();
                }

                if (sb.Length == 0) return;

                lock (_lock)
                {
                    File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never break a refresh.
            }
        }
    }
}
