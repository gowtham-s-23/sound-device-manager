using System.Runtime.InteropServices;

namespace AudioSwitch;

enum AudioFlow
{
    Render,
    Capture,
}

sealed record AudioEndpoint(string Id, string Name, bool IsDefault);

static class AudioDevices
{
    const uint Active = 0x1;
    const int ElementNotFound = unchecked((int)0x80070490);

    public static IReadOnlyList<AudioEndpoint> List(AudioFlow flow)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        try
        {
            var dataFlow = ToDataFlow(flow);
            var defaultId = TryGetDefaultId(enumerator, dataFlow);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(dataFlow, Active, out var collection));
            try
            {
                Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                var devices = new List<AudioEndpoint>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    Marshal.ThrowExceptionForHR(collection.Item(i, out var device));
                    try
                    {
                        Marshal.ThrowExceptionForHR(device.GetId(out var id));
                        var name = ReadFriendlyName(device);
                        devices.Add(new AudioEndpoint(id, name, id == defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }

                return Disambiguate(devices);
            }
            finally
            {
                Marshal.ReleaseComObject(collection);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    public static void SetDefault(string deviceId)
    {
        var policy = (IPolicyConfig)new PolicyConfigClient();
        try
        {
            foreach (var role in new[] { ERole.Console, ERole.Multimedia, ERole.Communications })
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, role));
            }
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    public static int SelfTest(TextWriter output)
    {
        var failed = false;
        failed |= Exercise(output, AudioFlow.Render, "Speakers");
        failed |= Exercise(output, AudioFlow.Capture, "Microphones");
        output.WriteLine(failed ? "FAIL" : "PASS");
        return failed ? 1 : 0;
    }

    static bool Exercise(TextWriter output, AudioFlow flow, string label)
    {
        IReadOnlyList<AudioEndpoint> devices;
        try
        {
            devices = List(flow);
        }
        catch (Exception ex)
        {
            output.WriteLine($"{label}: list failed: {ex.Message}");
            return true;
        }

        output.WriteLine($"{label}: {devices.Count}");
        foreach (var device in devices)
            output.WriteLine($"  {(device.IsDefault ? "*" : " ")} {device.Name}");

        if (devices.Count == 0)
            return false;

        if (devices.Count(device => device.IsDefault) != 1)
        {
            output.WriteLine("  expected exactly one default");
            return true;
        }

        var original = devices.First(device => device.IsDefault);
        var other = devices.FirstOrDefault(device => !device.IsDefault);
        if (other == null)
        {
            output.WriteLine("  only one device; skip switch");
            return false;
        }

        var failed = false;
        try
        {
            SetDefault(other.Id);
            if (!WaitUntilDefault(flow, other.Id))
            {
                output.WriteLine("  switch did not stick");
                failed = true;
            }
            else
            {
                output.WriteLine($"  switched to {other.Name}");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"  switch failed: {ex.Message}");
            failed = true;
        }
        finally
        {
            try
            {
                SetDefault(original.Id);
                if (!WaitUntilDefault(flow, original.Id))
                {
                    output.WriteLine("  restore did not stick");
                    failed = true;
                }
                else
                {
                    output.WriteLine($"  restored {original.Name}");
                }
            }
            catch (Exception ex)
            {
                output.WriteLine($"  restore failed: {ex.Message}");
                failed = true;
            }
        }

        return failed;
    }

    static bool WaitUntilDefault(AudioFlow flow, string id)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (List(flow).Any(device => device.Id == id && device.IsDefault))
                return true;
            Thread.Sleep(100);
        }

        return false;
    }

    static EDataFlow ToDataFlow(AudioFlow flow) =>
        flow == AudioFlow.Render ? EDataFlow.Render : EDataFlow.Capture;

    static string? TryGetDefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.Console, out var device);
        if (hr == ElementNotFound)
            return null;
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            Marshal.ThrowExceptionForHR(device.GetId(out var id));
            return id;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    static string ReadFriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out var store));
        try
        {
            var key = new PropertyKey
            {
                FormatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
                PropertyId = 14,
            };
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try
            {
                string? name = null;
                if (value.VariantType == 31 && value.Pointer != IntPtr.Zero)
                    name = Marshal.PtrToStringUni(value.Pointer);

                name = name?.ReplaceLineEndings(" ").Trim();
                return string.IsNullOrEmpty(name) ? "Unknown device" : name;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    static IReadOnlyList<AudioEndpoint> Disambiguate(List<AudioEndpoint> devices)
    {
        var totals = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var device in devices)
        {
            totals.TryGetValue(device.Name, out var count);
            totals[device.Name] = count + 1;
        }

        var seen = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var result = new List<AudioEndpoint>(devices.Count);
        foreach (var device in devices)
        {
            var name = device.Name;
            if (totals[name] > 1)
            {
                seen.TryGetValue(name, out var index);
                index++;
                seen[name] = index;
                if (index > 1)
                    name = $"{device.Name} ({index})";
            }

            result.Add(device with { Name = name });
        }

        return result;
    }

    [DllImport("ole32.dll")]
    static extern int PropVariantClear(ref PropVariant value);

    enum EDataFlow
    {
        Render = 0,
        Capture = 1,
    }

    enum ERole
    {
        Console = 0,
        Multimedia = 1,
        Communications = 2,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr Pointer;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, uint classContext, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(uint access, out IPropertyStore store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    // Undocumented policy object Windows Settings uses to change the default endpoint.
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    class PolicyConfigClient
    {
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr endpointFormat, IntPtr mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr period);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceName, IntPtr mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool fxStore, IntPtr key, IntPtr value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceName, ERole role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceName, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
