using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class SessionProbe
{
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr c);
        int UnregisterEndpointNotificationCallback(IntPtr c);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice {
        int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        int OpenPropertyStore(int access, out IPropertyStore ps);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out int state);
    }
    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore {
        int GetCount(out int c); int GetAt(int i, out PROPERTYKEY k);
        int GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
    }
    [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
    [StructLayout(LayoutKind.Explicit)] struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr p; [FieldOffset(16)] public IntPtr p2; }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2 {
        int GetAudioSessionControl(IntPtr g, int f, out IntPtr c);
        int GetSimpleAudioVolume(IntPtr g, int f, out IntPtr v);
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
        int RegisterSessionNotification(IntPtr n); int UnregisterSessionNotification(IntPtr n);
        int RegisterDuckNotification(IntPtr s, IntPtr n); int UnregisterDuckNotification(IntPtr n);
    }
    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, out IAudioSessionControl2 s); }
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2 {
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int SetDisplayName(string s, IntPtr g);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int SetIconPath(string s, IntPtr g);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr c);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string s);
        int GetProcessId(out uint pid);
        [PreserveSig] int IsSystemSoundsSession();
        int SetDuckingPreference(bool b);
    }
    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioMeterInformation { int GetPeakValue(out float peak); int GetMeteringChannelCount(out int n); int GetChannelsPeakValues(int n, [Out] float[] v); int QueryHardwareSupport(out int m); }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT v);

    static string FriendlyName(IMMDevice d) {
        IPropertyStore ps; d.OpenPropertyStore(0, out ps);
        var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
        PROPVARIANT v; ps.GetValue(ref key, out v);
        string s = v.vt == 31 ? Marshal.PtrToStringUni(v.p) : "?";
        PropVariantClear(ref v);
        return s;
    }

    static string ProcName(uint pid) { try { return pid == 0 ? "(system)" : Process.GetProcessById((int)pid).ProcessName; } catch { return "(gone)"; } }

    public static string Run(int sampleMs) { return Run(sampleMs, 100); }
    public static string Run(int sampleMs, int intervalMs) {
        var sb = new StringBuilder();
        var en = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")));
        IMMDevice def; en.GetDefaultAudioEndpoint(0, 1, out def); string defId; def.GetId(out defId);
        IMMDeviceCollection col; en.EnumAudioEndpoints(0, 1, out col);
        int n; col.GetCount(out n);
        var meters = new List<Tuple<string, IAudioMeterInformation>>();
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) {
            IMMDevice dev; col.Item(i, out dev); string id; dev.GetId(out id);
            string name = FriendlyName(dev);
            sb.AppendLine("ENDPOINT " + name + (id == defId ? "  [DEFAULT]" : ""));
            var iid = typeof(IAudioSessionManager2).GUID;
            object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
            var mgr = (IAudioSessionManager2)o;
            IAudioSessionEnumerator se; mgr.GetSessionEnumerator(out se); int sc; se.GetCount(out sc);
            for (int j = 0; j < sc; j++) {
                IAudioSessionControl2 s; se.GetSession(j, out s);
                int st; s.GetState(out st); uint pid; s.GetProcessId(out pid); string dn; s.GetDisplayName(out dn);
                string sid; s.GetSessionIdentifier(out sid);
                bool sys = s.IsSystemSoundsSession() == 0;
                var m = (IAudioMeterInformation)s; float pk; m.GetPeakValue(out pk);
                string state = st == 0 ? "Inactive" : st == 1 ? "Active" : "Expired";
                string label = name + " | pid " + pid + " " + ProcName(pid) + (sys ? " [SYSTEM SOUNDS]" : "");
                sb.AppendLine(string.Format("  pid={0,-6} proc={1,-18} state={2,-8} peak={3:F3} sys={4} name='{5}'", pid, ProcName(pid), state, pk, sys, dn));
                sb.AppendLine("      sid=" + sid);
                meters.Add(Tuple.Create(label, m));
            }
        }
        sb.AppendLine("enumerate ms=" + sw.Elapsed.TotalMilliseconds.ToString("F1"));
        var max = new float[meters.Count]; var nonzero = new int[meters.Count]; int samples = 0;
        var cost = new Stopwatch();
        var win = new List<float[]>(); float[] cur = null; int perWin = Math.Max(1, 10000 / intervalMs);
        for (int t = 0; t < sampleMs; t += intervalMs) {
            if (samples % perWin == 0) { cur = new float[meters.Count]; win.Add(cur); }
            cost.Start();
            for (int k = 0; k < meters.Count; k++) { float p; meters[k].Item2.GetPeakValue(out p); if (p > max[k]) max[k] = p; if (p > cur[k]) cur[k] = p; if (p > 0.001f) nonzero[k]++; }
            cost.Stop(); samples++;
            Thread.Sleep(intervalMs);
        }
        sb.AppendLine("PEAK SAMPLING " + samples + " rounds x " + meters.Count + " meters, total ms=" + cost.Elapsed.TotalMilliseconds.ToString("F2") + " (per call us=" + (cost.Elapsed.TotalMilliseconds*1000/(samples*Math.Max(1,meters.Count))).ToString("F1") + ")");
        for (int k = 0; k < meters.Count; k++) sb.AppendLine(string.Format("  max={0:F3} nonzero={1}/{2}  {3}", max[k], nonzero[k], samples, meters[k].Item1));
        sb.AppendLine("MAX PER 10 s WINDOW (meter index: values)");
        for (int k = 0; k < meters.Count; k++) { var line = new StringBuilder("  [" + k + "] "); foreach (var w in win) line.Append(w[k].ToString("F3")).Append(' '); sb.AppendLine(line.ToString() + " " + meters[k].Item1); }
        return sb.ToString();
    }
}
