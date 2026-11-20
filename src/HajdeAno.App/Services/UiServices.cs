using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

namespace HajdeAno.App.Services;

/// <summary>Clipboard and file dialogs of a window.</summary>
public sealed class UiServices(TopLevel topLevel) : IUiServices
{
    public async Task SetClipboardTextAsync(string text)
    {
        if (topLevel.Clipboard is { } clipboard)
        {
            await clipboard.SetValueAsync(DataFormat.Text, text);
        }
    }

    public async Task<string?> PickTextFileAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open a text or Markdown file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Text and Markdown")
                    {
                        Patterns = ["*.txt", "*.md", "*.markdown"],
                    },
                    FilePickerFileTypes.All,
                ],
            }
        );
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveTextFileAsync(string suggestedName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save the case text",
                SuggestedFileName = suggestedName,
                DefaultExtension = "txt",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Text") { Patterns = ["*.txt"] },
                    new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                ],
            }
        );
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickAnswerFileAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open the answer with placeholders",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Word, text and Markdown")
                    {
                        Patterns = ["*.docx", "*.txt", "*.md", "*.markdown"],
                    },
                    FilePickerFileTypes.All,
                ],
            }
        );
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveWordFileAsync(string suggestedName)
    {
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save the restored Word document",
                SuggestedFileName = suggestedName,
                DefaultExtension = "docx",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    new FilePickerFileType("Word document") { Patterns = ["*.docx"] },
                ],
            }
        );
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickModelFileAsync()
    {
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Select a GGUF model",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("GGUF model") { Patterns = ["*.gguf"] }],
            }
        );
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false }
        );
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
