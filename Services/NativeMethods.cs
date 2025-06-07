using Microsoft.Win32.SafeHandles;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ROCKNIXImageBurner.Services
{
    /// <summary>
    /// Contains P/Invoke (Platform Invoke) declarations for Windows API functions
    /// needed for raw, low-level disk access. This is an internal helper class.
    /// </summary>
    internal static class NativeMethods
    {
        #region Constants for CreateFile

        // Desired Access
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;

        // Flags and Attributes
        /// <summary>
        /// Disables system caching. All I/O operations are performed directly on the physical disk.
        /// Requires that all reads and writes be sector-aligned.
        /// </summary>
        public const uint FILE_FLAG_NO_BUFFERING = 0x20000000;
        /// <summary>
        /// Ensures that write operations are not completed until the data is physically written to the disk.
        /// </summary>
        public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

        #endregion

        #region Constants for SetFilePointerEx

        /// <summary>Move method for SetFilePointerEx: The starting point is zero or the beginning of the file.</summary>
        public const uint FILE_BEGIN = 0;

        #endregion

        #region Constants for DeviceIoControl

        // These are control codes sent to a device driver to perform an operation.

        /// <summary>Locks a volume to prevent other processes from accessing it.</summary>
        public const uint FSCTL_LOCK_VOLUME = 0x00090018;
        /// <summary>Dismounts a volume, making it inaccessible via its file system until it is remounted.</summary>
        public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
        /// <summary>Unlocks a previously locked volume.</summary>
        public const uint FSCTL_UNLOCK_VOLUME = 0x0009001C;
        /// <summary>Retrieves the geometry of a physical disk (sector size, etc.).</summary>
        public const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        /// <summary>Forces the system to re-read the partition table and update its properties for a disk.</summary>
        public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070020;

        #endregion

        #region Structs

        /// <summary>
        /// Represents the geometry of a disk device.
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct DISK_GEOMETRY
        {
            public long Cylinders;
            public int MediaType; // Corresponds to MEDIA_TYPE enum
            public uint TracksPerCylinder;
            public uint SectorsPerTrack;
            public uint BytesPerSector;
        }

        #endregion

        #region P/Invoke Method Declarations

        /// <summary>
        /// Creates or opens a file or I/O device.
        /// </summary>
        /// <returns>A SafeFileHandle wrapper for the device handle.</returns>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFile(
            string lpFileName,
            [MarshalAs(UnmanagedType.U4)] FileAccess dwDesiredAccess,
            [MarshalAs(UnmanagedType.U4)] FileShare dwShareMode,
            IntPtr lpSecurityAttributes,
            [MarshalAs(UnmanagedType.U4)] FileMode dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        /// <summary>
        /// Writes data to the specified file or I/O device.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteFile(
            SafeFileHandle hFile,
            byte[] lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            IntPtr lpOverlapped);

        /// <summary>
        /// Sends a control code directly to a specified device driver.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        /// <summary>
        /// Flushes the buffers of a specified file and causes all buffered data to be written to the file.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(SafeFileHandle hFile);

        /// <summary>
        /// Moves the file pointer of the specified file.
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetFilePointerEx(
            SafeFileHandle hFile,
            long liDistanceToMove,
            IntPtr lpNewFilePointer, // Can be null if not used
            uint dwMoveMethod);

        #endregion
    }
}