using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace BetterDns.Core.Dns;

public sealed record DnsQuestion(string Name, ushort Type, ushort Class, int QuestionEndOffset);

public static class DnsWire
{
    public const int HeaderLength = 12;

    public static ushort GetId(ReadOnlySpan<byte> message)
    {
        EnsureHeader(message);
        return BinaryPrimitives.ReadUInt16BigEndian(message);
    }

    public static byte[] CreateQuery(string domain, ushort type = 1)
    {
        var normalized = domain.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A DNS name is required.", nameof(domain));
        }

        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(header, checked((ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1)));
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);
        foreach (var label in normalized.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63)
            {
                throw new ArgumentException("DNS labels must contain 1 to 63 ASCII bytes.", nameof(domain));
            }

            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> questionTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(questionTail, type);
        BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..], 1);
        stream.Write(questionTail);
        return stream.ToArray();
    }

    public static byte[] WithId(ReadOnlySpan<byte> message, ushort id)
    {
        var copy = message.ToArray();
        EnsureHeader(copy);
        BinaryPrimitives.WriteUInt16BigEndian(copy, id);
        return copy;
    }

    public static DnsQuestion ReadFirstQuestion(ReadOnlySpan<byte> message)
    {
        EnsureHeader(message);
        var questionCount = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        if (questionCount == 0)
        {
            throw new InvalidDataException("DNS message has no question.");
        }

        var offset = HeaderLength;
        var name = ReadName(message, ref offset);
        if (offset + 4 > message.Length)
        {
            throw new InvalidDataException("DNS question is truncated.");
        }

        var type = BinaryPrimitives.ReadUInt16BigEndian(message[offset..]);
        var @class = BinaryPrimitives.ReadUInt16BigEndian(message[(offset + 2)..]);
        return new(name, type, @class, offset + 4);
    }

    public static bool IsUsableResponse(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength || (response[2] & 0x80) == 0)
        {
            return false;
        }

        var responseCode = response[3] & 0x0F;
        return responseCode is not (2 or 5);
    }

    public static string ResponseCodeName(ReadOnlySpan<byte> response)
    {
        if (response.Length < HeaderLength)
        {
            return "INVALID";
        }

        return (response[3] & 0x0F) switch
        {
            0 => "NOERROR",
            1 => "FORMERR",
            2 => "SERVFAIL",
            3 => "NXDOMAIN",
            4 => "NOTIMP",
            5 => "REFUSED",
            var code => $"RCODE{code}"
        };
    }

    public static byte[] CreateErrorResponse(ReadOnlySpan<byte> query, byte responseCode)
    {
        var question = ReadFirstQuestion(query);
        var response = query[..question.QuestionEndOffset].ToArray();
        response[2] = (byte)((response[2] & 0x79) | 0x80);
        response[3] = (byte)((response[3] & 0xF0) | 0x80 | (responseCode & 0x0F));
        response.AsSpan(6, 6).Clear();
        return response;
    }

    public static byte[] CreateAddressResponse(ReadOnlySpan<byte> query, IReadOnlyList<IPAddress> addresses)
    {
        var question = ReadFirstQuestion(query);
        var selected = addresses
            .Where(address => question.Type switch
            {
                1 => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
                28 => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                _ => false
            })
            .ToArray();

        if (selected.Length == 0)
        {
            return CreateErrorResponse(query, 0);
        }

        using var stream = new MemoryStream();
        stream.Write(query[..question.QuestionEndOffset]);
        var header = stream.GetBuffer();
        header[2] = (byte)((header[2] & 0x79) | 0x80);
        header[3] = (byte)((header[3] & 0xF0) | 0x80);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), checked((ushort)selected.Length));
        header.AsSpan(8, 4).Clear();

        var fixedRecord = new byte[12];
        foreach (var address in selected)
        {
            fixedRecord.AsSpan().Clear();
            BinaryPrimitives.WriteUInt16BigEndian(fixedRecord, 0xC00C);
            BinaryPrimitives.WriteUInt16BigEndian(fixedRecord[2..], question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedRecord[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(fixedRecord[6..], 300);
            var bytes = address.GetAddressBytes();
            BinaryPrimitives.WriteUInt16BigEndian(fixedRecord[10..], checked((ushort)bytes.Length));
            stream.Write(fixedRecord);
            stream.Write(bytes);
        }

        return stream.ToArray();
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int offset)
    {
        var labels = new List<string>();
        var jumped = false;
        var returnOffset = -1;
        var hops = 0;

        while (offset < message.Length)
        {
            if (++hops > 128)
            {
                throw new InvalidDataException("DNS name compression loop detected.");
            }

            var length = message[offset++];
            if (length == 0)
            {
                if (jumped)
                {
                    offset = returnOffset;
                }

                return string.Join('.', labels).ToLowerInvariant();
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (offset >= message.Length)
                {
                    throw new InvalidDataException("DNS compression pointer is truncated.");
                }

                var pointer = ((length & 0x3F) << 8) | message[offset++];
                if (!jumped)
                {
                    returnOffset = offset;
                    jumped = true;
                }

                offset = pointer;
                continue;
            }

            if (length > 63 || offset + length > message.Length)
            {
                throw new InvalidDataException("DNS label is invalid.");
            }

            labels.Add(Encoding.ASCII.GetString(message.Slice(offset, length)));
            offset += length;
        }

        throw new InvalidDataException("DNS name is truncated.");
    }

    private static void EnsureHeader(ReadOnlySpan<byte> message)
    {
        if (message.Length < HeaderLength)
        {
            throw new InvalidDataException("DNS message is shorter than its header.");
        }
    }
}
