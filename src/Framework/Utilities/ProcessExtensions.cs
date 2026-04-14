// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
#if !NET || !TARGET_WINDOWS
using Microsoft.Build.Framework;
#endif

#if NET
using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
#endif
#if NET && TARGET_WINDOWS
using Microsoft.Build.Shared.Win32;
using Microsoft.Build.Shared.Win32.Wmi;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Variant;
using IWbemClassObject = Microsoft.Build.Shared.Win32.Wmi.IWbemClassObject;
using IWbemLocator = Microsoft.Build.Shared.Win32.Wmi.IWbemLocator;
using IWbemServices = Microsoft.Build.Shared.Win32.Wmi.IWbemServices;
#endif

namespace Microsoft.Build.Shared
{
    internal static partial class ProcessExtensions
    {
        public static void KillTree(this Process process, int timeoutMilliseconds)
        {
#if NET
            process.Kill(entireProcessTree: true);
#else
            if (NativeMethods.IsWindows)
            {
                try
                {
                    NativeMethods.KillTree(process.Id);
                }
                catch (InvalidOperationException)
                {
                    // The process already exited, which is fine,
                    // just continue.
                }
            }
            else
            {
                throw new NotSupportedException();
            }
#endif
            // Wait until the process finishes exiting/getting killed.
            // We don't want to wait forever here because the task is already supposed to be dying, we just want to give it long enough
            // to try and flush what it can and stop. If it cannot do that in a reasonable time frame then we will just ignore it.
            process.WaitForExit(timeoutMilliseconds);
        }

        /// <summary>
        /// Retrieves the full command line for a process in a cross-platform manner.
        /// </summary>
        /// <param name="process">The process to get the command line for.</param>
        /// <param name="commandLine">The command line string, or null if it cannot be retrieved.</param>
        /// <returns>True if the command line was successfully retrieved, false if there was an error or the platform doesn't support command line retrieval.</returns>
        public static bool TryGetCommandLine(this Process? process, out string? commandLine)
        {
            commandLine = null;

            if (process?.HasExited != false)
            {
                return false;
            }

            try
            {
#if NET && TARGET_WINDOWS
                commandLine = Windows.GetCommandLine(process.Id);
                return true;
#elif NET
                if (NativeMethods.IsOSX || NativeMethods.IsBSD)
                {
                    commandLine = BSD.GetCommandLine(process.Id);
                    return true;
                }
                else if (NativeMethods.IsLinux)
                {
                    commandLine = Linux.GetCommandLine(process.Id);
                    return true;
                }
                else
                {
                    // Unsupported OS - return false to fall back to prior behavior
                    commandLine = null;
                    return true;
                }
#else
                // While we technically can do the same COM interop on .NET Framework that we do on modern .NET, VS perf tests yell at us for more assembly loads.
                // Out of deference to those tests, we artificially limit the functionality to just modern .NET.
                commandLine = null;
                return true;
#endif
            }
            catch
            {
                return false;
            }
        }

#if NET
        /// <summary>
        /// Parses a null-separated byte buffer into a space-joined argument string using span-based slicing.
        /// Used by both Linux (/proc/pid/cmdline) and macOS/BSD (sysctl KERN_PROCARGS2) parsing.
        /// Uses ArrayPool to rent char buffers for efficient UTF-8 decoding without intermediate string allocations.
        /// </summary>
        private static string ParseNullSeparatedArguments(ReadOnlySpan<byte> data, int maxArgs = int.MaxValue)
        {
            if (data.IsEmpty)
            {
                return string.Empty;
            }

            // Rent a char buffer for UTF-8 decoding (max char count equals byte count for ASCII-like content)
            char[] charBuffer = ArrayPool<char>.Shared.Rent(data.Length);
            try
            {
                int totalChars = 0;
                int argsFound = 0;

                while (!data.IsEmpty && argsFound < maxArgs)
                {
                    int nullIndex = data.IndexOf((byte)0);
                    ReadOnlySpan<byte> segment = nullIndex >= 0 ? data.Slice(0, nullIndex) : data;

                    if (!segment.IsEmpty)
                    {
                        // Add space separator between arguments
                        if (totalChars > 0)
                        {
                            charBuffer[totalChars++] = ' ';
                        }

                        // Decode UTF-8 directly into the char buffer
                        int charsWritten = Encoding.UTF8.GetChars(segment, charBuffer.AsSpan(totalChars));

                        // UTF-8 decoder converts null bytes to null chars - replace them with spaces for safety
                        Span<char> decodedChars = charBuffer.AsSpan(totalChars, charsWritten);
                        for (int i = 0; i < decodedChars.Length; i++)
                        {
                            if (decodedChars[i] == '\0')
                            {
                                decodedChars[i] = ' ';
                            }
                        }

                        totalChars += charsWritten;
                        argsFound++;
                    }

                    if (nullIndex < 0)
                    {
                        break;
                    }

                    data = data.Slice(nullIndex + 1);
                }

                return new string(charBuffer, 0, totalChars);
            }
            finally
            {
                ArrayPool<char>.Shared.Return(charBuffer);
            }
        }
#endif // TARGET_WINDOWS

#if NET && TARGET_WINDOWS
        /// <summary>
        /// Windows-specific command line retrieval via WMI COM interfaces.
        /// Queries Win32_Process for the CommandLine property using IWbemLocator/IWbemServices.
        /// Uses CsWin32-generated P/Invoke for ole32.dll functions and manually defined COM structs
        /// for WMI interfaces (which are not in Win32 metadata).
        /// </summary>
        [SupportedOSPlatform("windows")]
        private static class Windows
        {
            // WBEM status codes
            private static readonly HRESULT WBEM_S_FALSE = (HRESULT)1; // No more objects in enumeration
            private const int WBEM_FLAG_FORWARD_ONLY = 0x00000020;
            private const int WBEM_FLAG_RETURN_IMMEDIATELY = 0x00000010;
            private const int WBEM_INFINITE = -1;

