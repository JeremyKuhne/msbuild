// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
#if !TARGET_WINDOWS
using Microsoft.Build.Framework.Logging;
#endif
using Microsoft.Build.Shared;
#if TARGET_WINDOWS
using Microsoft.Win32;
#endif
using Microsoft.Win32.SafeHandles;
#if TARGET_WINDOWS
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Console;
using Windows.Win32.System.Diagnostics.Debug;
using Windows.Win32.System.SystemInformation;
using Windows.Win32.System.Threading;
using Wdk = Windows.Wdk;
using WdkThreading = Windows.Wdk.System.Threading;
#endif

#nullable disable

namespace Microsoft.Build.Framework;

internal static class NativeMethods
{
    #region Constants

    internal const uint ERROR_INSUFFICIENT_BUFFER = 0x8007007A;
    internal const uint S_OK = 0x0;

    internal const int FILE_ATTRIBUTE_READONLY = 0x00000001;
    internal const int FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const int FILE_ATTRIBUTE_REPARSE_POINT = 0x00000400;

    /// <summary>
    /// Default buffer size to use when dealing with the Windows API.
    /// </summary>
    internal const int MAX_PATH = 260;

    internal static DateTime MinFileDate { get; } = DateTime.FromFileTimeUtc(0);

#if TARGET_WINDOWS
    private const string WINDOWS_FILE_SYSTEM_REGISTRY_KEY = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string WINDOWS_LONG_PATHS_ENABLED_VALUE_NAME = "LongPathsEnabled";

    private const string WINDOWS_SAC_REGISTRY_KEY = @"SYSTEM\CurrentControlSet\Control\CI\Policy";
    private const string WINDOWS_SAC_VALUE_NAME = "VerifiedAndReputablePolicyState";
#endif

    #endregion

    #region Enums

    /// <summary>
    /// Processor architecture values
    /// </summary>
    internal enum ProcessorArchitectures
    {
        // Intel 32 bit
        X86,

        // AMD64 64 bit
        X64,

        // Itanium 64
        IA64,

        // ARM
        ARM,

        // ARM64
        ARM64,

        // WebAssembly
        WASM,

        // S390x
        S390X,

        // LongAarch64
        LOONGARCH64,

        // 32-bit ARMv6
        ARMV6,

        // PowerPC 64-bit (little-endian)
        PPC64LE,

        // Who knows
        Unknown
    }

    #endregion

    #region Structs

#if TARGET_WINDOWS
    /// <summary>
    /// Wrap the intptr returned by OpenProcess in a safe handle.
    /// </summary>
    internal class SafeProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeProcessHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        private SafeProcessHandle() : base(true)
        {
        }

        [SupportedOSPlatform("windows")]
        protected override bool ReleaseHandle()
        {
            return PInvoke.CloseHandle((HANDLE)handle);
        }
    }
#endif // TARGET_WINDOWS

    private class SystemInformationData
    {
        /// <summary>
        /// Architecture as far as the current process is concerned.
        /// It's x86 in wow64 (native architecture is x64 in that case).
        /// Otherwise it's the same as the native architecture.
        /// </summary>
        public readonly ProcessorArchitectures ProcessorArchitectureType;

        /// <summary>
        /// Actual architecture of the system.
        /// </summary>
        public readonly ProcessorArchitectures ProcessorArchitectureTypeNative;

#if TARGET_WINDOWS
        /// <summary>
        /// Convert SYSTEM_INFO architecture values to the internal enum
        /// </summary>
        /// <param name="arch"></param>
        /// <returns></returns>
        private static ProcessorArchitectures ConvertSystemArchitecture(PROCESSOR_ARCHITECTURE arch)
        {
            return arch switch
            {
                PROCESSOR_ARCHITECTURE.PROCESSOR_ARCHITECTURE_INTEL => ProcessorArchitectures.X86,
                PROCESSOR_ARCHITECTURE.PROCESSOR_ARCHITECTURE_AMD64 => ProcessorArchitectures.X64,
                PROCESSOR_ARCHITECTURE.PROCESSOR_ARCHITECTURE_ARM => ProcessorArchitectures.ARM,
                PROCESSOR_ARCHITECTURE.PROCESSOR_ARCHITECTURE_IA64 => ProcessorArchitectures.IA64,
                PROCESSOR_ARCHITECTURE.PROCESSOR_ARCHITECTURE_ARM64 => ProcessorArchitectures.ARM64,
                _ => ProcessorArchitectures.Unknown,
            };
        }
#endif

        /// <summary>
        /// Read system info values
        /// </summary>
        public SystemInformationData()
        {
            ProcessorArchitectureType = ProcessorArchitectures.Unknown;
            ProcessorArchitectureTypeNative = ProcessorArchitectures.Unknown;

#if TARGET_WINDOWS
            {
                SYSTEM_INFO systemInfo;

                PInvoke.GetSystemInfo(out systemInfo);
                ProcessorArchitectureType = ConvertSystemArchitecture(systemInfo.Anonymous.Anonymous.wProcessorArchitecture);

                PInvoke.GetNativeSystemInfo(out systemInfo);
                ProcessorArchitectureTypeNative = ConvertSystemArchitecture(systemInfo.Anonymous.Anonymous.wProcessorArchitecture);
            }
#else
            {
                ProcessorArchitectures processorArchitecture = ProcessorArchitectures.Unknown;

#if NET || NETSTANDARD1_1_OR_GREATER
                // Get the architecture from the runtime.
                processorArchitecture = RuntimeInformation.OSArchitecture switch
                {
                    Architecture.Arm => ProcessorArchitectures.ARM,
                    Architecture.Arm64 => ProcessorArchitectures.ARM64,
                    Architecture.X64 => ProcessorArchitectures.X64,
                    Architecture.X86 => ProcessorArchitectures.X86,
#if NET
                    Architecture.Wasm => ProcessorArchitectures.WASM,
                    Architecture.S390x => ProcessorArchitectures.S390X,
                    Architecture.LoongArch64 => ProcessorArchitectures.LOONGARCH64,
                    Architecture.Armv6 => ProcessorArchitectures.ARMV6,
                    Architecture.Ppc64le => ProcessorArchitectures.PPC64LE,
#endif
                    _ => ProcessorArchitectures.Unknown,
                };

#endif

                ProcessorArchitectureTypeNative = ProcessorArchitectureType = processorArchitecture;
            }
#endif
        }
    }

    public static int GetLogicalCoreCount()
    {
        int numberOfCpus = Environment.ProcessorCount;
        // .NET on Windows returns a core count limited to the current NUMA node
        //     https://github.com/dotnet/runtime/issues/29686
        // so always double-check it.
#if TARGET_WINDOWS
        var result = GetLogicalCoreCountOnWindows();
        if (result != -1)
        {
            numberOfCpus = result;
        }
#endif

        return numberOfCpus;
    }

