namespace ProjectSync.App.Views;

public partial class WslManagerWindow
{
    private static string DistributionLabel(string? flavor, string? version)
    {
        string name = flavor?.ToLowerInvariant() switch
        {
            "ubuntu" => "Ubuntu",
            "debian" => "Debian",
            "kali" or "kali-linux" => "Kali Linux",
            null or "" => "未知发行版",
            _ => flavor
        };
        return string.IsNullOrWhiteSpace(version) ? name : $"{name} {version}";
    }

}
