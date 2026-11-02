using System;
using System.Collections.Generic;
using Voltide.Core;
using Voltide.Interop;

namespace Voltide.Providers
{
    /// <summary>
    /// TAIDU (钛度) 2.4G mice, e.g. TSG809.
    ///
    /// Protocol reverse-engineered from TAIDU's official WebHID driver
    /// (https://l2a22.yjx2012.com/) and verified on a real receiver
    /// (VID 0xA8A5, PID 0x2255, product "USB Receiver", mfr "YJX-CHIP"):
    ///
    ///   Target collection: usage page 0xFF01, usage 0x10, 65-byte in/out.
    ///   The official driver filters the same collection (usagePage 65281,
    ///   usage 16) and accepts vendor IDs 0xA8A4 / 0xA8A5 / 0x3A37.
    ///
    ///   Query — 65-byte output report over interrupt OUT (WriteFile).
    ///   HidD_SetOutputReport reaches the device but the firmware does not
    ///   answer battery queries sent that way:
    ///
    ///     [0]     = 0x00 report id
    ///     [1]     = 0x55
    ///     [2]     = 0x30     command: battery query
    ///     [3..5]  = A5 0B 2E
    ///     [6..8]  = 01 01 01
    ///     [9..64] = 00
    ///
    ///   Reply — 65-byte input report (verified capture):
    ///
    ///     00 AA 30 A5 0B 0A 01 01 01 44 00 00 ...
    ///        ^^ ^^               ^^ ^^
    ///        |  |                |  +-- charge flag: 1 = charging
    ///        |  |                +----- percent (0x44 = 68)
    ///        |  +-------------------- echoes command 0x30
    ///        +------------------------ 0xAA marks a reply (query used 0x55)
    ///
    ///   The web driver reads the same bytes from the inputreport event,
    ///   which strips the report id, so its battery o[8] is frame[9] here.
    /// </summary>
    public class TaiduProvider : IDeviceProvider
    {
        private const ushort ReceiverVid = 0xA8A5;   // 2.4G receiver
        private const ushort WiredVid = 0xA8A4;      // wired mode VID
        private const ushort AltVid = 0x3A37;        // second VID family in the official driver
        private const ushort VendorPage = 0xFF01;
        private const ushort MouseUsage = 0x0010;

        private const int FrameSize = 65;
        private const byte CmdBattery = 0x30;
        private const byte ReplyMark = 0xAA;
        private const int PercentIndex = 9;
        private const int ChargeIndex = 10;

        public string Name { get { return "TAIDU HID"; } }

        public List<DeviceInfo> Discover(List<HidDeviceInfo> catalog)
        {
            List<DeviceInfo> found = new List<DeviceInfo>();
            Dictionary<string, HidDeviceInfo> candidates = new Dictionary<string, HidDeviceInfo>();

            foreach (HidDeviceInfo h in catalog)
            {
                if (h.VendorId != ReceiverVid && h.VendorId != WiredVid && h.VendorId != AltVid) continue;
                if (h.UsagePage != VendorPage) continue;
                if (h.Usage != MouseUsage) continue;
                if (h.InputReportLength != FrameSize) continue;
                if (h.OutputReportLength != FrameSize) continue;

                string key = h.VendorId.ToString("X4") + ":" + h.ProductId.ToString("X4");
                if (!candidates.ContainsKey(key)) candidates.Add(key, h);
            }

            foreach (KeyValuePair<string, HidDeviceInfo> kv in candidates)
            {
                HidDeviceInfo h = kv.Value;

                DeviceInfo d = new DeviceInfo();
                d.Id = "taidu-" + kv.Key;
                d.Brand = "TAIDU";
                d.Model = ResolveModel(h.ProductId, h.ProductName);
                d.Name = "TAIDU " + d.Model;
                d.ShortModel = d.Model;
                d.Kind = DeviceKind.Mouse;
                d.VendorId = h.VendorId;
                d.ProductId = h.ProductId;
                d.HidPath = h.Path;
                d.ProviderName = Name;
                found.Add(d);
            }
            return found;
        }

        public DeviceReading Read(DeviceInfo device)
        {
            DeviceReading r = new DeviceReading();
            r.UpdatedAt = DateTime.Now;
            r.Online = false;
            r.Level = BatteryLevel.Unknown;

            try
            {
                byte[] cmd = NewQuery();

                using (HidSession s = new HidSession(device.HidPath, FrameSize, FrameSize))
                {
                    s.Flush(30);

                    byte[] resp = null;

                    for (int attempt = 0; attempt < 3 && resp == null; attempt++)
                    {
                        if (attempt > 0) System.Threading.Thread.Sleep(120);

                        bool sent = s.Write(cmd, 500);
                        if (!sent) sent = s.SetOutputReport(cmd);
                        if (!sent)
                        {
                            r.Error = "发送失败（设备被占用？）";
                            return r;
                        }

                        resp = WaitFor(s, 450);
                    }

                    if (resp == null)
                    {
                        r.Sleeping = true;
                        r.Error = "设备休眠";
                        return r;
                    }

                    r.Raw = Hex(resp);

                    int level = resp[PercentIndex];
                    if (level > 100)
                    {
                        r.Online = true;
                        r.Percent = null;
                        r.Error = "读数异常";
                        return r;
                    }

                    r.Percent = level;
                    r.Level = BatteryMath.FromPercent(level);
                    r.Charging = resp[ChargeIndex] == 0x01;
                    r.Online = true;
                    r.Detail = device.VendorId == WiredVid ? "USB" : "2.4G";
                }
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
            }

            return r;
        }

        /// <summary>
        /// Builds the battery query. The official driver pads the same
        /// ten command bytes into a zeroed 65-byte report.
        /// </summary>
        private static byte[] NewQuery()
        {
            byte[] cmd = new byte[FrameSize];
            cmd[0] = 0x00;
            cmd[1] = 0x55;
            cmd[2] = CmdBattery;
            cmd[3] = 0xA5;
            cmd[4] = 0x0B;
            cmd[5] = 0x2E;
            cmd[6] = 0x01;
            cmd[7] = 0x01;
            cmd[8] = 0x01;
            return cmd;
        }

        private static byte[] WaitFor(HidSession s, int timeoutMs)
        {
            for (int i = 0; i < 12; i++)
            {
                byte[] p = s.Read(Math.Max(60, timeoutMs / 4));
                if (p == null) return null;
                if (p.Length > ChargeIndex && p[1] == ReplyMark && p[2] == CmdBattery) return p;
            }
            return null;
        }

        private static string Hex(byte[] b)
        {
            if (b == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("X2"));
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        private static string ResolveModel(ushort pid, string productName)
        {
            if (pid == 0x2255) return "TSG809";   // receiver verified with a TSG809
            if (!string.IsNullOrEmpty(productName) &&
                productName.IndexOf("TSG809", StringComparison.OrdinalIgnoreCase) >= 0)
                return "TSG809";
            return "PID_" + pid.ToString("X4");
        }
    }
}
