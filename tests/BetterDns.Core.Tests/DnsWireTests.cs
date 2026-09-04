using System.Net;
using BetterDns.Core.Dns;

namespace BetterDns.Core.Tests;

public sealed class DnsWireTests
{
    [Fact]
    public void Query_round_trips_first_question()
    {
        var query = DnsWire.CreateQuery("WWW.Example.COM.", 28);

        var question = DnsWire.ReadFirstQuestion(query);

        Assert.Equal("www.example.com", question.Name);
        Assert.Equal((ushort)28, question.Type);
        Assert.Equal((ushort)1, question.Class);
    }

    [Fact]
    public void Error_response_preserves_id_and_question()
    {
        var query = DnsWire.CreateQuery("blocked.example");

        var response = DnsWire.CreateErrorResponse(query, 5);

        Assert.Equal(DnsWire.GetId(query), DnsWire.GetId(response));
        Assert.Equal("blocked.example", DnsWire.ReadFirstQuestion(response).Name);
        Assert.Equal("REFUSED", DnsWire.ResponseCodeName(response));
    }

    [Theory]
    [InlineData(1, "192.0.2.1")]
    [InlineData(28, "2001:db8::1")]
    public void Bootstrap_response_is_a_valid_dns_answer(ushort type, string address)
    {
        var query = DnsWire.CreateQuery("resolver.example", type);

        var response = DnsWire.CreateAddressResponse(query, [IPAddress.Parse(address)]);

        Assert.True(DnsWire.IsUsableResponse(response));
        Assert.Equal(DnsWire.GetId(query), DnsWire.GetId(response));
        Assert.Equal(1, System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(6)));
    }
}
