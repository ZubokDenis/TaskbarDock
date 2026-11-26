using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace TaskbarDock {
    internal static class SystemAudio {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumerator { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator {
            int NotImpl1();
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice {
            int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioEndpointVolume {
            int NotImpl1();
            int NotImpl2();
            int NotImpl3();
            int NotImpl4();
            int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
            int NotImpl6();
            int GetMasterVolumeLevelScalar(out float pfLevel);
            int NotImpl8();
            int NotImpl9();
            int NotImpl10();
            int NotImpl11();
            int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
            int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        private static IAudioEndpointVolume GetEndpointVolume() {
            var enumerator = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
            IMMDevice dev;
            if (enumerator.GetDefaultAudioEndpoint(0, 1, out dev) != 0 || dev == null) return null;
            Guid iid = typeof(IAudioEndpointVolume).GUID;
            object obj;
            if (dev.Activate(ref iid, 23, IntPtr.Zero, out obj) != 0 || obj == null) return null;
            return (IAudioEndpointVolume)obj;
        }

        public static bool GetVolumeState(out int percent, out bool muted) {
            percent = 50;
            muted = false;
            try {
                var ep = GetEndpointVolume();
                if (ep == null) return false;
                float lvl;
                bool m;
                if (ep.GetMasterVolumeLevelScalar(out lvl) == 0) {
                    percent = Math.Max(0, Math.Min(100, (int)Math.Round(lvl * 100.0f)));
                }
                if (ep.GetMute(out m) == 0) {
                    muted = m;
                }
                return true;
            } catch {
                return false;
            }
        }

        public static void SetVolumePercent(int percent) {
            try {
                var ep = GetEndpointVolume();
                if (ep == null) return;
                float lvl = Math.Max(0.0f, Math.Min(1.0f, percent / 100.0f));
                Guid ctx = Guid.Empty;
                ep.SetMasterVolumeLevelScalar(lvl, ref ctx);
                if (percent > 0) {
                    ep.SetMute(false, ref ctx);
                }
            } catch { }
        }

        public static void SetMute(bool mute) {
            try {
                var ep = GetEndpointVolume();
                if (ep == null) return;
                Guid ctx = Guid.Empty;
                ep.SetMute(mute, ref ctx);
            } catch { }
        }
    }

    internal class WifiNetworkInfo {
        public Guid InterfaceGuid;
        public string Ssid;
        public string ProfileName;
        public int SignalQuality;
        public bool IsConnected;
        public bool HasProfile;
        public bool IsSecured;
    }

    internal static class SystemWifi {
        [DllImport("wlanapi.dll")]
        private static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr pMemory);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanGetAvailableNetworkList(IntPtr hClientHandle, ref Guid pInterfaceGuid, uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanConnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, ref WLAN_CONNECTION_PARAMETERS pConnectionParameters, IntPtr pReserved);

        [DllImport("wlanapi.dll")]
        private static extern uint WlanDisconnect(IntPtr hClientHandle, ref Guid pInterfaceGuid, IntPtr pReserved);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_INTERFACE_INFO {
            public Guid InterfaceGuid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strInterfaceDescription;
            public int isState;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_AVAILABLE_NETWORK {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strProfileName;
            public uint uSSIDLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public byte[] ucSSID;
            public int dot11BssType;
            public uint uNumberOfBssids;
            public int bNetworkConnectable;
            public uint wlanNotConnectableReason;
            public uint uNumberOfPhyTypes;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public uint[] dot11PhyTypes;
            public int bMorePhyTypes;
            public uint wlanSignalQuality;
            public int bSecurityEnabled;
            public int dot11DefaultAuthAlgorithm;
            public int dot11DefaultCipherAlgorithm;
            public uint dwFlags;
            public uint dwReserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WLAN_CONNECTION_PARAMETERS {
            public int wlanConnectionMode;
            public string strProfile;
            public IntPtr pDot11Ssid;
            public IntPtr pDesiredBssidList;
            public int dot11BssType;
            public uint dwFlags;
        }

        public static List<WifiNetworkInfo> GetNetworks(out string adapterName) {
            adapterName = "";
            var result = new List<WifiNetworkInfo>();
            IntPtr hClient = IntPtr.Zero;
            try {
                uint neg;
                if (WlanOpenHandle(2, IntPtr.Zero, out neg, out hClient) != 0 || hClient == IntPtr.Zero) return result;

                IntPtr pIfList;
                if (WlanEnumInterfaces(hClient, IntPtr.Zero, out pIfList) == 0 && pIfList != IntPtr.Zero) {
                    try {
                        int ifCount = Marshal.ReadInt32(pIfList);
                        IntPtr pIf = IntPtr.Add(pIfList, 8);
                        int ifSize = Marshal.SizeOf(typeof(WLAN_INTERFACE_INFO));

                        if (ifCount > 0) {
                            var iface = (WLAN_INTERFACE_INFO)Marshal.PtrToStructure(pIf, typeof(WLAN_INTERFACE_INFO));
                            adapterName = iface.strInterfaceDescription ?? "Wi-Fi";
                            Guid ifGuid = iface.InterfaceGuid;

                            IntPtr pNetList;
                            if (WlanGetAvailableNetworkList(hClient, ref ifGuid, 0, IntPtr.Zero, out pNetList) == 0 && pNetList != IntPtr.Zero) {
                                try {
                                    int nCount = Marshal.ReadInt32(pNetList);
                                    IntPtr pNet = IntPtr.Add(pNetList, 8);
                                    int nSize = Marshal.SizeOf(typeof(WLAN_AVAILABLE_NETWORK));
                                    var seen = new Dictionary<string, WifiNetworkInfo>(StringComparer.Ordinal);

                                    for (int j = 0; j < nCount; j++) {
                                        var net = (WLAN_AVAILABLE_NETWORK)Marshal.PtrToStructure(pNet, typeof(WLAN_AVAILABLE_NETWORK));
                                        pNet = IntPtr.Add(pNet, nSize);

                                        int len = (int)Math.Min(32, net.uSSIDLength);
                                        if (len <= 0) continue;
                                        string ssid = Encoding.UTF8.GetString(net.ucSSID, 0, len).Trim();
                                        if (string.IsNullOrEmpty(ssid)) continue;

                                        bool connected = (net.dwFlags & 0x1) != 0;
                                        bool hasProfile = (net.dwFlags & 0x2) != 0 && !string.IsNullOrEmpty(net.strProfileName);

                                        WifiNetworkInfo existing;
                                        if (seen.TryGetValue(ssid, out existing)) {
                                            if (connected) existing.IsConnected = true;
                                            if (hasProfile && !existing.HasProfile) {
                                                existing.HasProfile = true;
                                                existing.ProfileName = net.strProfileName;
                                            }
                                            if ((int)net.wlanSignalQuality > existing.SignalQuality) {
                                                existing.SignalQuality = (int)net.wlanSignalQuality;
                                            }
                                            continue;
                                        }

                                        var info = new WifiNetworkInfo {
                                            InterfaceGuid = ifGuid,
                                            Ssid = ssid,
                                            ProfileName = net.strProfileName ?? "",
                                            SignalQuality = (int)net.wlanSignalQuality,
                                            IsConnected = connected,
                                            HasProfile = hasProfile,
                                            IsSecured = net.bSecurityEnabled != 0
                                        };
                                        seen[ssid] = info;
                                        result.Add(info);
                                    }
                                } finally {
                                    WlanFreeMemory(pNetList);
                                }
                            }
                        }
                    } finally {
                        WlanFreeMemory(pIfList);
                    }
                }
            } catch {
            } finally {
                if (hClient != IntPtr.Zero) WlanCloseHandle(hClient, IntPtr.Zero);
            }

            result.Sort((a, b) => {
                if (a.IsConnected != b.IsConnected) return a.IsConnected ? -1 : 1;
                if (a.HasProfile != b.HasProfile) return a.HasProfile ? -1 : 1;
                return b.SignalQuality.CompareTo(a.SignalQuality);
            });
            return result;
        }

        public static bool ConnectProfile(Guid ifGuid, string profileName) {
            if (string.IsNullOrEmpty(profileName)) return false;
            IntPtr hClient = IntPtr.Zero;
            try {
                uint neg;
                if (WlanOpenHandle(2, IntPtr.Zero, out neg, out hClient) != 0) return false;
                var p = new WLAN_CONNECTION_PARAMETERS {
                    wlanConnectionMode = 0,
                    strProfile = profileName,
                    pDot11Ssid = IntPtr.Zero,
                    pDesiredBssidList = IntPtr.Zero,
                    dot11BssType = 3,
                    dwFlags = 0
                };
                return WlanConnect(hClient, ref ifGuid, ref p, IntPtr.Zero) == 0;
            } catch {
                return false;
            } finally {
                if (hClient != IntPtr.Zero) WlanCloseHandle(hClient, IntPtr.Zero);
            }
        }

        public static bool Disconnect(Guid ifGuid) {
            IntPtr hClient = IntPtr.Zero;
            try {
                uint neg;
                if (WlanOpenHandle(2, IntPtr.Zero, out neg, out hClient) != 0) return false;
                return WlanDisconnect(hClient, ref ifGuid, IntPtr.Zero) == 0;
            } catch {
                return false;
            } finally {
                if (hClient != IntPtr.Zero) WlanCloseHandle(hClient, IntPtr.Zero);
            }
        }
    }

    internal class KeyboardLayoutInfo {
        public IntPtr Hkl;
        public string Code;
        public string DisplayName;
    }

    internal static class SystemKeyboard {
        public static List<KeyboardLayoutInfo> GetInstalledLayouts() {
            var list = new List<KeyboardLayoutInfo>();
            try {
                int count = NativeMethods.GetKeyboardLayoutList(0, null);
                if (count <= 0) return list;
                IntPtr[] hkls = new IntPtr[count];
                NativeMethods.GetKeyboardLayoutList(count, hkls);
                var seen = new HashSet<long>();
                foreach (IntPtr hkl in hkls) {
                    long raw = hkl.ToInt64();
                    if (!seen.Add(raw)) continue;
                    int langId = (int)((ulong)raw & 0xFFFF);
                    string code = "LNG";
                    string name = "Keyboard";
                    try {
                        CultureInfo ci = new CultureInfo(langId);
                        code = ci.ThreeLetterISOLanguageName.ToUpperInvariant();
                        name = ci.NativeName;
                        if (!string.IsNullOrEmpty(name)) {
                            name = char.ToUpper(name[0]) + name.Substring(1);
                        }
                    } catch { }
                    list.Add(new KeyboardLayoutInfo {
                        Hkl = hkl,
                        Code = code,
                        DisplayName = name
                    });
                }
            } catch { }
            return list;
        }
    }
}
