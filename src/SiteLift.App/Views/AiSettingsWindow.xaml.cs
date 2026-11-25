using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using SiteLift.Core;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;

namespace SiteLift.App.Views;

public partial class AiSettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly AppDataStore store;
    private AiSettings loadedSettings;
    private bool clearSavedKey;
    private bool testing;

    public AiSettings? SavedSettings { get; private set; }

    public AiSettingsWindow(AppDataStore store)
    {
        this.store = store;
        InitializeComponent();
        loadedSettings = store.LoadAiSettings();
        PopulateFields(loadedSettings);
    }

    private void PopulateFields(AiSettings settings)
    {
        AiEnabledSwitch.IsChecked = settings.Enabled;
        EndpointBox.Text = settings.Endpoint;
        ModelBox.Text = settings.Model;
        clearSavedKey = false;
        UpdateKeyStateVisual(settings.HasApiKey);
    }

    private void UpdateKeyStateVisual(bool hasSavedKey)
    {
        if (hasSavedKey && !clearSavedKey)
        {
            ApiKeyBox.PlaceholderText = "已安全保存 API Key（留空保持不变，输入新值可覆盖）";
            ClearKeyButton.Visibility = Visibility.Visible;
        }
        else
        {
            ApiKeyBox.PlaceholderText = "输入 API Key（本地 Ollama 可留空）";
            ClearKeyButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string preset })
            return;

        switch (preset)
        {
            case "deepseek":
                EndpointBox.Text = "https://api.deepseek.com/v1";
                ModelBox.Text = "deepseek-chat";
                break;
            case "openai":
                EndpointBox.Text = "https://api.openai.com/v1";
                ModelBox.Text = "gpt-4o-mini";
                break;
            case "gemini":
                EndpointBox.Text = "https://generativelanguage.googleapis.com/v1beta/openai";
                ModelBox.Text = "gemini-2.5-flash";
                break;
            case "ollama":
                EndpointBox.Text = "http://localhost:11434/v1";
                ModelBox.Text = "qwen2.5:14b";
                break;
        }
        AiEnabledSwitch.IsChecked = true;
    }

    private void ClearKeyClick(object sender, RoutedEventArgs e)
    {
        clearSavedKey = true;
        ApiKeyBox.Password = "";
        UpdateKeyStateVisual(false);
        StatusInfoBar.ShowFeedback("已标记清除本地密钥，点击「保存设置」后生效。", InfoBarSeverity.Informational);
    }

    private AiSettings BuildDraftSettings(out string? plainKeyOverride)
    {
        string enteredKey = (ApiKeyBox.Password ?? "").Trim();
        string protectedKey;
        if (enteredKey.Length > 0)
        {
            protectedKey = DpapiSecretProtector.Protect(enteredKey);
            plainKeyOverride = enteredKey;
        }
        else if (clearSavedKey)
        {
            protectedKey = "";
            plainKeyOverride = "";
        }
        else
        {
            protectedKey = loadedSettings.ProtectedApiKey;
            plainKeyOverride = null;
        }

        return new AiSettings(
            Enabled: AiEnabledSwitch.IsChecked == true,
            Endpoint: EndpointBox.Text.Trim(),
            Model: ModelBox.Text.Trim(),
            ProtectedApiKey: protectedKey,
            TimeoutSeconds: loadedSettings.TimeoutSeconds).Normalize();
    }

    private async void TestClick(object sender, RoutedEventArgs e)
    {
        if (testing) return;

        AiSettings draft;
        string? plainKeyOverride;
        try
        {
            draft = BuildDraftSettings(out plainKeyOverride);
        }
        catch (Exception error)
        {
            StatusInfoBar.ShowFeedback(error.Message, InfoBarSeverity.Error);
            return;
        }

        testing = true;
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        TestProgressRing.Visibility = Visibility.Visible;
        StatusInfoBar.IsOpen = false;
        try
        {
            var (ok, message) = await AiAdvisorClient.TestConnectionAsync(draft, plainKeyOverride, CancellationToken.None);
            StatusInfoBar.ShowFeedback(message, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        }
        finally
        {
            testing = false;
            TestButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            TestProgressRing.Visibility = Visibility.Collapsed;
        }
    }

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (testing) return;
        try
        {
            var draft = BuildDraftSettings(out _);
            if (draft.Enabled)
            {
                AiAdvisorClient.BuildChatCompletionsUri(draft.Endpoint);
                if (string.IsNullOrWhiteSpace(draft.Model))
                    throw new ArgumentException("启用 AI 助手时请填写模型名称。");
            }
            store.SaveAiSettings(draft);
            SavedSettings = draft;
            DialogResult = true;
            Close();
        }
        catch (Exception error)
        {
            StatusInfoBar.ShowFeedback($"无法保存设置：{error.Message}", InfoBarSeverity.Error);
        }
    }

    private void CancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
