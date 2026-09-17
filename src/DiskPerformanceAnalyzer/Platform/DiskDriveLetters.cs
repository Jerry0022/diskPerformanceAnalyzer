using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Maps Windows physical disk numbers to the drive letters mounted on them. Used as a fallback
/// when a performance counter instance name does not carry drive letters directly.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DiskDriveLetters
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;

    /// <summary>
    /// Queries all fixed/removable drives and returns a map of physical disk number to the
    /// list of drive letters (e.g. "C:\") mounted on it. Drives that fail to query are skipped.
    /// Never throws.
    /// </summary>
    public static IReadOnlyDictionary<int, IReadOnlyList<string>> Query()
    {
        var result = new Dictionary<int, List<string>>();

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return new Dictionary<int, IReadOnlyList<string>>();
        }

        foreach (var drive in drives)
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                {
                    continue;
                }

                var letter = drive.Name.TrimEnd('\\');
                if (letter.Length < 2)
                {
                    continue;
                }

                if (!TryGetPhysicalDiskNumber(letter, out var diskNumber))
                {
                    continue;
                }

                if (!result.TryGetValue(diskNumber, out var letters))
                {
                    letters = new List<string>();
                    result[diskNumber] = letters;
                }

                letters.Add(drive.Name);
            }
            catch
            {
                // Skip drives that fail to query (e.g. removed media, permission issues).
            }
        }

        var readOnly = new Dictionary<int, IReadOnlyList<string>>();
        foreach (var kvp in result)
        {
            readOnly[kvp.Key] = kvp.Value;
        }

        return readOnly;
    }

    private static bool TryGetPhysicalDiskNumber(string driveLetter, out int diskNumber)
    {
        diskNumber = -1;
        var path = $@"\\.\{driveLetter}";
        var handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        try
        {
            var bufferSize = Marshal.SizeOf<StorageDeviceNumber>();
            var buffer = Marshal.AllocHGlobal(bufferSize);
            try
            {
                if (!DeviceIoControl(handle, IoctlStorageGetDeviceNumber, IntPtr.Zero, 0, buffer, (uint)bufferSize, out _, IntPtr.Zero))
                {
                    return false;
                }

                var info = Marshal.PtrToStructure<StorageDeviceNumber>(buffer);
                diskNumber = info.DeviceNumber;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StorageDeviceNumber
    {
        public int DeviceType;
        public int DeviceNumber;
        public int PartitionNumber;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
