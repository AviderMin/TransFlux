using System.Runtime.InteropServices;
using Microsoft.Win32;
using TransFlux.Domain.Media;

namespace TransFlux.Hardware.Windows;

/// <summary>Enumerates display adapters through DXGI (IDXGIFactory1/IDXGIAdapter1), surfacing the
/// LUID, PCI vendor/device ids, dedicated memory, driver version and software-adapter flag.</summary>
public static class DxgiAdapterEnumerator
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint DxgiAdapterFlagSoftware = 2;

    public static IReadOnlyList<AdapterInfo> Enumerate()
    {
        var result = new List<AdapterInfo>();

        var iid = typeof(IDXGIFactory1).GUID;
        var hr = CreateDXGIFactory1(ref iid, out var factoryPtr);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        try
        {
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            try
            {
                for (uint index = 0; ; index++)
                {
                    hr = factory.EnumAdapters1(index, out var adapter);
                    if (hr == DxgiErrorNotFound) break;
                    if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                    try
                    {
                        adapter.GetDesc1(out var desc);
                        var luid = ((long)desc.AdapterLuid.HighPart << 32) | (uint)desc.AdapterLuid.LowPart;
                        var maxDedicated = unchecked((nuint)long.MaxValue);
                        result.Add(new AdapterInfo
                        {
                            AdapterId = $"LUID-{luid:X16}",
                            Name = (desc.Description ?? string.Empty).Trim(),
                            Vendor = MapVendor(desc.VendorId),
                            VendorId = desc.VendorId,
                            DeviceId = desc.DeviceId,
                            DriverVersion = TryGetDriverVersion(desc.VendorId, desc.DeviceId),
                            DedicatedMemoryBytes = desc.DedicatedVideoMemory > maxDedicated
                                ? null
                                : (long)desc.DedicatedVideoMemory,
                            IsSoftware = (desc.Flags & DxgiAdapterFlagSoftware) != 0,
                            Luid = luid,
                        });
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(adapter);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }

        return result;
    }

    private static AdapterVendor MapVendor(uint vendorId) => vendorId switch
    {
        0x10DE => AdapterVendor.Nvidia,
        0x8086 => AdapterVendor.Intel,
        0x1002 => AdapterVendor.Amd,
        0x1414 => AdapterVendor.Microsoft,
        _ => AdapterVendor.Unknown,
    };

    private static string? TryGetDriverVersion(uint vendorId, uint deviceId)
    {
        try
        {
            var match = $@"PCI\VEN_{vendorId:X4}&DEV_{deviceId:X4}";
            using var classKey = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null) return null;

            foreach (var subName in classKey.GetSubKeyNames())
            {
                using var sub = classKey.OpenSubKey(subName);
                if (sub?.GetValue("MatchingDeviceId") is string matching &&
                    matching.StartsWith(match, StringComparison.OrdinalIgnoreCase))
                {
                    return sub.GetValue("DriverVersion") as string;
                }
            }
        }
        catch
        {
            // Registry shape differs by driver; driver version is best-effort metadata.
        }
        return null;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // NOTE: COM interfaces are flattened (all inherited methods redeclared in vtable order) rather
    // than using interface inheritance, which .NET COM interop does not lay out reliably.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr pParent);
        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr pParent);
        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(out DxgiAdapterDesc pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long pUMDVersion);
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out DxgiAdapterDesc1 pDesc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }
}
