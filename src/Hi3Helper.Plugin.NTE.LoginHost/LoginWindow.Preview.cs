using System.IO;
using System.Text.Json;

namespace Nte.LoginHost;

internal sealed partial class LoginWindow
{
    internal void PreviewFirstUseNotice()
    {
        if (!preview) throw new InvalidOperationException("Synthetic preview required.");
        noticeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        noticeGlobal = globalView != null;
        ShowLoginNotice();
    }
    private string? lastPreviewAction;
    private bool previewActionRunning;

    // Isolated, non-authenticating fixture commands. This path is never used
    // by a real login session and only touches the in-memory preview accounts.
    private async Task ProcessPreviewActionAsync()
    {
        if (!preview || previewActionRunning) return;
        string commandPath = Path.Combine(AppContext.BaseDirectory, "preview-command.json");
        if (!File.Exists(commandPath) || new FileInfo(commandPath).Length > 2048) return;
        previewActionRunning = true;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(commandPath));
            JsonElement command = document.RootElement;
            string id = command.GetProperty("id").GetString()!;
            if (id == lastPreviewAction) return;
            lastPreviewAction = id;
            switch (command.GetProperty("action").GetString())
            {
                case "send-sms": await SendSmsFromUiAsync(); break;
                case "accounts": await SwitchModeAsync(true); break;
                case "sms": await SwitchModeAsync(false); break;
                case "remove":
                    Guid account = command.GetProperty("account").GetGuid();
                    await RemoveAccountAsync(previewAccounts.Single(a => a.Id == account)); break;
                default: throw new InvalidDataException("Unknown preview action.");
            }
            WritePreviewState();
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-action-result.json"), JsonSerializer.Serialize(new { id, passed = true }));
        }
        finally { previewActionRunning = false; }
    }
}
