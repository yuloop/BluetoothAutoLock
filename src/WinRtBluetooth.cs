using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace BluetoothAutoLock
{
    internal sealed class ScanHit
    {
        public string Name;
        public string Address;
        public string Kind;     // "Classic" or "LE"
        public bool? Paired;
        public bool? Connected;
        public short? RssiDbm;
        public bool LiveSignal;
    }

    internal static class BluetoothScanner
    {
        // AEP protocol IDs for Bluetooth Classic and Bluetooth LE
        private const string ClassicAqs = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\")";
        private const string LeAqs      = "(System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\")";
        private const string CombinedAqs = "(" + ClassicAqs + " OR " + LeAqs + ")";

        public static List<ScanHit> Scan(int seconds)
        {
            var hits = new Dictionary<string, ScanHit>(StringComparer.OrdinalIgnoreCase);
            object sync = new object();
            var done = new ManualResetEventSlim(false);
            Action signalDone = () =>
            {
                try { done.Set(); } catch (ObjectDisposedException) { }
            };

            string[] props = {
                "System.Devices.Aep.DeviceAddress",
                "System.Devices.Aep.IsConnected",
                "System.Devices.Aep.IsPaired",
                "System.Devices.Aep.SignalStrength",
                "System.Devices.Aep.ProtocolId",
            };

            DeviceWatcher watcher = DeviceInformation.CreateWatcher(
                CombinedAqs, props, DeviceInformationKind.AssociationEndpoint);

            TypedEventHandler<DeviceWatcher, DeviceInformation> onAdded = (w, info) =>
            {
                lock (sync) Record(hits, info);
            };
            TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> onUpdated = (w, upd) =>
            {
                lock (sync)
                {
                    if (hits.ContainsKey(upd.Id))
                    {
                        var h = hits[upd.Id];
                        ApplyProps(h, upd.Properties);
                        if (upd.Properties.ContainsKey("System.Devices.Aep.SignalStrength"))
                            h.LiveSignal = true;
                    }
                }
            };
            TypedEventHandler<DeviceWatcher, object> onCompleted = (w, _) => signalDone();
            TypedEventHandler<DeviceWatcher, object> onStopped = (w, _) => signalDone();

            BluetoothLEAdvertisementWatcher advWatcher = null;
            TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> onAdvertisement =
                (w, args) =>
                {
                    lock (sync) RecordAdvertisement(hits, args);
                };

            watcher.Added += onAdded;
            watcher.Updated += onUpdated;
            watcher.EnumerationCompleted += onCompleted;
            watcher.Stopped += onStopped;

            try
            {
                try
                {
                    advWatcher = new BluetoothLEAdvertisementWatcher();
                    advWatcher.ScanningMode = BluetoothLEScanningMode.Active;
                    advWatcher.Received += onAdvertisement;
                    advWatcher.Start();
                }
                catch
                {
                    advWatcher = null;
                }

                watcher.Start();
                // Keep the radio scan open for the requested window.  The AEP
                // watcher may finish cached enumeration early; stopping then
                // would miss live BLE advertisements that arrive later.
                Thread.Sleep(seconds * 1000);
                try { if (advWatcher != null) advWatcher.Stop(); } catch { }
                try { watcher.Stop(); } catch { }
                done.Wait(2000);
            }
            finally
            {
                if (advWatcher != null)
                {
                    try { advWatcher.Received -= onAdvertisement; } catch { }
                }
                watcher.Added -= onAdded;
                watcher.Updated -= onUpdated;
                watcher.EnumerationCompleted -= onCompleted;
                watcher.Stopped -= onStopped;
                done.Dispose();
            }

            List<ScanHit> list;
            lock (sync)
            {
                list = new List<ScanHit>(hits.Values);
            }
            list.Sort((a, b) => string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase));
            return list;
        }


        public static ScanHit FindClassicTargetByAddress(int seconds, ulong address)
        {
            int boundedSeconds = Math.Max(1, Math.Min(12, seconds));
            List<ScanHit> hits = Scan(boundedSeconds);

            // 已配对的手机不在附近时，AEP Added 事件仍会带上 Windows 缓存的上次 RSSI
            // （2026-09-29 实测：手机离开后每次扫描都报 -17 dBm、LIVE=no），
            // 所以只认本次扫描里实时收到的信号（Updated 事件或广播）。
            // 代价：不可被发现、也不发同地址广播的手机，这次扫描看不到它，
            // 是否在场只由前面的 SDP 复核决定（SDP 才是主要在场证据）。
            //
            // 不回退到名称匹配：手机关闭 Classic 后可能仍暴露同名的其他 BLE 身份。
            return FindMatchingAddressHit(hits, address, HasCredibleClassicRadioEvidence);
        }

        private static ScanHit FindMatchingAddressHit(List<ScanHit> hits, ulong address, Func<ScanHit, bool> accept)
        {
            foreach (ScanHit hit in hits)
                if (accept(hit) && AddressMatches(hit.Address, address)) return hit;
            return null;
        }

        private static bool HasCredibleClassicRadioEvidence(ScanHit hit)
        {
            return hit != null &&
                IsClassic(hit) &&
                hit.LiveSignal &&
                ClassicBluetoothEvidence.HasCredibleRssi(hit.RssiDbm);
        }

        private static bool IsClassic(ScanHit hit)
        {
            return hit != null && string.Equals(hit.Kind, "Classic", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AddressMatches(string text, ulong address)
        {
            ulong parsed;
            return NativeMethods.TryParseBluetoothAddress(text, out parsed) && parsed == address;
        }

        private static void Record(Dictionary<string, ScanHit> dict, DeviceInformation info)
        {
            var hit = new ScanHit { Name = info.Name };
            ApplyProps(hit, info.Properties);
            dict[info.Id] = hit;
        }

        private static void RecordAdvertisement(Dictionary<string, ScanHit> dict, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string name = args.Advertisement != null ? args.Advertisement.LocalName : null;
            string addr = NativeMethods.FormatBluetoothAddress(args.BluetoothAddress);
            string key = "adv:" + args.BluetoothAddress.ToString("X12") + ":" + (name ?? "");

            foreach (ScanHit existing in dict.Values)
            {
                if (!AddressMatches(existing.Address, args.BluetoothAddress)) continue;
                if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(existing.Name))
                    existing.Name = name;
                existing.RssiDbm = args.RawSignalStrengthInDBm;
                existing.LiveSignal = true;
            }

            ScanHit hit;
            if (!dict.TryGetValue(key, out hit))
            {
                hit = new ScanHit();
                dict[key] = hit;
            }

            if (!string.IsNullOrWhiteSpace(name))
                hit.Name = name;
            hit.Address = addr;
            hit.Kind = "LE-Adv";
            hit.Paired = false;
            hit.Connected = false;
            hit.RssiDbm = args.RawSignalStrengthInDBm;
            hit.LiveSignal = true;
        }

        private static void ApplyProps(ScanHit hit, IReadOnlyDictionary<string, object> props)
        {
            object v;
            if (props.TryGetValue("System.Devices.Aep.DeviceAddress", out v) && v != null)
                hit.Address = v.ToString();
            if (props.TryGetValue("System.Devices.Aep.IsPaired", out v) && v is bool)
                hit.Paired = (bool)v;
            if (props.TryGetValue("System.Devices.Aep.IsConnected", out v) && v is bool)
                hit.Connected = (bool)v;
            if (props.TryGetValue("System.Devices.Aep.SignalStrength", out v) && v != null)
            {
                try { hit.RssiDbm = Convert.ToInt16(v); } catch { }
            }
            if (props.TryGetValue("System.Devices.Aep.ProtocolId", out v) && v != null)
            {
                string id = v.ToString();
                if (id.IndexOf("e0cbf06c", StringComparison.OrdinalIgnoreCase) >= 0) hit.Kind = "Classic";
                else if (id.IndexOf("bb7bb05e", StringComparison.OrdinalIgnoreCase) >= 0) hit.Kind = "LE";
            }
        }
    }

    /// <summary>
    /// Active proximity probe via Win10 Bluetooth radio. The probe forces an
    /// uncached SDP service query to the paired device. The device's radio
    /// has to be on AND in range for the query to succeed — exactly the
    /// "phone is here" signal the user wants.
    ///
    /// Notes:
    ///  - We intentionally ignore <see cref="BluetoothDevice.ConnectionStatus"/>
    ///    because that field only flips when an active profile binding (audio,
    ///    HID, etc.) exists; modern phones do not maintain such a binding while
    ///    idle.
    ///  - SDP probing is the same radio-level handshake Windows' built-in
    ///    "Dynamic Lock" feature uses; it does not show a notification on the
    ///    phone and does not consume a profile slot.
    /// </summary>
    internal static class WinRtBluetooth
    {
        /// <summary>
        /// Returns true if the SDP query to the device responded within
        /// <paramref name="timeoutMs"/>; false on a clean unreachable error;
        /// null on transient errors (caller should treat as "no signal change").
        /// </summary>
        public static bool? IsInRange(ulong address, int timeoutMs)
        {
            var done = new ManualResetEventSlim(false);
            Action signalDone = () =>
            {
                try { done.Set(); } catch (ObjectDisposedException) { }
            };
            bool result = false;
            bool transient = false;

            IAsyncOperation<BluetoothDevice> getDeviceOp = null;
            IAsyncOperation<RfcommDeviceServicesResult> sdpOp = null;

            try
            {
                getDeviceOp = BluetoothDevice.FromBluetoothAddressAsync(address);
                getDeviceOp.Completed = (op, status) =>
                {
                    BluetoothDevice device = null;
                    try
                    {
                        if (status != AsyncStatus.Completed) { transient = true; signalDone(); return; }
                        device = op.GetResults();
                        if (device == null) { result = false; signalDone(); return; }

                        sdpOp = device.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
                        BluetoothDevice deviceForCallback = device;
                        sdpOp.Completed = (op2, status2) =>
                        {
                            try
                            {
                                if (status2 != AsyncStatus.Completed) { transient = true; return; }
                                RfcommDeviceServicesResult res = op2.GetResults();
                                result = res.Error == BluetoothError.Success && res.Services.Count > 0;
                            }
                            catch { transient = true; }
                            finally
                            {
                                try { deviceForCallback.Dispose(); } catch { }
                                signalDone();
                            }
                        };
                    }
                    catch
                    {
                        transient = true;
                        try { if (device != null) device.Dispose(); } catch { }
                        signalDone();
                    }
                };
            }
            catch
            {
                done.Dispose();
                return null;
            }

            try
            {
                if (!done.Wait(timeoutMs))
                {
                    try { if (sdpOp != null) sdpOp.Cancel(); } catch { }
                    try { if (getDeviceOp != null) getDeviceOp.Cancel(); } catch { }
                    return false;
                }

                if (transient) return null;
                return result;
            }
            finally
            {
                done.Dispose();
            }
        }
    }
}
