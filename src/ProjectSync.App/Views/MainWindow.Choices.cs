using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ProjectSync.Core;

namespace ProjectSync.App.Views;

public partial class MainWindow
{
    private string ChoicesFile => ProjectSync.App.App.Store.ChoicesFilePath;

    private T? ReadChoices<T>(string key) where T : class
    {
        if (!restoreDetectedOptions) return null;
        try { return ReadChoiceCache()[key]?.Deserialize<T>(); }
        catch (JsonException) { return null; }
    }

    private JsonObject ReadChoiceCache()
    {
        try
        {
            return File.Exists(ChoicesFile) && JsonNode.Parse(File.ReadAllText(ChoicesFile)) is JsonObject cache
                ? cache : new JsonObject();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return new JsonObject();
        }
    }

    private string? SaveChoices<T>(string key, T value)
    {
        try
        {
            var cache = ReadChoiceCache();
            cache[key] = JsonSerializer.SerializeToNode(value);
            AppDataStore.WriteTextAtomically(ChoicesFile, cache.ToJsonString());
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "无法保存下拉列表缓存：" + error.Message;
        }
    }

    private static string ChoiceKey(params string[] parts) => JsonSerializer.Serialize(parts);

    private string RemoteChoiceKey() => ChoiceKey("remotes", windowsRepo.Text.Trim());
    private string BranchChoiceKey() => ChoiceKey("branches", windowsRepo.Text.Trim(), remote.Text.Trim(),
        remoteUrl.Text.Trim(), githubUsername.Text.Trim(), gitSshCommand.Text.Trim());
    private string DistributionChoiceKey() => ChoiceKey("distributions", wslRepo.Text.Trim());
    private string UserChoiceKey(string distribution) => ChoiceKey("users", distribution, wslRepo.Text.Trim());

    private static void RestoreChoices(ComboBox field, string selected, string[]? choices)
    {
        field.ItemsSource = (choices ?? Array.Empty<string>())
            .Concat(selected.Length > 0 ? new[] { selected } : Array.Empty<string>()).Distinct().ToArray();
        field.SelectedItem = selected.Length > 0 ? selected : null;
        if (field.IsEditable) field.Text = selected;
    }
}
