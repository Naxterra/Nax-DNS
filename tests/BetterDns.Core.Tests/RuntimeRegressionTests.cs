using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using BetterDns.Core.Configuration;
using BetterDns.Core.Dns;

namespace BetterDns.Core.Tests;

public sealed class RuntimeRegressionTests
{
    [Fact]
    public void First_run_configuration_serializes_and_round_trips_ipv4_and_ipv6()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        var config = DefaultConfiguration.Create();
        var json = JsonSerializer.Serialize(config, options);
        Assert.DoesNotContain("parsedBootstrapAddresses", json);
        Assert.DoesNotContain("hostName", json);
        var restored = JsonSerializer.Deserialize<BetterDnsConfiguration>(json, options)!;
        Assert.Equal(config.Upstreams[0].BootstrapAddresses, restored.Upstreams[0].BootstrapAddresses);
        Assert.Equal(config.Upstreams[0].ParsedBootstrapAddresses, restored.Upstreams[0].ParsedBootstrapAddresses);
    }

    [Theory]
    [InlineData(1, "192.0.2.10")]
    [InlineData(28, "2001:db8::10")]
    public void Bootstrap_answer_has_real_type_class_ttl_and_rdata_length(ushort type, string ip)
    {
        var query = DnsWire.CreateQuery("resolver.example", type);
        var answer = DnsWire.CreateAddressResponse(query, [IPAddress.Parse(ip)]);
        var record = answer.AsSpan(DnsWire.ReadFirstQuestion(answer).QuestionEndOffset);
        Assert.Equal(type, BinaryPrimitives.ReadUInt16BigEndian(record[2..]));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(record[4..]));
        Assert.Equal(300u, BinaryPrimitives.ReadUInt32BigEndian(record[6..]));
        Assert.Equal(IPAddress.Parse(ip).GetAddressBytes().Length, BinaryPrimitives.ReadUInt16BigEndian(record[10..]));
        Assert.Equal(IPAddress.Parse(ip).GetAddressBytes(), record[12..].ToArray());
    }

    [Theory]
    [InlineData("resolver.example:8853", "resolver.example")]
    [InlineData("tls://resolver.example:853", "resolver.example")]
    [InlineData("[2001:db8::1]:853", "2001:db8::1")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void Stream_endpoint_hostname_is_parsed_correctly(string endpoint, string expected)
    {
        var upstream = new UpstreamDefinition { Id = "test", Name = "test", Protocol = DnsProtocol.Dot, Endpoint = endpoint };
        Assert.Equal(expected, upstream.HostName);
    }
}
