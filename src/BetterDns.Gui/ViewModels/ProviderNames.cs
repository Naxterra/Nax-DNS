using BetterDns.Gui.Localization;

namespace BetterDns.Gui.ViewModels;

public static class ProviderNames
{
    public static string Display(string name, string endpoint)
    {
        // Only generated placeholder names are translated; custom names are kept verbatim.
        if (name == "Control D (replace with your resolver URL)")
            return LocalizationManager.Get(Uri.TryCreate(endpoint, UriKind.Absolute, out var url) && url.Host == "freedns.controld.com" ? "Server.ControlDFree" : "Server.ControlD");
        if (name == "NextDNS (replace with your profile URL)")
            return LocalizationManager.Get(Uri.TryCreate(endpoint, UriKind.Absolute, out var url) && url.AbsolutePath.Trim('/').Length > 0 ? "Server.NextDNS" : "Server.NextDNSMissing");
        return name;
    }
}
