using System;
using System.Runtime.InteropServices;
using System.IO;
using System.Reflection;

namespace ProxyBridge.GUI.Interop;

public static class ProxyBridgeNative
{
    private const string DllName = "ProxyBridgeCore.dll";

    static ProxyBridgeNative()
    {
        // Resolve the routing core ONLY from our own application directory.
        // Never let the OS fall back to PATH — when an original ProxyBridge is
        // installed in parallel, its directory is on PATH and a default search
        // would load its (possibly incompatible) native core into our process.
        NativeLibrary.SetDllImportResolver(typeof(ProxyBridgeNative).Assembly, ResolveNativeLibrary);
    }

    private static IntPtr ResolveNativeLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase))
            return IntPtr.Zero; // default resolution for any other library

        var dllPath = Path.Combine(AppContext.BaseDirectory, DllName);
        if (File.Exists(dllPath))
            return NativeLibrary.Load(dllPath);

        // Do not fall back to PATH; signal a clean, catchable failure instead.
        throw new DllNotFoundException(
            $"{DllName} was not found next to the application. The native routing core is required for traffic routing.");
    }

    public enum ProxyType
    {
        HTTP = 0,
        SOCKS5 = 1
    }

    public enum RuleAction
    {
        PROXY = 0,
        DIRECT = 1,
        BLOCK = 2
    }

    public enum RuleProtocol
    {
        TCP = 0,
        UDP = 1,
        BOTH = 2
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void LogCallback([MarshalAs(UnmanagedType.LPStr)] string message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ConnectionCallback(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
        uint pid,
        [MarshalAs(UnmanagedType.LPStr)] string destIp,
        ushort destPort,
        [MarshalAs(UnmanagedType.LPStr)] string proxyInfo);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ProxyBridge_AddRule(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
        [MarshalAs(UnmanagedType.LPStr)] string targetHosts,
        [MarshalAs(UnmanagedType.LPStr)] string targetPorts,
        RuleProtocol protocol,
        RuleAction action,
        uint proxyConfigId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_EnableRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_DisableRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_DeleteRule(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_EditRule(
        uint ruleId,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string processName,
        [MarshalAs(UnmanagedType.LPStr)] string targetHosts,
        [MarshalAs(UnmanagedType.LPStr)] string targetPorts,
        RuleProtocol protocol,
        RuleAction action,
        uint proxyConfigId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_SetRuleFullCone(uint ruleId, [MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint ProxyBridge_GetRulePosition(uint ruleId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_MoveRuleToPosition(uint ruleId, uint newPosition);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ProxyBridge_AddProxyConfig")]
    private static extern uint ProxyBridge_AddProxyConfig_Native(
        ProxyType type,
        [MarshalAs(UnmanagedType.LPStr)] string proxyIp,
        ushort proxyPort,
        [MarshalAs(UnmanagedType.LPStr)] string username,
        [MarshalAs(UnmanagedType.LPStr)] string password);

    public static uint ProxyBridge_AddProxyConfig(
        ProxyType type,
        string proxyIp,
        ushort proxyPort,
        string username,
        string password)
    {
        if (string.IsNullOrWhiteSpace(proxyIp))
            throw new ArgumentException("Proxy IP must not be null or empty.", nameof(proxyIp));
        username ??= string.Empty;
        password ??= string.Empty;
        return ProxyBridge_AddProxyConfig_Native(type, proxyIp, proxyPort, username, password);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "ProxyBridge_EditProxyConfig")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProxyBridge_EditProxyConfig_Native(
        uint configId,
        ProxyType type,
        [MarshalAs(UnmanagedType.LPStr)] string proxyIp,
        ushort proxyPort,
        [MarshalAs(UnmanagedType.LPStr)] string username,
        [MarshalAs(UnmanagedType.LPStr)] string password);

    public static bool ProxyBridge_EditProxyConfig(
        uint configId,
        ProxyType type,
        string proxyIp,
        ushort proxyPort,
        string username,
        string password)
    {
        username ??= string.Empty;
        password ??= string.Empty;
        return ProxyBridge_EditProxyConfig_Native(configId, type, proxyIp, proxyPort, username, password);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_DeleteProxyConfig(uint configId);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi, EntryPoint = "ProxyBridge_TestProxyConfig")]
    private static extern int ProxyBridge_TestProxyConfig_Native(
        uint configId,
        [MarshalAs(UnmanagedType.LPStr)] string targetHost,
        ushort targetPort,
        [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder resultBuffer,
        UIntPtr bufferSize);

    public static int ProxyBridge_TestProxyConfig(
        uint configId,
        string targetHost,
        ushort targetPort,
        System.Text.StringBuilder resultBuffer,
        UIntPtr bufferSize)
    {
        ArgumentNullException.ThrowIfNull(targetHost);
        ArgumentNullException.ThrowIfNull(resultBuffer);
        var effectiveSize = bufferSize == UIntPtr.Zero ? (UIntPtr)resultBuffer.Capacity : bufferSize;
        return ProxyBridge_TestProxyConfig_Native(configId, targetHost, targetPort, resultBuffer, effectiveSize);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetLogCallback(LogCallback callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetConnectionCallback(ConnectionCallback callback);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetTrafficLoggingEnabled([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void ProxyBridge_SetLocalhostViaProxy([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_Start();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ProxyBridge_Stop();

}
