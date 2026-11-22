using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SiteLift.App.Views;

internal static class VirtualDiskCapacity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct SizeInfo
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(8)] public ulong VirtualSize;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint OpenVirtualDisk(ref StorageType storageType, string path, uint access,
        uint flags, IntPtr parameters, out SafeFileHandle handle);

    [DllImport("virtdisk.dll", ExactSpelling = true)]
    private static extern uint GetVirtualDiskInformation(SafeFileHandle handle, ref uint size,
        ref SizeInfo information, IntPtr sizeUsed);

    internal static ulong? Read(string location, string fileName)
    {
        if (!Path.IsPathFullyQualified(location) || Path.IsPathRooted(fileName)) return null;

        try
        {
            string path = Path.GetFullPath(Path.Combine(location, fileName));
            return ReadWindowsCapacity(path) ?? ReadVhdxMetadata(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static ulong? ReadWindowsCapacity(string path)
    {
        var type = new StorageType { DeviceId = 3, VendorId = new Guid("EC984AEC-A0F9-47E9-901F-71415A66345B") };
        const uint getInfo = 0x00080000;
        uint result = OpenVirtualDisk(ref type, path, getInfo, 0, IntPtr.Zero, out var handle);
        using (handle)
        {
            if (result != 0) return null;
            var info = new SizeInfo { Version = 1 };
            uint size = (uint)Marshal.SizeOf<SizeInfo>();
            return GetVirtualDiskInformation(handle, ref size, ref info, IntPtr.Zero) == 0 ? info.VirtualSize : null;
        }
    }

    // MS-VHDX stores the virtual size as an eight-byte metadata item.
    private static ulong? ReadVhdxMetadata(string path)
    {
        const int kib = 1024;
        var metadataRegion = new Guid("8B7CA206-4790-4B9A-B8FE-575F050F886E");
        var virtualSize = new Guid("2FA54224-CD1B-4876-B211-5DBED83BF4B8");
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt64() != 0x656c696678646876) return null; // vhdxfile

        foreach (int table in new[] { 192 * kib, 256 * kib })
        {
            stream.Position = table;
            if (reader.ReadUInt32() != 0x69676572) continue; // regi
            stream.Position = table + 8;
            uint regionCount = reader.ReadUInt32();
            if (regionCount > 2047) continue;
            for (int region = 0; region < regionCount; region++)
            {
                stream.Position = table + 16 + region * 32;
                if (new Guid(reader.ReadBytes(16)) != metadataRegion) continue;
                long offset = reader.ReadInt64();
                uint length = reader.ReadUInt32();
                if (offset < 1024 * kib || length < 64 * kib || offset > stream.Length - length) continue;
                stream.Position = offset;
                if (reader.ReadUInt64() != 0x617461646174656d) continue; // metadata
                stream.Position = offset + 10;
                int itemCount = reader.ReadUInt16();
                if (itemCount > 2047) continue;
                for (int item = 0; item < itemCount; item++)
                {
                    stream.Position = offset + 32 + item * 32;
                    if (new Guid(reader.ReadBytes(16)) != virtualSize) continue;
                    uint relative = reader.ReadUInt32();
                    if (reader.ReadUInt32() != 8 || relative < 64 * kib || relative > length - 8) continue;
                    stream.Position = offset + relative;
                    ulong bytes = reader.ReadUInt64();
                    if (bytes > 0 && bytes <= (64UL << 40) && bytes % 512 == 0) return bytes;
                }
            }
        }
        return null;
    }

    internal static string Format(ulong bytes)
    {
        const double gib = 1024d * 1024 * 1024;
        return bytes >= gib * 1024 ? $"{bytes / (gib * 1024):0.##} TiB" :
            bytes >= gib ? $"{bytes / gib:0.##} GiB" : $"{bytes / (1024d * 1024):0.##} MiB";
    }
}
