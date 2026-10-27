using System.Runtime.InteropServices;

namespace FxAuto.Browser;

// Minimal COM declarations of UI Automation (UIAutomationCore.dll), only the vtable slots FxAuto calls.
// Slot order verified against the type library embedded in C:\Windows\System32\UIAutomationCore.dll
// (2026-10-25, task 4c). Unused slots are placeholders (SlotNN, never called) that only keep the vtable layout.
// Why COM and not System.Windows.Automation: the managed client grew ~20 KB of native memory per read with no
// plateau (+55 MB after 1500 reads), see plan "Spike outcomes" 4c.
#pragma warning disable CA1707, IDE1006 // placeholder names
#pragma warning disable SYSLIB1096 // built-in COM interop on purpose (no source generator setup, no NuGet)

internal static class UiaIds
{
    public static readonly Guid ClsidCUIAutomation8 = new("e22ad333-b25f-460c-83d0-0581107395c9");

    public const int ControlTypePropertyId = 30003;
    public const int NamePropertyId = 30005;
    public const int SelectionItemIsSelectedPropertyId = 30079;

    public const int TabItemControlTypeId = 50019;
    public const int DocumentControlTypeId = 50030;

    /// <summary>UIA_E_TIMEOUT (ConnectionTimeout / TransactionTimeout expired).</summary>
    public const int UiaETimeout = unchecked((int)0x80131505);
}

/// <summary>IUIAutomation (55 methods) + IUIAutomation2 (6), flattened in vtable order.</summary>
[ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2
{
    void Slot03(); // CompareElements
    void Slot04(); // CompareRuntimeIds
    void Slot05(); // GetRootElement
    [PreserveSig] int ElementFromHandle(nint hwnd, out IUIAutomationElement? element);
    void Slot07(); // ElementFromPoint
    void Slot08(); // GetFocusedElement
    void Slot09(); // GetRootElementBuildCache
    void Slot10(); // ElementFromHandleBuildCache
    void Slot11(); // ElementFromPointBuildCache
    void Slot12(); // GetFocusedElementBuildCache
    void Slot13(); // CreateTreeWalker
    [PreserveSig] int get_ControlViewWalker(out IUIAutomationTreeWalker? walker);
    void Slot15(); void Slot16(); void Slot17(); void Slot18(); void Slot19();
    [PreserveSig] int CreateCacheRequest(out IUIAutomationCacheRequest? cacheRequest);
    void Slot21(); void Slot22(); void Slot23(); void Slot24(); void Slot25(); void Slot26(); void Slot27();
    void Slot28(); void Slot29(); void Slot30(); void Slot31(); void Slot32(); void Slot33(); void Slot34();
    void Slot35(); void Slot36(); void Slot37(); void Slot38(); void Slot39(); void Slot40(); void Slot41();
    void Slot42(); void Slot43(); void Slot44(); void Slot45(); void Slot46(); void Slot47(); void Slot48();
    void Slot49(); void Slot50(); void Slot51(); void Slot52(); void Slot53(); void Slot54(); void Slot55();
    void Slot56(); void Slot57();
    // ---- IUIAutomation2 ----
    void Slot58(); // get_AutoSetFocus
    void Slot59(); // put_AutoSetFocus
    void Slot60(); // get_ConnectionTimeout
    [PreserveSig] int put_ConnectionTimeout(uint timeoutMs);
    void Slot62(); // get_TransactionTimeout
    [PreserveSig] int put_TransactionTimeout(uint timeoutMs);
}

/// <summary>IUIAutomationElement, only up to GetCachedPropertyValue (slot 12).</summary>
[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void Slot03(); // SetFocus — never called
    void Slot04(); // GetRuntimeId
    void Slot05(); // FindFirst
    void Slot06(); // FindAll
    void Slot07(); // FindFirstBuildCache
    void Slot08(); // FindAllBuildCache
    void Slot09(); // BuildUpdatedCache
    void Slot10(); // GetCurrentPropertyValue
    void Slot11(); // GetCurrentPropertyValueEx
    [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
}

[ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCacheRequest
{
    [PreserveSig] int AddProperty(int propertyId);
}

[ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationTreeWalker
{
    void Slot03(); void Slot04(); void Slot05(); void Slot06(); void Slot07(); void Slot08(); void Slot09();
    [PreserveSig] int GetFirstChildElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest,
        out IUIAutomationElement? first);
    void Slot11(); // GetLastChildElementBuildCache
    [PreserveSig] int GetNextSiblingElementBuildCache(IUIAutomationElement element, IUIAutomationCacheRequest cacheRequest,
        out IUIAutomationElement? next);
}

#pragma warning restore SYSLIB1096
#pragma warning restore CA1707, IDE1006

internal static class Com
{
    /// <summary>Releases one reference of a runtime callable wrapper (null and non-COM objects are ignored).</summary>
    public static void Release(object? o)
    {
        if (o is not null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o);
    }
}
