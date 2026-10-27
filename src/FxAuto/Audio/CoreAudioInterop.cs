using System.Runtime.InteropServices;

namespace FxAuto.Audio;

// Hand-written Core Audio (MMDevice + WASAPI session) COM interop. GUIDs and vtable order from the Windows SDK
// headers (mmdeviceapi.h, audiopolicy.h, endpointvolume.h); verified on this machine by research 01 §1.5 and the
// spike (tools/spike/Sessions.cs). Only the methods FxAuto uses are typed; vtable prefixes are complete.

/// <summary>Core Audio constants, helpers and COM types (nested so they never clash with other layers' interop).</summary>
internal static class CoreAudio
{
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public const int ERender = 0;
    public const int DeviceStateActive = 1;
    public const int ClsCtxAll = 23;
    public const int StgmRead = 0;
    public const short VtLpwstr = 31;

    /// <summary>PKEY_DeviceInterface_FriendlyName: adapter name ("FxSound Audio Enhancer").</summary>
    public static readonly PropertyKey PkeyAdapter = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    /// <summary>PKEY_Device_EnumeratorName: bus ("ROOT" for FxSound's virtual device).</summary>
    public static readonly PropertyKey PkeyEnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    /// <summary>PKEY_Device_FriendlyName ("FxSound Speakers (FxSound Audio Enhancer)").</summary>
    public static readonly PropertyKey PkeyFriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);

    public const string FxSoundAdapter = "FxSound Audio Enhancer";

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>String property of an endpoint, or null.</summary>
    public static string? ReadString(IMMDevice device, PropertyKey key)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null) return null;
        try
        {
            if (store.GetValue(ref key, out var v) != 0) return null;
            try { return v.Vt == VtLpwstr && v.Pointer != 0 ? Marshal.PtrToStringUni(v.Pointer) : null; }
            finally { PropVariantClear(ref v); }
        }
        finally { Marshal.ReleaseComObject(store); }
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try { Marshal.ReleaseComObject(com); }
            catch (InvalidComObjectException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey(Guid formatId, int propertyId)
    {
        public Guid FormatId = formatId;
        public int PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT (24 bytes on x64; the extra field is harmless padding on x86).</summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct PropVariant
    {
        [FieldOffset(0)] public short Vt;
        [FieldOffset(8)] public nint Pointer;
        [FieldOffset(16)] public nint Pointer2;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int Item(int index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetAt(int index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    /// <summary>Implemented by FxAuto (callbacks on audio service threads: only flag and wake).</summary>
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(nint sessionGuid, int streamFlags, out nint control);
        [PreserveSig] int GetSimpleAudioVolume(nint sessionGuid, int streamFlags, out nint volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        [PreserveSig] int RegisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int UnregisterSessionNotification(IAudioSessionNotification notification);
        [PreserveSig] int RegisterDuckNotification(nint sessionId, nint notification);
        [PreserveSig] int UnregisterDuckNotification(nint notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    /// <summary>Includes the 9 methods of IAudioSessionControl.</summary>
    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, nint eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, nint eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, nint eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int UnregisterAudioSessionNotification(IAudioSessionEvents events);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetProcessId(out uint pid);
        /// <summary>S_OK (0) = it is the system sounds session; S_FALSE (1) = it is not.</summary>
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }

    /// <summary>Implemented by FxAuto. The new session pointer is not used (no AddRef): the monitor re-enumerates.</summary>
    [ComImport, Guid("641DD20B-4D41-49CC-ABA3-174B9477BB08"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionNotification
    {
        [PreserveSig] int OnSessionCreated(nint newSession);
    }

    /// <summary>Implemented by FxAuto. Pointer arguments are raw (not used, not released).</summary>
    [ComImport, Guid("24918ACC-64B3-37C1-8CA9-74A66E9957A8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEvents
    {
        [PreserveSig] int OnDisplayNameChanged(nint newDisplayName, nint eventContext);
        [PreserveSig] int OnIconPathChanged(nint newIconPath, nint eventContext);
        [PreserveSig] int OnSimpleVolumeChanged(float newVolume, int newMute, nint eventContext);
        [PreserveSig] int OnChannelVolumeChanged(uint channelCount, nint newChannelVolumeArray, uint changedChannel,
            nint eventContext);
        [PreserveSig] int OnGroupingParamChanged(nint newGroupingParam, nint eventContext);
        [PreserveSig] int OnStateChanged(int newState);
        [PreserveSig] int OnSessionDisconnected(int disconnectReason);
    }
}
