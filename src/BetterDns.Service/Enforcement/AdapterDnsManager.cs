using System.Text.Json;
using BetterDns.Service.Configuration;

namespace BetterDns.Service.Enforcement;

public sealed class AdapterDnsManager
{
    private static readonly string StatePath = Path.Combine(ConfigurationStore.DataDirectory, "adapter-backup.json");
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public async Task RestoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(StatePath))
        {
            return;
        }

        var adapters = JsonSerializer.Deserialize<IReadOnlyList<AdapterBackup>>(File.ReadAllText(StatePath), jsonOptions) ?? [];
        foreach (var adapter in adapters)
        {
            var addresses = adapter.ServerAddresses.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var addressLiteral = string.Join(',', addresses.Select(static value => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"));
            var script = addresses.Length == 0
                ? $"Set-DnsClientServerAddress -InterfaceIndex {adapter.InterfaceIndex} -ResetServerAddresses -ErrorAction SilentlyContinue"
                : $"Set-DnsClientServerAddress -InterfaceIndex {adapter.InterfaceIndex} -ServerAddresses @({addressLiteral}) -ErrorAction SilentlyContinue";
            await PowerShellRunner.RunAsync(script, cancellationToken).ConfigureAwait(false);
        }

        File.Delete(StatePath);
    }
    private sealed record AdapterBackup(int InterfaceIndex, string InterfaceAlias, IReadOnlyList<string> ServerAddresses);
}
