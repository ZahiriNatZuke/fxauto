using System.Runtime.InteropServices;
using FxAuto.Core.Devices;

namespace FxAuto.Devices;

/// <summary>Reads render endpoints and the Windows defaults through IMMDeviceEnumerator (read-only: never changes a
/// device or a default). Call it from an MTA thread (thread pool or the monitor's timers).</summary>
internal static class EndpointEnumerator
{
    /// <summary>Active, disabled and unplugged endpoints. NOTPRESENT is left out: it is the history Windows keeps of
    /// every device ever seen (research 02 §1.1), not something the user can pick.</summary>
    public const int StateMask = EndpointMapping.StateActive | EndpointMapping.StateDisabled | EndpointMapping.StateUnplugged;

    public static EndpointInterop.IMMDeviceEnumerator Create()
    {
        var type = Type.GetTypeFromCLSID(EndpointInterop.ClsidMMDeviceEnumerator, throwOnError: true)!;
        return (EndpointInterop.IMMDeviceEnumerator)Activator.CreateInstance(type)!;
    }

    /// <summary>Full snapshot. Endpoints whose properties cannot be read are skipped (logged by the caller through
    /// <paramref name="onError"/>).</summary>
    public static EndpointSnapshot Read(EndpointInterop.IMMDeviceEnumerator e, Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var raw = ReadRaw(e, onError);
        return new EndpointSnapshot(DateTimeOffset.Now, EndpointMapping.Sort(raw.Select(EndpointMapping.ToInfo)),
            DefaultId(e, EndpointInterop.ERoleMultimedia), DefaultId(e, EndpointInterop.ERoleConsole));
    }

    public static List<RawEndpoint> ReadRaw(EndpointInterop.IMMDeviceEnumerator e, Action<string>? onError = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var list = new List<RawEndpoint>();
        int hr = e.EnumAudioEndpoints(EndpointInterop.ERender, StateMask, out var coll);
        if (hr != 0 || coll is null)
        {
            onError?.Invoke($"EnumAudioEndpoints 0x{hr:X8}");
            return list;
        }
        try
        {
            if (coll.GetCount(out int n) != 0) return list;
            for (int i = 0; i < n; i++)
            {
                if (coll.Item(i, out var dev) != 0 || dev is null) continue;
                try
                {
                    var r = ReadDevice(dev);
                    if (r is not null) list.Add(r);
                }
                catch (COMException ex) { onError?.Invoke($"endpoint {i}: 0x{ex.HResult:X8}"); }
                finally { EndpointInterop.Release(dev); }
            }
        }
        finally { EndpointInterop.Release(coll); }
        return list;
    }

    private static RawEndpoint? ReadDevice(EndpointInterop.IMMDevice dev)
    {
        if (dev.GetId(out var id) != 0 || string.IsNullOrEmpty(id)) return null;
        dev.GetState(out int state);
        if (dev.OpenPropertyStore(EndpointInterop.StgmRead, out var store) != 0 || store is null)
            return new RawEndpoint(id, null, null, null, null, null, null, state);
        try
        {
            return new RawEndpoint(id,
                EndpointInterop.ReadString(store, EndpointInterop.PkeyFriendlyName),
                EndpointInterop.ReadString(store, EndpointInterop.PkeyDeviceDesc),
                EndpointInterop.ReadString(store, EndpointInterop.PkeyInterfaceFriendlyName),
                EndpointInterop.ReadUInt(store, EndpointInterop.PkeyFormFactor),
                EndpointInterop.ReadGuid(store, EndpointInterop.PkeyContainerId),
                EndpointInterop.ReadString(store, EndpointInterop.PkeyEnumeratorName),
                state);
        }
        finally { EndpointInterop.Release(store); }
    }

    private static string? DefaultId(EndpointInterop.IMMDeviceEnumerator e, int role)
    {
        // E_NOTFOUND when there is no render endpoint at all.
        if (e.GetDefaultAudioEndpoint(EndpointInterop.ERender, role, out var dev) != 0 || dev is null) return null;
        try { return dev.GetId(out var id) == 0 ? id : null; }
        finally { EndpointInterop.Release(dev); }
    }
}

/// <summary>Hand-written MMDevice COM interop for the device layer (GUIDs and vtable order from mmdeviceapi.h,
/// propsys.h; property keys from functiondiscoverykeys_devpkey.h / mmdeviceapi.h, verified in research 02 §1).</summary>
internal static class EndpointInterop
{
    public static readonly Guid ClsidMMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public const int ERender = 0;
    public const int ERoleConsole = 0;
    public const int ERoleMultimedia = 1;
    public const int StgmRead = 0;

    private const short VtUi4 = 19;
    private const short VtLpwstr = 31;
    private const short VtClsid = 72;

    private static readonly Guid DevPkeyBase = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    /// <summary>PKEY_Device_DeviceDesc: endpoint ("jack") name.</summary>
    public static readonly PropertyKey PkeyDeviceDesc = new(DevPkeyBase, 2);
    /// <summary>PKEY_Device_FriendlyName: "&lt;DeviceDesc&gt; (&lt;adapter&gt;)".</summary>
    public static readonly PropertyKey PkeyFriendlyName = new(DevPkeyBase, 14);
    /// <summary>PKEY_Device_EnumeratorName: bus.</summary>
    public static readonly PropertyKey PkeyEnumeratorName = new(DevPkeyBase, 24);
    /// <summary>PKEY_DeviceInterface_FriendlyName: adapter name.</summary>
    public static readonly PropertyKey PkeyInterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    /// <summary>PKEY_AudioEndpoint_FormFactor (VT_UI4).</summary>
    public static readonly PropertyKey PkeyFormFactor = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    /// <summary>PKEY_Device_ContainerId (VT_CLSID).</summary>
    public static readonly PropertyKey PkeyContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    public static string? ReadString(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) != 0) return null;
        try { return v.Vt == VtLpwstr && v.Pointer != 0 ? Marshal.PtrToStringUni(v.Pointer) : null; }
        finally { PropVariantClear(ref v); }
    }

    public static uint? ReadUInt(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) != 0) return null;
        try { return v.Vt == VtUi4 ? unchecked((uint)(long)v.Pointer) : null; }
        finally { PropVariantClear(ref v); }
    }

    public static Guid? ReadGuid(IPropertyStore store, PropertyKey key)
    {
        if (store.GetValue(ref key, out var v) != 0) return null;
        try { return v.Vt == VtClsid && v.Pointer != 0 ? Marshal.PtrToStructure<Guid>(v.Pointer) : null; }
        finally { PropVariantClear(ref v); }
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
    public readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly int PropertyId = propertyId;
    }

    /// <summary>PROPVARIANT: vt at 0, value union at 8 (VT_UI4 in the low 4 bytes, pointers for LPWSTR/CLSID).</summary>
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

    /// <summary>Implemented by DeviceMonitor. Callbacks arrive on audio service threads: only enqueue, never block.</summary>
    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }
}
