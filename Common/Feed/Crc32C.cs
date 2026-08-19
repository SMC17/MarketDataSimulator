using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;
using X86Crc32 = System.Runtime.Intrinsics.X86.Sse42;

namespace MarketData.Common.Feed
{
    /// <summary>Allocation-free CRC-32C with x64 and Arm64 hardware paths.</summary>
    public static class Crc32C
    {
        private const uint Polynomial = 0x82F63B78u;

        public static bool IsHardwareAccelerated => X86Crc32.X64.IsSupported || ArmCrc32.Arm64.IsSupported;
        public static string Implementation => X86Crc32.X64.IsSupported ? "SSE4.2" :
            ArmCrc32.Arm64.IsSupported ? "ARMv8 CRC" : "software";

        public static uint Compute(ReadOnlySpan<byte> data)
        {
            var crc = uint.MaxValue;
            crc = Append(crc, data);
            return ~crc;
        }

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = uint.MaxValue;
            crc = Append(crc, first);
            crc = Append(crc, second);
            return ~crc;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Append(uint crc, ReadOnlySpan<byte> data)
        {
            var offset = 0;

            if (X86Crc32.X64.IsSupported)
            {
                var wide = (ulong)crc;

                while (offset <= data.Length - sizeof(ulong))
                {
                    wide = X86Crc32.X64.Crc32(wide,
                        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong))));
                    offset += sizeof(ulong);
                }

                crc = (uint)wide;

                while (offset < data.Length)
                    crc = X86Crc32.Crc32(crc, data[offset++]);

                return crc;
            }

            if (ArmCrc32.Arm64.IsSupported)
            {
                while (offset <= data.Length - sizeof(ulong))
                {
                    crc = ArmCrc32.Arm64.ComputeCrc32C(crc,
                        BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, sizeof(ulong))));
                    offset += sizeof(ulong);
                }

                while (offset < data.Length)
                    crc = ArmCrc32.ComputeCrc32C(crc, data[offset++]);

                return crc;
            }

            while (offset < data.Length)
            {
                crc ^= data[offset++];

                for (var bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ (Polynomial & (uint)-(int)(crc & 1));
            }

            return crc;
        }
    }
}
