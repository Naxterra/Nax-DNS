using System.Buffers.Binary;

namespace BetterDns.Core.Dns;

public static class UdpPacketRewriter
{
    private const byte UdpProtocol = 17;

    public static bool TryGetDnsPayload(ReadOnlySpan<byte> packet, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!TryGetUdpOffset(packet, out var udpOffset) || udpOffset + 8 > packet.Length)
        {
            return false;
        }

        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(packet[(udpOffset + 4)..]);
        if (udpLength < 8 || udpOffset + udpLength > packet.Length)
        {
            return false;
        }

        payload = packet.Slice(udpOffset + 8, udpLength - 8);
        return true;
    }

    public static byte[] CreateResponse(ReadOnlySpan<byte> requestPacket, ReadOnlySpan<byte> dnsResponse)
    {
        if (!TryGetUdpOffset(requestPacket, out var udpOffset) || udpOffset + 8 > requestPacket.Length)
        {
            throw new InvalidDataException("Packet does not contain a complete UDP header.");
        }

        var packetLength = checked(udpOffset + 8 + dnsResponse.Length);
        if (packetLength > ushort.MaxValue)
        {
            throw new InvalidDataException("DNS response is too large for an IP packet.");
        }

        var response = new byte[packetLength];
        requestPacket[..(udpOffset + 8)].CopyTo(response);
        dnsResponse.CopyTo(response.AsSpan(udpOffset + 8));
        var version = response[0] >> 4;

        if (version == 4)
        {
            Swap(response.AsSpan(12, 4), response.AsSpan(16, 4));
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2), checked((ushort)packetLength));
            response[8] = 64;
            response[10] = 0;
            response[11] = 0;
        }
        else if (version == 6)
        {
            Swap(response.AsSpan(8, 16), response.AsSpan(24, 16));
            BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(4), checked((ushort)(packetLength - 40)));
            response[7] = 64;
        }
        else
        {
            throw new InvalidDataException("Packet is neither IPv4 nor IPv6.");
        }

        Swap(response.AsSpan(udpOffset, 2), response.AsSpan(udpOffset + 2, 2));
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(udpOffset + 4), checked((ushort)(dnsResponse.Length + 8)));
        response[udpOffset + 6] = 0;
        response[udpOffset + 7] = 0;
        return response;
    }

    private static bool TryGetUdpOffset(ReadOnlySpan<byte> packet, out int udpOffset)
    {
        udpOffset = 0;
        if (packet.Length < 1)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            if (packet.Length < 20 || packet[9] != UdpProtocol)
            {
                return false;
            }

            udpOffset = (packet[0] & 0x0F) * 4;
            return udpOffset >= 20 && udpOffset <= packet.Length;
        }

        if (version != 6 || packet.Length < 40)
        {
            return false;
        }

        var nextHeader = packet[6];
        var offset = 40;
        while (nextHeader != UdpProtocol)
        {
            if (offset + 2 > packet.Length)
            {
                return false;
            }

            var currentHeader = nextHeader;
            nextHeader = packet[offset];
            int extensionLength;
            switch (currentHeader)
            {
                case 0:
                case 43:
                case 60:
                    extensionLength = (packet[offset + 1] + 1) * 8;
                    break;
                case 44:
                    extensionLength = 8;
                    break;
                case 51:
                    extensionLength = (packet[offset + 1] + 2) * 4;
                    break;
                default:
                    return false;
            }

            offset += extensionLength;
            if (offset > packet.Length)
            {
                return false;
            }
        }

        udpOffset = offset;
        return true;
    }

    private static void Swap(Span<byte> left, Span<byte> right)
    {
        if (left.Length != right.Length)
        {
            throw new ArgumentException("Swap spans must have the same length.");
        }

        var temporary = left.ToArray();
        right.CopyTo(left);
        temporary.CopyTo(right);
    }
}