#if TARGET_WINDOWS
    /// <summary>
    /// Get the exact physical core count on Windows
    /// Useful for getting the exact core count in 32 bits processes,
    /// as Environment.ProcessorCount has a 32-core limit in that case.
    /// https://github.com/dotnet/runtime/blob/221ad5b728f93489655df290c1ea52956ad8f51c/src/libraries/System.Runtime.Extensions/src/System/Environment.Windows.cs#L171-L210
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe int GetLogicalCoreCountOnWindows()
    {
        uint len = 0;
        const int ERROR_INSUFFICIENT_BUFFER = 122;

        if (!PInvoke.GetLogicalProcessorInformationEx(
                LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore, null, ref len) &&
            Marshal.GetLastWin32Error() == ERROR_INSUFFICIENT_BUFFER)
        {
            // Allocate that much space
            var buffer = new byte[len];
            fixed (byte* bufferPtr = buffer)
            {
                // Call GetLogicalProcessorInformationEx with the allocated buffer
                if (PInvoke.GetLogicalProcessorInformationEx(
                        LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore,
                        (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)bufferPtr, ref len))
                {
                    // Walk each SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX in the buffer, where the Size of each dictates how
                    // much space it's consuming.  For each group relation, count the number of active processors in each of its group infos.
                    int processorCount = 0;
                    byte* ptr = bufferPtr;
                    byte* endPtr = bufferPtr + len;
                    while (ptr < endPtr)
                    {
                        var current = (SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX*)ptr;
                        if (current->Relationship == LOGICAL_PROCESSOR_RELATIONSHIP.RelationProcessorCore)
                        {
                            // Flags is 0 if the core has a single logical proc, LTP_PC_SMT if more than one
                            // for now, assume "more than 1" == 2, as it has historically been for hyperthreading
                            processorCount += (current->Anonymous.Processor.Flags == 0) ? 1 : 2;
                        }
                        ptr += current->Size;
                    }
                    return processorCount;
                }
            }
        }

        return -1;
    }
