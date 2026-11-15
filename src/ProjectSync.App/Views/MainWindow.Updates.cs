using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private const string ReleasesUrl = "https://github.com/nrjycyd/projectsync/releases";
    private const string LatestReleaseApi = "https://api.github.com/repos/nrjycyd/projectsync/releases/latest";
    private static readonly Version CurrentVersion = typeof(MainWindow).Assembly.GetName().Version!;
    private static readonly HttpClient ReleaseClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "检查中…";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd($"ProjectSync/{CurrentVersion.ToString(3)}");
            using var response = await ReleaseClient.SendAsync(request);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                OfferReleasesPage("GitHub 没有返回可公开读取的版本。是否在浏览器打开发布页？");
                return;
            }

            response.EnsureSuccessStatusCode();
            using var release = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (release.RootElement.ValueKind != JsonValueKind.Object ||
                !release.RootElement.TryGetProperty("tag_name", out var tag) ||
                tag.ValueKind != JsonValueKind.String ||
                !Version.TryParse(tag.GetString()?.TrimStart('v'), out var latest) ||
                latest.Build < 0 || latest.Revision >= 0)
            {
                OfferReleasesPage("无法识别最新版本号。是否在浏览器打开发布页？");
                return;
            }

            var current = new Version(CurrentVersion.ToString(3));
            if (latest > current)
                OfferReleasesPage($"发现新版 v{latest}。是否打开 GitHub 发布页？");
            else
                MessageBox.Show(this, $"当前版本 v{current} 已是最新。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
        {
            OfferReleasesPage("暂时无法检查更新。是否在浏览器打开发布页？");
        }
        finally
        {
            CheckUpdatesButton.Content = "检查更新";
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    private void OfferReleasesPage(string message)
    {
        if (MessageBox.Show(this, message, "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(ReleasesUrl) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"无法打开浏览器，请访问 {ReleasesUrl}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