            /// <summary>
            /// Retrieves the command line for a process by querying WMI Win32_Process via COM.
            /// Runs: SELECT CommandLine FROM Win32_Process WHERE ProcessId='<paramref name="processId"/>'
            /// </summary>
            [UnconditionalSuppressMessage("Trimming", "IL2050", Justification = "COM interop is required for WMI process queries and the interfaces are fully defined in this file.")]
            internal static unsafe string? GetCommandLine(int processId)
            {
                HRESULT hr = PInvoke.CoInitializeSecurity(
                    default,
                    -1,
                    null,
                    null,
                    RPC_C_AUTHN_LEVEL.RPC_C_AUTHN_LEVEL_DEFAULT,
                    RPC_C_IMP_LEVEL.RPC_C_IMP_LEVEL_IMPERSONATE,
                    null,
                    EOLE_AUTHENTICATION_CAPABILITIES.EOAC_NONE,
                    null);
                // RPC_E_TOO_LATE (0x80010119) means another call already set security — not fatal.
                if (hr.Failed && hr != HRESULT.RPC_E_TOO_LATE)
                {
                    throw new InvalidOperationException(
                        $"WMI CoInitializeSecurity failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                Guid clsid = IWbemLocator.CLSID;
                Guid iid = IWbemLocator.Guid;
                IWbemLocator* pLocator;
                hr = PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&pLocator);
                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI CoCreateInstance failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                using ComScope<IWbemLocator> locator = new(pLocator);

                IWbemServices* pServices;
                fixed (char* networkResource = @"ROOT\CIMV2")
                {
                    hr = locator.Pointer->ConnectServer(
                        networkResource,
                        strUser: null, strPassword: null, strLocale: null,
                        lSecurityFlags: 0, strAuthority: null,
                        pCtx: null,
                        &pServices);
                }

                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI ConnectServer failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                using ComScope<IWbemServices> services = new(pServices);

                hr = PInvoke.CoSetProxyBlanket(
                    (IUnknown*)services.Pointer,
                    0x0A, // RPC_C_AUTHN_WINNT
                    0, // RPC_C_AUTHZ_NONE
                    default,
                    RPC_C_AUTHN_LEVEL.RPC_C_AUTHN_LEVEL_CALL,
                    RPC_C_IMP_LEVEL.RPC_C_IMP_LEVEL_IMPERSONATE,
                    null,
                    EOLE_AUTHENTICATION_CAPABILITIES.EOAC_NONE);
                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI CoSetProxyBlanket failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                string query = $"SELECT CommandLine FROM Win32_Process WHERE ProcessId='{processId}'";
                IEnumWbemClassObject* pEnumerator;
#pragma warning disable SA1519 // Braces should not be omitted from multi-line child statement
                fixed (char* queryLanguage = "WQL")
                fixed (char* queryStr = query)
#pragma warning restore SA1519
                {
                    hr = services.Pointer->ExecQuery(
                        queryLanguage,
                        queryStr,
                        WBEM_FLAG_FORWARD_ONLY | WBEM_FLAG_RETURN_IMMEDIATELY,
                        pCtx: null,
                        &pEnumerator);
                }

                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI ExecQuery failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                using ComScope<IEnumWbemClassObject> enumerator = new(pEnumerator);

                IWbemClassObject* pObj;
                uint returned;
                hr = enumerator.Pointer->Next(WBEM_INFINITE, 1, &pObj, &returned);
                if (hr == WBEM_S_FALSE || returned == 0)
                {
                    // No matching process found.
                    return null;
                }

                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI IEnumWbemClassObject.Next failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                using ComScope<IWbemClassObject> obj = new(pObj);

                using VARIANT val = default;
                fixed (char* propName = "CommandLine")
                {
                    hr = obj.Pointer->Get(propName, 0, &val, pType: null, plFlavor: null);
                }

                if (hr.Failed)
                {
                    throw new InvalidOperationException(
                        $"WMI IWbemClassObject.Get(\"CommandLine\") failed for PID {processId}. HRESULT: 0x{hr.Value:X8}");
                }

                if (val.Anonymous.Anonymous.vt == VARENUM.VT_BSTR)
                {
                    return val.Anonymous.Anonymous.Anonymous.bstrVal.ToString();
                }

                return null;
            }
        }
#endif // TARGET_WINDOWS

#if NET
        /// <summary>
        /// Linux-specific command line retrieval via /proc/{pid}/cmdline.
        /// </summary>
        [SupportedOSPlatform("linux")]
        private static class Linux
        {
            /// <summary>
            /// Reads /proc/{pid}/cmdline where arguments are null-byte separated,
            /// and joins them with spaces.
            /// </summary>
            internal static string? GetCommandLine(int processId)
            {
                try
                {
                    string cmdlinePath = $"/proc/{processId}/cmdline";
                    byte[] cmdlineBytes = File.ReadAllBytes(cmdlinePath);
                    if (cmdlineBytes.Length == 0)
                    {
                        return null;
                    }

                    return ParseNullSeparatedArguments(cmdlineBytes);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>
        /// macOS/BSD-specific P/Invoke bindings and command line retrieval via sysctl KERN_PROCARGS2.
        /// </summary>
        [SupportedOSPlatform("macos")]
        [SupportedOSPlatform("freebsd")]
        private static partial class BSD
        {
            [LibraryImport("libc", SetLastError = true)]
            private static partial int sysctl(
                ReadOnlySpan<int> name,
                uint namelen,
                Span<byte> oldp,
                ref nuint oldlenp,
                ReadOnlySpan<byte> newp,
                nuint newlen);

            /// <summary>
            /// Wrapper over the raw sysctl P/Invoke that is optimized for reading values, not writing.
            /// </summary>
            private static int Sysctl(ReadOnlySpan<int> name, Span<byte> oldp, ref nuint oldlenp)
                => sysctl(name, (uint)name.Length, oldp, ref oldlenp, ReadOnlySpan<byte>.Empty, 0);

            private const int CTL_KERN = 1;
            private const int KERN_PROCARGS2 = 49;

            /// <summary>
            /// Uses sysctl with KERN_PROCARGS2 to read the process arguments,
            /// then parses the null-separated buffer using span-based slicing with ArrayPool for efficient memory management.
            /// Related: https://github.com/dotnet/runtime/issues/101837
            /// </summary>
            internal static string? GetCommandLine(int processId)
            {
                ReadOnlySpan<int> mib = [CTL_KERN, KERN_PROCARGS2, processId];
                nuint size = 0;

                // Get the required buffer size
                if (Sysctl(mib, Span<byte>.Empty, ref size) != 0 || size == 0)
                {
                    return null;
                }

                // Rent a buffer from ArrayPool and pin it for sysctl
                byte[] buffer = ArrayPool<byte>.Shared.Rent((int)size);
                try
                {
                    if (Sysctl(mib, buffer.AsSpan(0, (int)size), ref size) != 0)
                    {
                        return null;
                    }

                    // Buffer format (KERN_PROCARGS2):
                    //   int argc (number of arguments including executable)
                    //   fully-qualified executable path (null-terminated)
                    //   padding null bytes
                    //   argv[0] .. argv[argc-1] (each null-terminated)
                    //   environment variables (not needed)
                    ReadOnlySpan<byte> data = buffer.AsSpan(0, (int)size);

                    if (data.Length < sizeof(int))
                    {
                        return null;
                    }

                    int argc = MemoryMarshal.Read<int>(data);
                    if (argc <= 0)
                    {
                        return null;
                    }

                    data = data.Slice(sizeof(int));

                    // Skip past the executable path (first null terminator)
                    int execPathEnd = data.IndexOf((byte)0);
                    if (execPathEnd < 0)
                    {
                        return null;
                    }

                    data = data.Slice(execPathEnd + 1);

                    // Skip padding null bytes between executable path and argv[0]
                    while (!data.IsEmpty && data[0] == 0)
                    {
                        data = data.Slice(1);
                    }

                    return ParseNullSeparatedArguments(data, argc);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
#endif
    }
}