#endif // TARGET_WINDOWS

    #endregion

    #region Member data

    internal static bool HasMaxPath => MaxPath == MAX_PATH;

    /// <summary>
    /// Gets the max path limit of the current OS.
    /// </summary>
    internal static int MaxPath
    {
        get
        {
            if (!IsMaxPathSet)
            {
                SetMaxPath();
            }
            return _maxPath;
        }
    }

    /// <summary>
    /// Cached value for MaxPath.
    /// </summary>
    private static int _maxPath;

    private static bool IsMaxPathSet { get; set; }

    private static readonly LockType MaxPathLock = new LockType();

    private static void SetMaxPath()
    {
        lock (MaxPathLock)
        {
            if (!IsMaxPathSet)
            {
                bool isMaxPathRestricted = Traits.Instance.EscapeHatches.DisableLongPaths || IsMaxPathLegacyWindows();
                _maxPath = isMaxPathRestricted ? MAX_PATH : int.MaxValue;
                IsMaxPathSet = true;
            }
        }
    }

    internal enum LongPathsStatus
    {
        /// <summary>
        ///  The registry key is set to 0 or does not exist.
        /// </summary>
        Disabled,

        /// <summary>
        /// The registry key does not exist.
        /// </summary>
        Missing,

        /// <summary>
        /// The registry key is set to 1.
        /// </summary>
        Enabled,

        /// <summary>
        /// Not on Windows.
        /// </summary>
        NotApplicable,
    }

    internal static LongPathsStatus IsLongPathsEnabled()
    {
#if TARGET_WINDOWS
        try
        {
            return IsLongPathsEnabledRegistry();
        }
        catch
        {
            return LongPathsStatus.Disabled;
        }
#else
        return LongPathsStatus.NotApplicable;
#endif
    }

    internal static bool IsMaxPathLegacyWindows()
    {
        var longPathsStatus = IsLongPathsEnabled();
        return longPathsStatus == LongPathsStatus.Disabled || longPathsStatus == LongPathsStatus.Missing;
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    private static LongPathsStatus IsLongPathsEnabledRegistry()
    {
        using (RegistryKey fileSystemKey = Registry.LocalMachine.OpenSubKey(WINDOWS_FILE_SYSTEM_REGISTRY_KEY))
        {
            object longPathsEnabledValue = fileSystemKey?.GetValue(WINDOWS_LONG_PATHS_ENABLED_VALUE_NAME, -1);
            if (fileSystemKey != null && Convert.ToInt32(longPathsEnabledValue) == -1)
            {
                return LongPathsStatus.Missing;
            }
            else if (fileSystemKey != null && Convert.ToInt32(longPathsEnabledValue) == 1)
            {
                return LongPathsStatus.Enabled;
            }
            else
            {
                return LongPathsStatus.Disabled;
            }
        }
    }
#endif

    private static SAC_State? s_sacState;

    /// <summary>
    /// Get from registry state of the Smart App Control (SAC) on the system.
    /// </summary>
    /// <returns>State of SAC</returns>
    internal static SAC_State GetSACState()
    {
        s_sacState ??= GetSACStateInternal();

        return s_sacState.Value;
    }

    internal static SAC_State GetSACStateInternal()
    {
#if TARGET_WINDOWS
        try
        {
            return GetSACStateRegistry();
        }
        catch
        {
            return SAC_State.Missing;
        }
#else
        return SAC_State.NotApplicable;
#endif
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    private static SAC_State GetSACStateRegistry()
    {
        SAC_State SACState = SAC_State.Missing;

        using (RegistryKey policyKey = Registry.LocalMachine.OpenSubKey(WINDOWS_SAC_REGISTRY_KEY))
        {
            if (policyKey != null)
            {
                object sacValue = policyKey.GetValue(WINDOWS_SAC_VALUE_NAME, -1);
                SACState = Convert.ToInt32(sacValue) switch
                {
                    0 => SAC_State.Off,
                    1 => SAC_State.Enforcement,
                    2 => SAC_State.Evaluation,
                    _ => SAC_State.Missing,
                };
            }
        }

        return SACState;
    }
#endif

    /// <summary>
    /// State of Smart App Control (SAC) on the system.
    /// </summary>
    internal enum SAC_State
    {
        /// <summary>
        /// 1: SAC is on and enforcing.
        /// </summary>
        Enforcement,
        /// <summary>
        /// 2: SAC is on and in evaluation mode.
        /// </summary>
        Evaluation,
        /// <summary>
        /// 0: SAC is off.
        /// </summary>
        Off,
        /// <summary>
        /// The registry key is missing.
        /// </summary>
        Missing,
        /// <summary>
        /// Not on Windows.
        /// </summary>
        NotApplicable
    }

    /// <summary>
    /// Cached value for IsUnixLike (this method is called frequently during evaluation).
    /// </summary>
    private static readonly bool s_isUnixLike = IsLinux || IsOSX || IsBSD;

    /// <summary>
    /// Gets a flag indicating if we are running under a Unix-like system (Mac, Linux, etc.)
    /// </summary>
    internal static bool IsUnixLike
    {
        get { return s_isUnixLike; }
    }

    /// <summary>
    /// Gets a flag indicating if we are running under Linux
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    internal static bool IsLinux
    {
        get { return RuntimeInformation.IsOSPlatform(OSPlatform.Linux); }
    }

    /// <summary>
    /// Gets a flag indicating if we are running under flavor of BSD (NetBSD, OpenBSD, FreeBSD)
    /// </summary>
    [SupportedOSPlatformGuard("freebsd")]
    internal static bool IsBSD
    {
        get
        {
            return RuntimeInformation.IsOSPlatform(OSPlatform.Create("FREEBSD")) ||
                   RuntimeInformation.IsOSPlatform(OSPlatform.Create("NETBSD")) ||
                   RuntimeInformation.IsOSPlatform(OSPlatform.Create("OPENBSD"));
        }
    }

    private static bool? _isWindows;

    /// <summary>
    /// Gets a flag indicating if we are running under some version of Windows
    /// </summary>
    [SupportedOSPlatformGuard("windows")]
    internal static bool IsWindows
    {
        get
        {
            _isWindows ??= RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
            return _isWindows.Value;
        }
    }

    private static bool? _isOSX;

    /// <summary>
    /// Gets a flag indicating if we are running under Mac OSX
    /// </summary>
    [SupportedOSPlatformGuard("macos")]
    internal static bool IsOSX
    {
        get
        {
            _isOSX ??= RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            return _isOSX.Value;
        }
    }

    /// <summary>
    /// Gets a string for the current OS. This matches the OS env variable
    /// for Windows (Windows_NT).
    /// </summary>
    internal static string OSName
    {
        get { return IsWindows ? "Windows_NT" : "Unix"; }
    }

    /// <summary>
    /// Framework named as presented to users (for example in version info).
    /// </summary>
    internal static string FrameworkName
    {
        get
        {
#if RUNTIME_TYPE_NETCORE
            const string frameworkName = ".NET";
#else
            const string frameworkName = ".NET Framework";
#endif
            return frameworkName;
        }
    }

    /// <summary>
    /// OS name that can be used for the msbuildExtensionsPathSearchPaths element
    /// for a toolset
    /// </summary>
    internal static string GetOSNameForExtensionsPath()
    {
        return IsOSX ? "osx" : IsUnixLike ? "unix" : "windows";
    }

    internal static bool OSUsesCaseSensitivePaths
    {
        get { return IsLinux; }
    }

    /// <summary>
    /// Determines whether the file system is case sensitive by creating a test file.
    /// Copied from FileUtilities.GetIsFileSystemCaseSensitive() in Shared.
    /// FIXME: shared code should be consolidated to Framework https://github.com/dotnet/msbuild/issues/6984
    /// </summary>
    private static readonly Lazy<bool> s_isFileSystemCaseSensitive = new(() =>
    {
        try
        {
            string pathWithUpperCase = Path.Combine(Path.GetTempPath(), $"INTCASESENSITIVETEST{Guid.NewGuid():N}");
            using (new FileStream(pathWithUpperCase, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 0x1000, FileOptions.DeleteOnClose))
            {
                return !File.Exists(pathWithUpperCase.ToLowerInvariant());
            }
        }
        catch
        {
            return OSUsesCaseSensitivePaths;
        }
    });

    internal static bool IsFileSystemCaseSensitive => s_isFileSystemCaseSensitive.Value;

    /// <summary>
    /// The base directory for all framework paths in Mono
    /// </summary>
    private static string s_frameworkBasePath;

    /// <summary>
    /// The directory of the current framework
    /// </summary>
    private static string s_frameworkCurrentPath;

    /// <summary>
    /// Gets the currently running framework path
    /// </summary>
    internal static string FrameworkCurrentPath
    {
        get
        {
            if (s_frameworkCurrentPath == null)
            {
                var baseTypeLocation = AssemblyUtilities.GetAssemblyLocation(typeof(string).GetTypeInfo().Assembly);

                s_frameworkCurrentPath =
                    Path.GetDirectoryName(baseTypeLocation)
                    ?? string.Empty;
            }

            return s_frameworkCurrentPath;
        }
    }

    /// <summary>
    /// Gets the base directory of all Mono frameworks
    /// </summary>
    internal static string FrameworkBasePath
    {
        get
        {
            if (s_frameworkBasePath == null)
            {
                var dir = FrameworkCurrentPath;
                if (dir != string.Empty)
                {
                    dir = Path.GetDirectoryName(dir);
                }

                s_frameworkBasePath = dir ?? string.Empty;
            }

            return s_frameworkBasePath;
        }
    }

    /// <summary>
    /// System information, initialized when required.
    /// </summary>
    /// <remarks>
    /// Initially implemented as <see cref="Lazy{SystemInformationData}"/>, but
    /// that's .NET 4+, and this is used in MSBuildTaskHost.
    /// </remarks>
    private static SystemInformationData SystemInformation
    {
        get
        {
            if (!_systemInformationInitialized)
            {
                lock (SystemInformationLock)
                {
                    if (!_systemInformationInitialized)
                    {
                        _systemInformation = new SystemInformationData();
                        _systemInformationInitialized = true;
                    }
                }
            }
            return _systemInformation;
        }
    }

    private static SystemInformationData _systemInformation;
    private static bool _systemInformationInitialized;
    private static readonly LockType SystemInformationLock = new LockType();

    /// <summary>
    /// Architecture getter
    /// </summary>
    internal static ProcessorArchitectures ProcessorArchitecture => SystemInformation.ProcessorArchitectureType;

    /// <summary>
    /// Native architecture getter
    /// </summary>
    internal static ProcessorArchitectures ProcessorArchitectureNative => SystemInformation.ProcessorArchitectureTypeNative;

    #endregion

    #region Wrapper methods

    /// <summary>
    /// Get the last write time of the fullpath to a directory. If the pointed path is not a directory, or
    /// if the directory does not exist, then false is returned and fileModifiedTimeUtc is set DateTime.MinValue.
    /// </summary>
    /// <param name="fullPath">Full path to the file in the filesystem</param>
    /// <param name="fileModifiedTimeUtc">The UTC last write time for the directory</param>
    internal static bool GetLastWriteDirectoryUtcTime(string fullPath, out DateTime fileModifiedTimeUtc)
    {
        // This code was copied from the reference manager, if there is a bug fix in that code, see if the same fix should also be made
        // there
#if TARGET_WINDOWS
        fileModifiedTimeUtc = DateTime.MinValue;

        WIN32_FILE_ATTRIBUTE_DATA data = default;
        bool success = GetFileAttributesEx(fullPath, 0, ref data);
        if (success)
        {
            if (((int)data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                long dt = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | ((long)data.ftLastWriteTime.dwLowDateTime);
                fileModifiedTimeUtc = DateTime.FromFileTimeUtc(dt);
            }
            else
            {
                // Path does not point to a directory
                success = false;
            }
        }

        return success;
#else
        if (Directory.Exists(fullPath))
        {
            fileModifiedTimeUtc = Directory.GetLastWriteTimeUtc(fullPath);
            return true;
        }
        else
        {
            fileModifiedTimeUtc = DateTime.MinValue;
            return false;
        }
#endif
    }

#if TARGET_WINDOWS
    /// <summary>
    /// Takes the path and returns the short path
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string GetShortFilePath(string path)
    {
        if (path != null)
        {
            int length = GetShortPathName(path, null, 0);
            int errorCode = Marshal.GetLastWin32Error();

            if (length > 0)
            {
                char[] fullPathBuffer = new char[length];
                length = GetShortPathName(path, fullPathBuffer, length);
                errorCode = Marshal.GetLastWin32Error();

                if (length > 0)
                {
                    string fullPath = new(fullPathBuffer, 0, length);
                    path = fullPath;
                }
            }

            if (length == 0 && errorCode != 0)
            {
                ThrowExceptionForErrorCode(errorCode);
            }
        }

        return path;
    }

    /// <summary>
    /// Takes the path and returns a full path
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static string GetLongFilePath(string path)
    {
        if (path != null)
        {
            int length = GetLongPathName(path, null, 0);
            int errorCode = Marshal.GetLastWin32Error();

            if (length > 0)
            {
                char[] fullPathBuffer = new char[length];
                length = GetLongPathName(path, fullPathBuffer, length);
                errorCode = Marshal.GetLastWin32Error();

                if (length > 0)
                {
                    string fullPath = new(fullPathBuffer, 0, length);
                    path = fullPath;
                }
            }

            if (length == 0 && errorCode != 0)
            {
                ThrowExceptionForErrorCode(errorCode);
            }
        }

        return path;
    }
#endif // TARGET_WINDOWS

    /// <summary>
    /// Retrieves the current global memory status.
    /// </summary>
#if TARGET_WINDOWS
    internal static MEMORYSTATUSEX? GetMemoryStatus()
    {
        MEMORYSTATUSEX status = default;
        status.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
        return !PInvoke.GlobalMemoryStatusEx(ref status) ? null : status;
    }
#endif

    internal static bool MakeSymbolicLink(string newFileName, string existingFileName, ref string errorMessage)
    {
#if TARGET_WINDOWS
        Version osVersion = Environment.OSVersion.Version;
        SYMBOLIC_LINK_FLAGS flags = 0; // File = 0 (no named constant)
        if (osVersion.Major >= 11 || (osVersion.Major == 10 && osVersion.Build >= 14972))
        {
            flags |= SYMBOLIC_LINK_FLAGS.SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE;
        }

        bool symbolicLinkCreated = PInvoke.CreateSymbolicLink(newFileName, existingFileName, flags);
        errorMessage = symbolicLinkCreated ? null : Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()).Message;
#else
        bool symbolicLinkCreated = symlink(existingFileName, newFileName) == 0;
        errorMessage = symbolicLinkCreated ? null : Marshal.GetLastWin32Error().ToString();
#endif
        return symbolicLinkCreated;
    }

    /// <summary>
    /// Get the last write time of the fullpath to the file.
    /// </summary>
    /// <param name="fullPath">Full path to the file in the filesystem</param>
    /// <returns>The last write time of the file, or DateTime.MinValue if the file does not exist.</returns>
    /// <remarks>
    /// This method should be accurate for regular files and symlinks, but can report incorrect data
    /// if the file's content was modified by writing to it through a different link, unless
    /// MSBUILDALWAYSCHECKCONTENTTIMESTAMP=1.
    /// </remarks>
    internal static DateTime GetLastWriteFileUtcTime(string fullPath)
    {
        if (Traits.Instance.EscapeHatches.AlwaysDoImmutableFilesUpToDateCheck)
        {
            return LastWriteFileUtcTime(fullPath);
        }

        bool isNonModifiable = FileClassifier.Shared.IsNonModifiable(fullPath);
        if (isNonModifiable)
        {
            if (ImmutableFilesTimestampCache.Shared.TryGetValue(fullPath, out DateTime modifiedAt))
            {
                return modifiedAt;
            }
        }

        DateTime modifiedTime = LastWriteFileUtcTime(fullPath);

        if (isNonModifiable && modifiedTime != DateTime.MinValue)
        {
            ImmutableFilesTimestampCache.Shared.TryAdd(fullPath, modifiedTime);
        }

        return modifiedTime;

        DateTime LastWriteFileUtcTime(string path)
        {
            DateTime fileModifiedTime = DateTime.MinValue;

#if TARGET_WINDOWS
            {
                if (Traits.Instance.EscapeHatches.AlwaysUseContentTimestamp)
                {
                    return GetContentLastWriteFileUtcTime(path);
                }

                var data = default(WIN32_FILE_ATTRIBUTE_DATA);
                bool success = GetFileAttributesEx(path, 0, ref data);

                if (success && ((int)data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0)
                {
                    long dt = ((long)data.ftLastWriteTime.dwHighDateTime << 32) | ((long)data.ftLastWriteTime.dwLowDateTime);
                    fileModifiedTime = DateTime.FromFileTimeUtc(dt);

                    // If file is a symlink _and_ we're not instructed to do the wrong thing, get a more accurate timestamp.
                    if (((int)data.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) == FILE_ATTRIBUTE_REPARSE_POINT && !Traits.Instance.EscapeHatches.UseSymlinkTimeInsteadOfTargetTime)
                    {
                        fileModifiedTime = GetContentLastWriteFileUtcTime(path);
                    }
                }

                return fileModifiedTime;
            }
#else
            {
                return File.Exists(path)
                    ? File.GetLastWriteTimeUtc(path)
                    : DateTime.MinValue;
            }
#endif
        }
    }

#if TARGET_WINDOWS
    /// <summary>
    /// Get the SafeFileHandle for a file, while skipping reparse points (going directly to target file).
    /// </summary>
    /// <param name="fullPath">Full path to the file in the filesystem</param>
    /// <returns>the SafeFileHandle for a file (target file in case of symlinks)</returns>
    [SupportedOSPlatform("windows")]
    private static unsafe SafeFileHandle OpenFileThroughSymlinks(string fullPath)
    {
        HANDLE h = PInvoke.CreateFile(
            fullPath,
            (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ,
            FILE_SHARE_MODE.FILE_SHARE_READ,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL, /* No FILE_FLAG_OPEN_REPARSE_POINT; read through to content */
            HANDLE.Null);
        return new SafeFileHandle((IntPtr)h.Value, ownsHandle: true);
    }

    /// <summary>
    /// Get the last write time of the content pointed to by a file path.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static unsafe DateTime GetContentLastWriteFileUtcTime(string fullPath)
    {
        DateTime fileModifiedTime = DateTime.MinValue;

        using (SafeFileHandle handle = OpenFileThroughSymlinks(fullPath))
        {
            if (!handle.IsInvalid)
            {
                System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
                if (PInvoke.GetFileTime((HANDLE)handle.DangerousGetHandle(), null, null, &ftLastWriteTime))
                {
                    long fileTime = ((long)(uint)ftLastWriteTime.dwHighDateTime) << 32 |
                                    (long)(uint)ftLastWriteTime.dwLowDateTime;
                    fileModifiedTime =
                        DateTime.FromFileTimeUtc(fileTime);
                }
            }
        }

        return fileModifiedTime;
    }
#endif // TARGET_WINDOWS

    /// <summary>
    /// Given an error code, converts it to an HRESULT and throws the appropriate exception.
    /// </summary>
    /// <param name="errorCode"></param>
    public static void ThrowExceptionForErrorCode(int errorCode)
    {
        // See ndp\clr\src\bcl\system\io\__error.cs for this code as it appears in the CLR.

        // Something really bad went wrong with the call
        // translate the error into an exception

        // Convert the errorcode into an HRESULT (See MakeHRFromErrorCode in Win32Native.cs in
        // ndp\clr\src\bcl\microsoft\win32)
        errorCode = unchecked(((int)0x80070000) | errorCode);

        // Throw an exception as best we can
        Marshal.ThrowExceptionForHR(errorCode);
    }

#if TARGET_WINDOWS
    /// <summary>
    /// Kills the specified process by id and all of its children recursively.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static void KillTree(int processIdToKill)
    {
        // Note that GetProcessById does *NOT* internally hold on to the process handle.
        // Only when you create the process using the Process object
        // does the Process object retain the original handle.

        Process thisProcess;
        try
        {
            thisProcess = Process.GetProcessById(processIdToKill);
        }
        catch (ArgumentException)
        {
            // The process has already died for some reason.  So shrug and assume that any child processes
            // have all also either died or are in the process of doing so.
            return;
        }

        try
        {
            DateTime myStartTime = thisProcess.StartTime;

            // Grab the process handle.  We want to keep this open for the duration of the function so that
            // it cannot be reused while we are running.
            using (SafeProcessHandle hProcess = OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION, false, processIdToKill))
            {
                if (hProcess.IsInvalid)
                {
                    return;
                }

                try
                {
                    // Kill this process, so that no further children can be created.
                    thisProcess.Kill();
                }
                catch (Win32Exception e) when (e.NativeErrorCode == (int)WIN32_ERROR.ERROR_ACCESS_DENIED)
                {
                    // Access denied is potentially expected -- it happens when the process that
                    // we're attempting to kill is already dead.  So just ignore in that case.
                }

                // Now enumerate our children.  Children of this process are any process which has this process id as its parent
                // and which also started after this process did.
                List<KeyValuePair<int, SafeProcessHandle>> children = GetChildProcessIds(processIdToKill, myStartTime);

                try
                {
                    foreach (KeyValuePair<int, SafeProcessHandle> childProcessInfo in children)
                    {
                        KillTree(childProcessInfo.Key);
                    }
                }
                finally
                {
                    foreach (KeyValuePair<int, SafeProcessHandle> childProcessInfo in children)
                    {
                        childProcessInfo.Value.Dispose();
                    }
                }
            }
        }
        finally
        {
            thisProcess.Dispose();
        }
    }

    /// <summary>
    /// Returns the parent process id for the specified process.
    /// Returns zero if it cannot be gotten for some reason.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static int GetParentProcessId(int processId)
    {
        int ParentID = 0;

#if !TARGET_WINDOWS
        string line = null;

        try
        {
            // /proc/<processID>/stat returns a bunch of space separated fields. Get that string

            // TODO: this was
            // using (var r = FileUtilities.OpenRead("/proc/" + processId + "/stat"))
            // and could be again when FileUtilities moves to Framework

            using var fileStream = new FileStream($"/proc/{processId}/stat", FileMode.Open, FileAccess.Read);
            using StreamReader r = new(fileStream);

            line = r.ReadLine();
        }
        catch // Ignore errors since the process may have terminated
        {
        }

        if (!string.IsNullOrWhiteSpace(line))
        {
            // One of the fields is the process name. It may contain any characters, but since it's
            // in parenthesis, we can finds its end by looking for the last parenthesis. After that,
            // there comes a space, then the second fields separated by a space is the parent id.
            string[] statFields = line.Substring(line.LastIndexOf(')')).Split(MSBuildConstants.SpaceChar, 4);
            if (statFields.Length >= 3)
            {
                ParentID = Int32.Parse(statFields[2]);
            }
        }
#else
        using SafeProcessHandle hProcess = OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION, false, processId);
        {
            if (!hProcess.IsInvalid)
            {
                // UNDONE: NtQueryInformationProcess will fail if we are not elevated and other process is. Advice is to change to use ToolHelp32 API's
                // For now just return zero and worst case we will not kill some children.
                var pbi = default(PROCESS_BASIC_INFORMATION);
                int pSize = 0;

                if (0 == NtQueryInformationProcess(hProcess, ref pbi, ref pSize))
                {
                    ParentID = (int)pbi.InheritedFromUniqueProcessId;
                }
            }
        }
#endif

        return ParentID;
    }

    /// <summary>
    /// Returns an array of all the immediate child processes by id.
    /// NOTE: The IntPtr in the tuple is the handle of the child process.  CloseHandle MUST be called on this.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static List<KeyValuePair<int, SafeProcessHandle>> GetChildProcessIds(int parentProcessId, DateTime parentStartTime)
    {
        List<KeyValuePair<int, SafeProcessHandle>> myChildren = new List<KeyValuePair<int, SafeProcessHandle>>();

        foreach (Process possibleChildProcess in Process.GetProcesses())
        {
            using (possibleChildProcess)
            {
                // Hold the child process handle open so that children cannot die and restart with a different parent after we've started looking at it.
                // This way, any handle we pass back is guaranteed to be one of our actual children.
#pragma warning disable CA2000 // Dispose objects before losing scope - caller must dispose returned handles
                SafeProcessHandle childHandle = OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_INFORMATION, false, possibleChildProcess.Id);
#pragma warning restore CA2000 // Dispose objects before losing scope
                {
                    if (childHandle.IsInvalid)
                    {
                        continue;
                    }

                    bool keepHandle = false;
                    try
                    {
                        if (possibleChildProcess.StartTime > parentStartTime)
                        {
                            int childParentProcessId = GetParentProcessId(possibleChildProcess.Id);
                            if (childParentProcessId != 0)
                            {
                                if (parentProcessId == childParentProcessId)
                                {
                                    // Add this one
                                    myChildren.Add(new KeyValuePair<int, SafeProcessHandle>(possibleChildProcess.Id, childHandle));
                                    keepHandle = true;
                                }
                            }
                        }
                    }
                    finally
                    {
                        if (!keepHandle)
                        {
                            childHandle.Dispose();
                        }
                    }
                }
            }
        }

        return myChildren;
    }
#endif // TARGET_WINDOWS

    /// <summary>
    /// Internal, optimized GetCurrentDirectory implementation that simply delegates to the native method
    /// </summary>
    /// <returns></returns>
    internal static unsafe string GetCurrentDirectory()
    {
#if FEATURE_LEGACY_GETCURRENTDIRECTORY
        int bufferSize = (int)PInvoke.GetCurrentDirectory(0, null);
        VerifyThrowWin32Result(bufferSize);
        char* buffer = stackalloc char[bufferSize];
        int pathLength = (int)PInvoke.GetCurrentDirectory((uint)bufferSize, buffer);
        VerifyThrowWin32Result(pathLength);
        return new string(buffer, startIndex: 0, length: pathLength);
#else
        return Directory.GetCurrentDirectory();
#endif
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    internal static unsafe string GetFullPath(string path)
    {
        char* buffer = stackalloc char[MAX_PATH];
        int fullPathLength = GetFullPathWin32(path, MAX_PATH, buffer, IntPtr.Zero);

        // if user is using long paths we could need to allocate a larger buffer
        if (fullPathLength > MAX_PATH)
        {
            char* newBuffer = stackalloc char[fullPathLength];
            fullPathLength = GetFullPathWin32(path, fullPathLength, newBuffer, IntPtr.Zero);

            buffer = newBuffer;
        }

        // Avoid creating new strings unnecessarily
        return AreStringsEqual(buffer, fullPathLength, path) ? path : new string(buffer, startIndex: 0, length: fullPathLength);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe int GetFullPathWin32(string target, int bufferLength, char* buffer, IntPtr mustBeZero)
    {
        int pathLength = (int)PInvoke.GetFullPathName(target, new Span<char>(buffer, bufferLength), null);
        VerifyThrowWin32Result(pathLength);
        return pathLength;
    }

    /// <summary>
    /// Compare an unsafe char buffer with a <see cref="System.String"/> to see if their contents are identical.
    /// </summary>
    /// <param name="buffer">The beginning of the char buffer.</param>
    /// <param name="len">The length of the buffer.</param>
    /// <param name="s">The string.</param>
    /// <returns>True only if the contents of <paramref name="s"/> and the first <paramref name="len"/> characters in <paramref name="buffer"/> are identical.</returns>
    private static unsafe bool AreStringsEqual(char* buffer, int len, string s)
    {
        return s.AsSpan().SequenceEqual(new ReadOnlySpan<char>(buffer, len));
    }
#endif // TARGET_WINDOWS

    internal static void VerifyThrowWin32Result(int result)
    {
        bool isError = result == 0;
        if (isError)
        {
            int code = Marshal.GetLastWin32Error();
            ThrowExceptionForErrorCode(code);
        }
    }

    internal static (bool acceptAnsiColorCodes, bool outputIsScreen, uint? originalConsoleMode) QueryIsScreenAndTryEnableAnsiColorCodes(bool useStandardError = false)
    {
        if (Console.IsOutputRedirected)
        {
            // There's no ANSI terminal support if console output is redirected.
            return (acceptAnsiColorCodes: false, outputIsScreen: false, originalConsoleMode: null);
        }

        if (Console.BufferHeight == 0 || Console.BufferWidth == 0)
        {
            // The current console doesn't have a valid buffer size, which means it is not a real console. let's default to not using TL
            // in those scenarios.
            return (acceptAnsiColorCodes: false, outputIsScreen: false, originalConsoleMode: null);
        }

        bool acceptAnsiColorCodes = false;
        bool outputIsScreen = false;
        uint? originalConsoleMode = null;
#if TARGET_WINDOWS
        try
        {
            HANDLE outputStream = PInvoke.GetStdHandle(useStandardError ? STD_HANDLE.STD_ERROR_HANDLE : STD_HANDLE.STD_OUTPUT_HANDLE);
            if (PInvoke.GetConsoleMode(outputStream, out CONSOLE_MODE consoleMode))
            {
                if (consoleMode.HasFlag(CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING))
                {
                    // Console is already in required state.
                    acceptAnsiColorCodes = true;
                }
                else
                {
                    originalConsoleMode = (uint)consoleMode;
                    consoleMode |= CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING;
                    if (PInvoke.SetConsoleMode(outputStream, consoleMode) && PInvoke.GetConsoleMode(outputStream, out consoleMode))
                    {
                        // We only know if vt100 is supported if the previous call actually set the new flag, older
                        // systems ignore the setting.
                        acceptAnsiColorCodes = consoleMode.HasFlag(CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING);
                    }
                }

                // The std out is a char type (LPT or Console).
                outputIsScreen = PInvoke.GetFileType(outputStream) == FILE_TYPE.FILE_TYPE_CHAR;
                acceptAnsiColorCodes &= outputIsScreen;
            }
        }
        catch
        {
            // In the unlikely case that the above fails we just ignore and continue.
        }
#else
        // On posix OSes detect whether the terminal supports VT100 from the value of the TERM environment variable.
        acceptAnsiColorCodes = AnsiDetector.IsAnsiSupported(Environment.GetEnvironmentVariable("TERM"));
        // It wasn't redirected as tested above so we assume output is screen/console
        outputIsScreen = true;
#endif
        return (acceptAnsiColorCodes, outputIsScreen, originalConsoleMode);
    }

    internal static void RestoreConsoleMode(uint? originalConsoleMode, bool useStandardError = false)
    {
#if TARGET_WINDOWS
        if (originalConsoleMode is not null)
        {
            HANDLE stdOut = PInvoke.GetStdHandle(useStandardError ? STD_HANDLE.STD_ERROR_HANDLE : STD_HANDLE.STD_OUTPUT_HANDLE);
            _ = PInvoke.SetConsoleMode(stdOut, (CONSOLE_MODE)originalConsoleMode.Value);
        }
#endif
    }

    internal static bool SetCurrentDirectory(string path)
    {
#if TARGET_WINDOWS
        return PInvoke.SetCurrentDirectory(path);
#else
        // Make sure this does not throw
        try
        {
            Directory.SetCurrentDirectory(path);
        }
        catch
        {
        }
        return true;
#endif
    }

    #endregion

    #region PInvoke

    // ---- Unix (libc) ----
#if !TARGET_WINDOWS
    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    internal static extern int chmod(string pathname, int mode);

    [SupportedOSPlatform("linux")]
    [DllImport("libc", SetLastError = true)]
    internal static extern int mkdir(string path, int mode);

    [DllImport("libc", SetLastError = true)]
    internal static extern int symlink(string oldpath, string newpath);
#endif

    // ---- Windows (CsWin32 wrappers preserving existing internal signatures) ----
#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    internal static unsafe bool GetFileAttributesEx(string name, int fileInfoLevel, ref WIN32_FILE_ATTRIBUTE_DATA lpFileInformation)
    {
        fixed (WIN32_FILE_ATTRIBUTE_DATA* ptr = &lpFileInformation)
        {
            return PInvoke.GetFileAttributesEx(name, (GET_FILEEX_INFO_LEVELS)fileInfoLevel, ptr);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static unsafe int GetCurrentDirectory(int nBufferLength, char* lpBuffer)
    {
        return (int)PInvoke.GetCurrentDirectory((uint)nBufferLength, lpBuffer);
    }

    [SupportedOSPlatform("windows")]
    internal static unsafe int GetFullPathName(string target, int bufferLength, char* buffer, IntPtr mustBeZero)
    {
        return (int)PInvoke.GetFullPathName(target, new Span<char>(buffer, bufferLength), null);
    }

    [SupportedOSPlatform("windows")]
    private static SafeProcessHandle OpenProcess(PROCESS_ACCESS_RIGHTS dwDesiredAccess, bool bInheritHandle, int dwProcessId)
    {
        HANDLE h = PInvoke.OpenProcess(dwDesiredAccess, bInheritHandle, (uint)dwProcessId);
        return new SafeProcessHandle(h);
    }

    [SupportedOSPlatform("windows")]
    private static unsafe int NtQueryInformationProcess(
        SafeProcessHandle hProcess,
        ref PROCESS_BASIC_INFORMATION pbi,
        ref int pSize)
    {
        fixed (PROCESS_BASIC_INFORMATION* pbiPtr = &pbi)
        {
            uint returnLength = 0;
            NTSTATUS status = Wdk.PInvoke.NtQueryInformationProcess(
                (HANDLE)hProcess.DangerousGetHandle(),
                WdkThreading.PROCESSINFOCLASS.ProcessBasicInformation,
                pbiPtr,
                (uint)sizeof(PROCESS_BASIC_INFORMATION),
                ref returnLength);
            pSize = (int)returnLength;
            return status.Value;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static int GetShortPathName(string path, char[] fullpath, int length)
    {
        Span<char> span = fullpath is null ? default : fullpath.AsSpan(0, length);
        return (int)PInvoke.GetShortPathName(path, span);
    }

    [SupportedOSPlatform("windows")]
    internal static int GetLongPathName(string path, char[] fullpath, int length)
    {
        Span<char> span = fullpath is null ? default : fullpath.AsSpan(0, length);
        return (int)PInvoke.GetLongPathName(path, span);
    }

    [SupportedOSPlatform("windows")]
    internal static unsafe SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile)
    {
        HANDLE h = PInvoke.CreateFile(
            lpFileName,
            dwDesiredAccess,
            (FILE_SHARE_MODE)dwShareMode,
            (SECURITY_ATTRIBUTES?)null,
            (FILE_CREATION_DISPOSITION)dwCreationDisposition,
            (FILE_FLAGS_AND_ATTRIBUTES)dwFlagsAndAttributes,
            (HANDLE)hTemplateFile);
        return new SafeFileHandle((IntPtr)h.Value, ownsHandle: true);
    }

    [SupportedOSPlatform("windows")]
    internal static unsafe bool SetThreadErrorMode(int newMode, out int oldMode)
    {
        THREAD_ERROR_MODE oldModeU;
        bool result = PInvoke.SetThreadErrorMode((THREAD_ERROR_MODE)newMode, &oldModeU);
        oldMode = (int)oldModeU;
        return result;
    }
#endif // TARGET_WINDOWS

    #endregion

    #region helper methods

    internal static bool DirectoryExists(string fullPath)
    {
#if TARGET_WINDOWS
        return DirectoryExistsWindows(fullPath);
#else
        return Directory.Exists(fullPath);
#endif
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    internal static bool DirectoryExistsWindows(string fullPath)
    {
        var data = default(WIN32_FILE_ATTRIBUTE_DATA);
        bool success = GetFileAttributesEx(fullPath, 0, ref data);
        return success && ((int)data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
    }
#endif

    internal static bool FileExists(string fullPath)
    {
#if TARGET_WINDOWS
        return FileExistsWindows(fullPath);
#else
        return File.Exists(fullPath);
#endif
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    internal static bool FileExistsWindows(string fullPath)
    {
        var data = default(WIN32_FILE_ATTRIBUTE_DATA);
        bool success = GetFileAttributesEx(fullPath, 0, ref data);
        return success && ((int)data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) == 0;
    }
#endif

    internal static bool FileOrDirectoryExists(string path)
    {
#if TARGET_WINDOWS
        return FileOrDirectoryExistsWindows(path);
#else
        // Path will call Path.GetFullPath on the input path, which has some cost.
        return Path.Exists(path);
#endif
    }

#if TARGET_WINDOWS
    [SupportedOSPlatform("windows")]
    internal static bool FileOrDirectoryExistsWindows(string path)
    {
        var data = default(WIN32_FILE_ATTRIBUTE_DATA);
        return GetFileAttributesEx(path, 0, ref data);
    }
#endif

    #endregion
}
