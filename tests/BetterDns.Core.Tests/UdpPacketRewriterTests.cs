using System.Buffers.Binary;
using System.Net;
using BetterDns.Core.Dns;

namespace BetterDns.Core.Tests;

public sealed class UdpPacketRewriterTests
{
    [Fact]
    public void IPv4_dns_response_reverses_endpoints_and_updates_lengths()
    {
        var query = DnsWire.CreateQuery("example.com");
        var request = CreateIpv4UdpPacket(query);
        var dnsResponse = DnsWire.CreateErrorResponse(query, 0);

        var response = UdpPacketRewriter.CreateResponse(request, dnsResponse);

        Assert.Equal(IPAddress.Parse("9.9.9.9").GetAddressBytes(), response.AsSpan(12, 4).ToArray());
        Assert.Equal(IPAddress.Parse("192.0.2.10").GetAddressBytes(), response.AsSpan(16, 4).ToArray());
        Assert.Equal((ushort)53, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(20)));
        Assert.Equal((ushort)53000, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(22)));
        Assert.Equal(response.Length, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2)));
        Assert.True(UdpPacketRewriter.TryGetDnsPayload(response, out var payload));
        Assert.Equal(dnsResponse, payload.ToArray());
    }

    [Fact]
    public void IPv6_dns_response_reverses_endpoints()
    {
        var query = DnsWire.CreateQuery("example.com", 28);
        var request = CreateIpv6UdpPacket(query);
        var dnsResponse = DnsWire.CreateErrorResponse(query, 0);

        var response = UdpPacketRewriter.CreateResponse(request, dnsResponse);

        Assert.Equal(IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes(), response.AsSpan(8, 16).ToArray());
        Assert.Equal(IPAddress.Parse("2001:db8::10").GetAddressBytes(), response.AsSpan(24, 16).ToArray());
        Assert.Equal(response.Length - 40, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(4)));
        Assert.True(UdpPacketRewriter.TryGetDnsPayload(response, out var payload));
        Assert.Equal(dnsResponse, payload.ToArray());
    }

    private static byte[] CreateIpv4UdpPacket(byte[] payload)
    {
        var packet = new byte[20 + 8 + payload.Length];
        packet[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), checked((ushort)packet.Length));
        packet[8] = 128;
        packet[9] = 17;
        IPAddress.Parse("192.0.2.10").GetAddressBytes().CopyTo(packet, 12);
        IPAddress.Parse("9.9.9.9").GetAddressBytes().CopyTo(packet, 16);
        WriteUdp(packet.AsSpan(20), payload, 53000);
        return packet;
    }

    private static byte[] CreateIpv6UdpPacket(byte[] payload)
    {
        var packet = new byte[40 + 8 + payload.Length];
        packet[0] = 0x60;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), checked((ushort)(packet.Length - 40)));
        packet[6] = 17;
        packet[7] = 128;
        IPAddress.Parse("2001:db8::10").GetAddressBytes().CopyTo(packet, 8);
        IPAddress.Parse("2001:4860:4860::8888").GetAddressBytes().CopyTo(packet, 24);
        WriteUdp(packet.AsSpan(40), payload, 53001);
        return packet;
    }

    private static void WriteUdp(Span<byte> destination, byte[] payload, ushort sourcePort)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, sourcePort);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 53);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], checked((ushort)(8 + payload.Length)));
        payload.CopyTo(destination[8..]);
    }
}
