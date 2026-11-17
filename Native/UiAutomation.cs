using System.Runtime.InteropServices;

namespace TaskbarPlayer.Native;

// Slot order as in the type library of UIAutomationCore.dll.
// Methods we never call are declared without parameters: only their position matters.

[ComImport, Guid("e22ad333-b25f-460c-83d0-0581107395c9")]
internal class CUIAutomation8
{
}

[ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCondition
{
}

[ComImport, Guid("b32a92b5-bc25-4078-9c08-d7ee95c48e03"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationCacheRequest
{
    [PreserveSig] int AddProperty(int propertyId);
    void AddPattern();
    void Clone();
    void get_TreeScope();
    void put_TreeScope();
    void get_TreeFilter();
    void put_TreeFilter();
    void get_AutomationElementMode();
    [PreserveSig] int put_AutomationElementMode(int mode);
}

[ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElementArray
{
    [PreserveSig] int get_Length(out int length);
    [PreserveSig] int GetElement(int index, out IUIAutomationElement element);
}

[ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomationElement
{
    void SetFocus();
    void GetRuntimeId();
    void FindFirst();
    void FindAll();
    void FindFirstBuildCache();
    [PreserveSig]
    int FindAllBuildCache(int scope, IUIAutomationCondition condition, IUIAutomationCacheRequest cacheRequest, out IUIAutomationElementArray? found);
    void BuildUpdatedCache();
    void GetCurrentPropertyValue();
    void GetCurrentPropertyValueEx();
    [PreserveSig] int GetCachedPropertyValue(int propertyId, [MarshalAs(UnmanagedType.Struct)] out object? value);
}

[ComImport, Guid("34723aff-0c9d-49d0-9896-7ab52df8cd8a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IUIAutomation2
{
    void CompareElements();
    void CompareRuntimeIds();
    void GetRootElement();
    [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement? element);
    void ElementFromPoint();
    void GetFocusedElement();
    void GetRootElementBuildCache();
    void ElementFromHandleBuildCache();
    void ElementFromPointBuildCache();
    void GetFocusedElementBuildCache();
    void CreateTreeWalker();
    void get_ControlViewWalker();
    void get_ContentViewWalker();
    void get_RawViewWalker();
    void get_RawViewCondition();
    void get_ControlViewCondition();
    void get_ContentViewCondition();
    [PreserveSig] int CreateCacheRequest(out IUIAutomationCacheRequest cacheRequest);
    void CreateTrueCondition();
    void CreateFalseCondition();
    [PreserveSig]
    int CreatePropertyCondition(int propertyId, [In, MarshalAs(UnmanagedType.Struct)] object value, out IUIAutomationCondition condition);
    void CreatePropertyConditionEx();
    void CreateAndCondition();
    void CreateAndConditionFromArray();
    void CreateAndConditionFromNativeArray();
    [PreserveSig]
    int CreateOrCondition(IUIAutomationCondition condition1, IUIAutomationCondition condition2, out IUIAutomationCondition condition);
    void CreateOrConditionFromArray();
    void CreateOrConditionFromNativeArray();
    void CreateNotCondition();
    void AddAutomationEventHandler();
    void RemoveAutomationEventHandler();
    void AddPropertyChangedEventHandlerNativeArray();
    void AddPropertyChangedEventHandler();
    void RemovePropertyChangedEventHandler();
    void AddStructureChangedEventHandler();
    void RemoveStructureChangedEventHandler();
    void AddFocusChangedEventHandler();
    void RemoveFocusChangedEventHandler();
    void RemoveAllEventHandlers();
    void IntNativeArrayToSafeArray();
    void IntSafeArrayToNativeArray();
    void RectToVariant();
    void VariantToRect();
    void SafeArrayToRectNativeArray();
    void CreateProxyFactoryEntry();
    void get_ProxyFactoryMapping();
    void GetPropertyProgrammaticName();
    void GetPatternProgrammaticName();
    void PollForPotentialSupportedPatterns();
    void PollForPotentialSupportedProperties();
    void CheckNotSupported();
    void get_ReservedNotSupportedValue();
    void get_ReservedMixedAttributeValue();
    void ElementFromIAccessible();
    void ElementFromIAccessibleBuildCache();
    void get_AutoSetFocus();
    void put_AutoSetFocus();
    [PreserveSig] int get_ConnectionTimeout(out uint milliseconds);
    [PreserveSig] int put_ConnectionTimeout(uint milliseconds);
    [PreserveSig] int get_TransactionTimeout(out uint milliseconds);
    [PreserveSig] int put_TransactionTimeout(uint milliseconds);
}
