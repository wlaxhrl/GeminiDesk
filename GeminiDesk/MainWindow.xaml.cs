using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Win32;

namespace GeminiDesk;

public partial class MainWindow : Window
{
    private const string ApiKeyCredentialTarget = "GeminiDesk:GoogleGeminiApiKey";
    private const string DefaultModelId = "gemini-3.5-flash";
    private const string ProModelId = "gemini-3.1-pro-preview";
    private const string SelectedModelSettingKey = "selected-model";
    private const long MaxFileSize = 10 * 1024 * 1024;
    private const long MaxTotalAttachmentSize = 20 * 1024 * 1024;
    private readonly List<Content> _conversationHistory = [];
    private readonly List<AttachmentItem> _attachments = [];
    private readonly WindowsCredentialStore _apiKeyCredentialStore = new(ApiKeyCredentialTarget);
    private readonly ChatStore _chatStore;
    private CancellationTokenSource? _generationCancellation;
    private ChatMessage? _editingUserMessage;
    private string? _currentConversationId;
    private string _selectedModelId = DefaultModelId;
    private bool _isUpdatingRememberApiKey;
    private bool _isUpdatingConversationSelection;
    private bool _isUpdatingModelSelection;

    public ObservableCollection<ChatMessage> Messages { get; } = [];
    public ObservableCollection<ConversationSummary> Conversations { get; } = [];

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        LoadRememberedApiKey();
        _chatStore = new ChatStore();
        LoadSelectedModel();
        _chatStore.CleanupOrphanedAttachments(TimeSpan.FromDays(7));
        RefreshConversations();
        ContentRendered += (_, _) => PromptBox.Focus();
        PromptBox.Focus();
    }

    private void LoadSelectedModel()
    {
        try
        {
            var storedModelId = _chatStore.GetSetting(SelectedModelSettingKey);
            _selectedModelId = IsSupportedModel(storedModelId)
                ? storedModelId!
                : DefaultModelId;
        }
        catch
        {
            _selectedModelId = DefaultModelId;
            StatusText.Text = "모델 설정을 불러오지 못해 3.5 Flash를 사용해요";
        }

        _isUpdatingModelSelection = true;

        try
        {
            FlashModelRadioButton.IsChecked = _selectedModelId == DefaultModelId;
            ProModelRadioButton.IsChecked = _selectedModelId == ProModelId;
        }
        finally
        {
            _isUpdatingModelSelection = false;
        }
    }

    private void ModelChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingModelSelection ||
            sender is not RadioButton { Tag: string modelId } ||
            !IsSupportedModel(modelId))
        {
            return;
        }

        _selectedModelId = modelId;

        try
        {
            _chatStore.SetSetting(SelectedModelSettingKey, modelId);
            StatusText.Text = $"다음 답변부터 {GetModelDisplayName(modelId)}를 사용해요";
        }
        catch
        {
            StatusText.Text = $"{GetModelDisplayName(modelId)}를 사용해요 · 선택 기억 실패";
        }
    }

    private static bool IsSupportedModel(string? modelId)
    {
        return modelId is DefaultModelId or ProModelId;
    }

    private static string GetModelDisplayName(string modelId)
    {
        return modelId == ProModelId
            ? "Gemini 3.1 Pro Preview"
            : "Gemini 3.5 Flash";
    }

    private void LoadRememberedApiKey()
    {
        try
        {
            if (!_apiKeyCredentialStore.TryRead(out var apiKey))
            {
                return;
            }

            ApiKeyBox.Password = NormalizeApiKey(apiKey);
            SetRememberApiKeyChecked(true);
            StatusText.Text = "저장된 API 키를 불러왔어요";
        }
        catch (Exception exception)
        {
            SetRememberApiKeyChecked(false);
            StatusText.Text = "저장된 API 키를 불러오지 못했어요";
            MessageBox.Show(
                $"Windows에 저장된 API 키를 불러오지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "API 키 불러오기 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void RememberApiKeyCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingRememberApiKey)
        {
            return;
        }

        SaveRememberedApiKey(showConfirmation: true);
    }

    private void RememberApiKeyCheckBox_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingRememberApiKey)
        {
            return;
        }

        try
        {
            _apiKeyCredentialStore.Delete();
            StatusText.Text = "API 키 저장을 해제했어요";
        }
        catch (Exception exception)
        {
            SetRememberApiKeyChecked(true);
            MessageBox.Show(
                $"저장된 API 키를 삭제하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "API 키 삭제 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ApiKeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (RememberApiKeyCheckBox.IsChecked == true)
        {
            SaveRememberedApiKey(showConfirmation: false);
        }
    }

    private bool SaveRememberedApiKey(bool showConfirmation)
    {
        if (RememberApiKeyCheckBox.IsChecked != true)
        {
            return false;
        }

        var apiKey = NormalizeApiKey(ApiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (showConfirmation)
            {
                StatusText.Text = "API 키를 입력하면 안전하게 기억해요";
            }

            return false;
        }

        try
        {
            ApiKeyBox.Password = apiKey;
            _apiKeyCredentialStore.Write(apiKey);

            if (showConfirmation)
            {
                StatusText.Text = "API 키를 Windows에 안전하게 저장했어요";
            }

            return true;
        }
        catch (Exception exception)
        {
            try
            {
                _apiKeyCredentialStore.Delete();
            }
            catch
            {
                // 원래 저장 오류를 사용자에게 보여 줍니다.
            }

            SetRememberApiKeyChecked(false);
            MessageBox.Show(
                $"API 키를 Windows에 저장하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "API 키 저장 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }
    }

    private void SetRememberApiKeyChecked(bool isChecked)
    {
        _isUpdatingRememberApiKey = true;

        try
        {
            RememberApiKeyCheckBox.IsChecked = isChecked;
        }
        finally
        {
            _isUpdatingRememberApiKey = false;
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_generationCancellation is not null)
        {
            StatusText.Text = "답변 생성을 중지하는 중…";
            _generationCancellation.Cancel();
            return;
        }

        await SendMessageAsync();
    }

    private async void PromptBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;

        var isShiftPressed =
            e.KeyboardDevice.Modifiers.HasFlag(ModifierKeys.Shift) ||
            e.KeyboardDevice.IsKeyDown(Key.LeftShift) ||
            e.KeyboardDevice.IsKeyDown(Key.RightShift);

        if (isShiftPressed)
        {
            var selectionStart = PromptBox.SelectionStart;
            var selectionLength = PromptBox.SelectionLength;

            InsertPromptLineBreak(selectionStart, selectionLength);
            return;
        }

        await SendMessageAsync();
    }

    private void InsertPromptLineBreak(int selectionStart, int selectionLength)
    {
        var currentText = PromptBox.Text;
        var safeStart = Math.Clamp(selectionStart, 0, currentText.Length);
        var safeLength = Math.Clamp(selectionLength, 0, currentText.Length - safeStart);
        var lineBreak = System.Environment.NewLine;

        PromptBox.Text = currentText
            .Remove(safeStart, safeLength)
            .Insert(safeStart, lineBreak);
        PromptBox.Select(safeStart + lineBreak.Length, 0);
        PromptBox.Focus();
    }

    private async Task SendMessageAsync()
    {
        if (_generationCancellation is not null)
        {
            return;
        }

        var apiKey = NormalizeApiKey(ApiKeyBox.Password);
        var prompt = PromptBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                "Gemini API 키를 입력해 주세요.",
                "API 키 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ApiKeyBox.Focus();
            return;
        }

        if (apiKey != ApiKeyBox.Password)
        {
            ApiKeyBox.Password = apiKey;
        }

        if (string.IsNullOrWhiteSpace(prompt) && _attachments.Count == 0)
        {
            MessageBox.Show(
                "질문을 입력하거나 파일을 첨부해 주세요.",
                "내용 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            PromptBox.Focus();
            return;
        }

        var attachments = _attachments.ToList();
        var effectivePrompt = string.IsNullOrWhiteSpace(prompt)
            ? "첨부한 파일의 내용을 분석해 주세요."
            : prompt;
        var attachmentNames = attachments.Count == 0
            ? string.Empty
            : $"{System.Environment.NewLine}{System.Environment.NewLine}첨부: {string.Join(", ", attachments.Select(item => item.Name))}";

        IReadOnlyList<ChatAttachment> storedAttachments;

        try
        {
            storedAttachments = PreserveAttachments(attachments);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"첨부 파일을 보관하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "첨부 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        var userMessage = new ChatMessage(effectivePrompt + attachmentNames, true, [], storedAttachments);
        Messages.Add(userMessage);
        var modelMessage = new ChatMessage(string.Empty, false, [], []);
        modelMessage.IsStreaming = true;
        Messages.Add(modelMessage);
        UpdateMessageActionAvailability();
        PromptBox.Clear();
        ScrollToLatestMessage();
        _generationCancellation = new CancellationTokenSource();
        SetBusy(true);

        Content? userContent = null;

        try
        {
            var userParts = new List<Part> { new() { Text = effectivePrompt } };

            foreach (var attachment in storedAttachments)
            {
                var bytes = await System.IO.File.ReadAllBytesAsync(attachment.Path);
                userParts.Add(new Part
                {
                    InlineData = new Blob
                    {
                        Data = bytes,
                        MimeType = attachment.MimeType
                    }
                });
            }

            userContent = new Content
            {
                Role = "user",
                Parts = userParts
            };
            var requestContents = _conversationHistory.Append(userContent).ToList();
            var client = new Client(apiKey: apiKey);
            var config = WebSearchCheckBox.IsChecked == true
                ? new GenerateContentConfig
                {
                    Tools = [new Tool { GoogleSearch = new GoogleSearch() }]
                }
                : null;
            var collectedSources = new List<ChatSource>();

            await foreach (var chunk in client.Models.GenerateContentStreamAsync(
                               model: _selectedModelId,
                               contents: requestContents,
                               config: config,
                               cancellationToken: _generationCancellation.Token))
            {
                var chunkText = string.Concat(chunk.Candidates?
                    .SelectMany(candidate => candidate.Content?.Parts ?? [])
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrEmpty(text)) ?? []);

                if (!string.IsNullOrEmpty(chunkText))
                {
                    modelMessage.Text += chunkText;
                    ScrollToLatestMessage();
                }

                collectedSources.AddRange(ExtractSources(chunk));
            }

            if (string.IsNullOrWhiteSpace(modelMessage.Text))
            {
                modelMessage.Text = "응답 내용이 없습니다.";
            }

            modelMessage.Sources = collectedSources
                .DistinctBy(source => source.Uri)
                .ToList();
            modelMessage.IsStreaming = false;

            SaveRememberedApiKey(showConfirmation: false);
            CompleteExchange(userContent, userMessage, modelMessage, prompt, attachments, "응답 완료 · 저장됨");
        }
        catch (OperationCanceledException) when (_generationCancellation.IsCancellationRequested)
        {
            modelMessage.Text = string.IsNullOrWhiteSpace(modelMessage.Text)
                ? "답변 생성을 중지했습니다."
                : $"{modelMessage.Text}{System.Environment.NewLine}{System.Environment.NewLine}[생성 중지됨]";
            modelMessage.IsStreaming = false;

            if (userContent is not null)
            {
                CompleteExchange(userContent, userMessage, modelMessage, prompt, attachments, "생성 중지됨 · 저장됨");
            }
        }
        catch (Exception exception)
        {
            modelMessage.Text = exception.Message.Contains(
                "headers must contain only ASCII",
                StringComparison.OrdinalIgnoreCase)
                ? "API 키에 사용할 수 없는 문자가 포함되어 있습니다. API 키 입력창을 비우고 Google AI Studio에서 키만 다시 복사해 주세요."
                : $"요청 중 오류가 발생했습니다.{System.Environment.NewLine}{exception.Message}";
            modelMessage.IsStreaming = false;
            StatusText.Text = "요청 실패";
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            SetBusy(false);
            ScrollToLatestMessage();
            PromptBox.Focus();
        }
    }

    private void CompleteExchange(
        Content userContent,
        ChatMessage userMessage,
        ChatMessage modelMessage,
        string prompt,
        IReadOnlyList<AttachmentItem> attachments,
        string successStatus)
    {
        _conversationHistory.Add(userContent);
        _conversationHistory.Add(new Content
        {
            Role = "model",
            Parts = [new Part { Text = modelMessage.Text }]
        });
        _attachments.Clear();
        UpdateAttachmentSummary();

        try
        {
            _currentConversationId ??= _chatStore.CreateConversation(
                CreateConversationTitle(prompt, attachments));
            _chatStore.SaveMessage(_currentConversationId, userMessage);
            _chatStore.SaveMessage(_currentConversationId, modelMessage);
            RefreshConversations();
            UpdateMessageActionAvailability();
            StatusText.Text = successStatus;
        }
        catch (Exception storageException)
        {
            StatusText.Text = "답변 완료 · 저장 실패";
            MessageBox.Show(
                $"답변은 받았지만 대화를 저장하지 못했습니다.{System.Environment.NewLine}{storageException.Message}",
                "저장 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void NewChatButton_Click(object sender, RoutedEventArgs e)
    {
        StartNewChat();
        StatusText.Text = "새 대화 준비됨";
    }

    private void ConversationList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingConversationSelection || ConversationList.SelectedItem is not ConversationSummary conversation)
        {
            return;
        }

        LoadConversation(conversation.Id);
    }

    private void DeleteChatButton_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedConversation();
    }

    private void ConversationList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (System.Windows.Controls.ItemsControl.ContainerFromElement(
                ConversationList,
                e.OriginalSource as DependencyObject) is System.Windows.Controls.ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();

            var renameItem = new System.Windows.Controls.MenuItem { Header = "제목 변경" };
            renameItem.Click += RenameChatMenuItem_Click;
            var deleteItem = new System.Windows.Controls.MenuItem
            {
                Header = "삭제",
                Foreground = System.Windows.Media.Brushes.Firebrick
            };
            deleteItem.Click += DeleteChatMenuItem_Click;

            var menu = new System.Windows.Controls.ContextMenu
            {
                PlacementTarget = item
            };
            menu.Items.Add(renameItem);
            menu.Items.Add(new System.Windows.Controls.Separator());
            menu.Items.Add(deleteItem);
            menu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void RenameChatMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ConversationList.SelectedItem is not ConversationSummary conversation)
        {
            return;
        }

        var dialog = new RenameConversationDialog(conversation.Title)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _chatStore.RenameConversation(conversation.Id, dialog.ConversationTitle);
        RefreshConversations();
        StatusText.Text = "대화 제목을 변경했습니다";
    }

    private void DeleteChatMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DeleteSelectedConversation();
    }

    private void DeleteSelectedConversation()
    {
        if (ConversationList.SelectedItem is not ConversationSummary conversation)
        {
            MessageBox.Show(
                "삭제할 대화를 먼저 선택해 주세요.",
                "대화 선택",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"‘{conversation.Title}’ 대화와 첨부 파일을 삭제할까요?",
            "대화 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _chatStore.DeleteConversation(conversation.Id);

        if (_currentConversationId == conversation.Id)
        {
            StartNewChat();
        }

        RefreshConversations();
        StatusText.Text = "대화와 첨부 파일을 삭제했습니다";
    }

    private void StartNewChat()
    {
        _editingUserMessage = null;
        _currentConversationId = null;
        _conversationHistory.Clear();
        Messages.Clear();
        _attachments.Clear();
        UpdateAttachmentSummary();
        PromptBox.Clear();
        _isUpdatingConversationSelection = true;
        ConversationList.SelectedItem = null;
        _isUpdatingConversationSelection = false;
        PromptBox.Focus();
    }

    private void LoadConversation(string conversationId)
    {
        var storedMessages = _chatStore.GetMessages(conversationId);
        _editingUserMessage = null;
        _currentConversationId = conversationId;
        Messages.Clear();
        _conversationHistory.Clear();
        _attachments.Clear();
        UpdateAttachmentSummary();

        foreach (var message in storedMessages)
        {
            Messages.Add(message);
            var parts = new List<Part> { new() { Text = message.Text } };

            foreach (var attachment in message.Attachments)
            {
                if (!System.IO.File.Exists(attachment.Path))
                {
                    continue;
                }

                parts.Add(new Part
                {
                    InlineData = new Blob
                    {
                        Data = System.IO.File.ReadAllBytes(attachment.Path),
                        MimeType = attachment.MimeType
                    }
                });
            }

            _conversationHistory.Add(new Content
            {
                Role = message.IsUser ? "user" : "model",
                Parts = parts
            });
        }

        UpdateMessageActionAvailability();
        StatusText.Text = "저장된 대화를 불러왔습니다";
        ScrollToLatestMessage();
        PromptBox.Focus();
    }

    private void RefreshConversations()
    {
        var selectedId = _currentConversationId;
        _isUpdatingConversationSelection = true;
        Conversations.Clear();

        foreach (var conversation in _chatStore.GetConversations())
        {
            Conversations.Add(conversation);
        }

        ConversationList.SelectedItem = Conversations.FirstOrDefault(item => item.Id == selectedId);
        _isUpdatingConversationSelection = false;
    }

    private static string CreateConversationTitle(string prompt, IReadOnlyList<AttachmentItem> attachments)
    {
        var title = string.IsNullOrWhiteSpace(prompt)
            ? attachments.FirstOrDefault()?.Name ?? "새 대화"
            : string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return title.Length <= 36 ? title : $"{title[..36]}…";
    }

    private static string NormalizeApiKey(string value)
    {
        return new string(value
            .Where(character => character is >= '!' and <= '~')
            .ToArray());
    }

    private void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Gemini에 보낼 파일 선택",
            Multiselect = true,
            Filter = "지원 파일|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.bmp;*.pdf;*.txt;*.md;*.csv;*.json;*.xml;*.html;*.mp3;*.wav;*.m4a;*.aac;*.ogg;*.mp4;*.mov;*.webm|모든 파일|*.*"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var path in dialog.FileNames)
        {
            var file = new FileInfo(path);

            if (_attachments.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (file.Length > MaxFileSize)
            {
                MessageBox.Show(
                    $"{file.Name}은(는) 10MB보다 커서 첨부할 수 없습니다.",
                    "파일 크기 초과",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                continue;
            }

            if (_attachments.Sum(item => item.Size) + file.Length > MaxTotalAttachmentSize)
            {
                MessageBox.Show(
                    "한 번에 첨부할 수 있는 파일의 총 크기는 20MB입니다.",
                    "첨부 크기 초과",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                break;
            }

            var mimeType = GetMimeType(file.Extension);

            if (mimeType is null)
            {
                MessageBox.Show(
                    $"{file.Name} 형식은 아직 지원하지 않습니다.",
                    "지원하지 않는 파일",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                continue;
            }

            _attachments.Add(new AttachmentItem(file.Name, file.FullName, file.Length, mimeType));
        }

        UpdateAttachmentSummary();
    }

    private void ClearAttachmentsButton_Click(object sender, RoutedEventArgs e)
    {
        _attachments.Clear();
        UpdateAttachmentSummary();
    }

    private static IReadOnlyList<ChatAttachment> PreserveAttachments(IEnumerable<AttachmentItem> attachments)
    {
        var attachmentFolder = Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
            "GeminiDesk",
            "Attachments");
        Directory.CreateDirectory(attachmentFolder);

        var stored = new List<ChatAttachment>();

        foreach (var attachment in attachments)
        {
            var storedName = $"{Guid.NewGuid():N}{Path.GetExtension(attachment.Name)}";
            var storedPath = Path.Combine(attachmentFolder, storedName);
            System.IO.File.Copy(attachment.Path, storedPath, overwrite: false);
            stored.Add(new ChatAttachment(
                attachment.Name,
                storedPath,
                attachment.Size,
                attachment.MimeType,
                attachment.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)));
        }

        return stored;
    }

    private void OpenAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChatAttachment attachment })
        {
            return;
        }

        if (!System.IO.File.Exists(attachment.Path))
        {
            ShowMissingAttachmentMessage();
            return;
        }

        Process.Start(new ProcessStartInfo(attachment.Path) { UseShellExecute = true });
    }

    private void SaveAttachmentButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ChatAttachment attachment })
        {
            return;
        }

        if (!System.IO.File.Exists(attachment.Path))
        {
            ShowMissingAttachmentMessage();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "첨부 파일 저장",
            FileName = attachment.Name,
            Filter = "모든 파일|*.*"
        };

        if (dialog.ShowDialog() == true)
        {
            System.IO.File.Copy(attachment.Path, dialog.FileName, overwrite: true);
        }
    }

    private static void ShowMissingAttachmentMessage()
    {
        MessageBox.Show(
            "보관된 첨부 파일을 찾을 수 없습니다.",
            "파일 없음",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void UpdateAttachmentSummary()
    {
        AttachmentSummaryText.Text = _attachments.Count == 0
            ? "첨부 없음"
            : string.Join(", ", _attachments.Select(item => $"{item.Name} ({FormatFileSize(item.Size)})"));
        ClearAttachmentsButton.IsEnabled = _attachments.Count > 0 && SendButton.IsEnabled;
    }

    private static string FormatFileSize(long bytes)
    {
        return bytes >= 1024 * 1024
            ? $"{bytes / 1024d / 1024d:0.#}MB"
            : $"{Math.Max(1, bytes / 1024d):0.#}KB";
    }

    private static string? GetMimeType(string extension)
    {
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".html" => "text/html",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".ogg" => "audio/ogg",
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            _ => null
        };
    }

    private static IReadOnlyList<ChatSource> ExtractSources(GenerateContentResponse response)
    {
        return response.Candidates?
            .SelectMany(candidate => candidate.GroundingMetadata?.GroundingChunks ?? [])
            .Select(chunk => chunk.Web)
            .Where(web => web is not null && !string.IsNullOrWhiteSpace(web.Uri))
            .Select(web => new ChatSource(
                string.IsNullOrWhiteSpace(web!.Title) ? web.Uri! : web.Title,
                web.Uri!))
            .DistinctBy(source => source.Uri)
            .ToList() ?? [];
    }

    private void SourceLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void CopyResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not ChatMessage message ||
            string.IsNullOrEmpty(message.Text))
        {
            return;
        }

        CopyMessageToClipboard(message, "Gemini 응답 전체를 복사했어요");
    }

    private void CopyUserMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not ChatMessage message ||
            string.IsNullOrEmpty(message.Text))
        {
            return;
        }

        CopyMessageToClipboard(message, "내 메시지를 복사했어요");
    }

    private void CopyMessageToClipboard(ChatMessage message, string successStatus)
    {
        try
        {
            Clipboard.SetText(message.Text);
            StatusText.Text = successStatus;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"메시지를 클립보드에 복사하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "복사 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void EditUserMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.Tag is not ChatMessage userMessage ||
            !userMessage.CanEdit ||
            Messages.Count < 2 ||
            !ReferenceEquals(Messages[Messages.Count - 2], userMessage))
        {
            return;
        }

        if (_editingUserMessage is not null && !ReferenceEquals(_editingUserMessage, userMessage))
        {
            _editingUserMessage.IsEditing = false;
        }

        userMessage.EditText = GetEditableUserText(userMessage);
        userMessage.IsEditing = true;
        _editingUserMessage = userMessage;

        if (Messages.LastOrDefault() is { IsUser: false } modelMessage)
        {
            modelMessage.CanRegenerate = false;
        }

        SetBusy(false);
        StatusText.Text = "마지막 메시지를 편집하고 있어요";
    }

    private void EditMessageBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox textBox || !textBox.IsVisible)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            textBox.Focus();
            textBox.CaretIndex = textBox.Text.Length;
        });
    }

    private void CancelEditMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ChatMessage userMessage)
        {
            return;
        }

        userMessage.EditText = GetEditableUserText(userMessage);
        userMessage.IsEditing = false;

        if (ReferenceEquals(_editingUserMessage, userMessage))
        {
            _editingUserMessage = null;
        }

        UpdateMessageActionAvailability();
        SetBusy(false);
        StatusText.Text = "메시지 편집을 취소했어요";
        PromptBox.Focus();
    }

    private async void SaveEditMessageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ChatMessage userMessage)
        {
            return;
        }

        await RegenerateAfterUserEditAsync(userMessage);
    }

    private async Task RegenerateAfterUserEditAsync(ChatMessage userMessage)
    {
        var modelMessage = Messages.LastOrDefault();

        if (_generationCancellation is not null ||
            !userMessage.CanEdit ||
            !userMessage.IsEditing ||
            Messages.Count < 2 ||
            !ReferenceEquals(Messages[Messages.Count - 2], userMessage) ||
            modelMessage is null ||
            modelMessage.IsUser ||
            modelMessage.IsStreaming ||
            _currentConversationId is null ||
            _conversationHistory.Count < 2 ||
            !string.Equals(_conversationHistory[^2].Role, "user", StringComparison.Ordinal) ||
            !string.Equals(_conversationHistory[^1].Role, "model", StringComparison.Ordinal))
        {
            return;
        }

        var editedPrompt = userMessage.EditText.Trim();
        if (string.IsNullOrWhiteSpace(editedPrompt))
        {
            MessageBox.Show(
                "편집할 메시지 내용을 입력해 주세요.",
                "내용 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var apiKey = NormalizeApiKey(ApiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                "Gemini API 키를 입력해 주세요.",
                "API 키 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ApiKeyBox.Focus();
            return;
        }

        if (apiKey != ApiKeyBox.Password)
        {
            ApiKeyBox.Password = apiKey;
        }

        var originalUserText = userMessage.Text;
        var originalModelText = modelMessage.Text;
        var originalModelSources = modelMessage.Sources.ToList();
        var originalUserContent = _conversationHistory[^2];
        var originalModelContent = _conversationHistory[^1];
        var historyPrefixCount = _conversationHistory.Count - 2;

        var editedParts = new List<Part> { new() { Text = editedPrompt } };
        if (originalUserContent.Parts is not null)
        {
            editedParts.AddRange(originalUserContent.Parts.Skip(1));
        }

        var editedUserContent = new Content
        {
            Role = "user",
            Parts = editedParts
        };

        userMessage.Text = BuildUserMessageText(editedPrompt, userMessage.Attachments);
        userMessage.IsEditing = false;
        userMessage.CanEdit = false;
        _editingUserMessage = null;

        _conversationHistory.RemoveRange(historyPrefixCount, 2);
        _conversationHistory.Add(editedUserContent);

        modelMessage.CanRegenerate = false;
        modelMessage.Text = string.Empty;
        modelMessage.Sources = [];
        modelMessage.IsStreaming = true;
        _generationCancellation = new CancellationTokenSource();
        SetBusy(true);
        ScrollToLatestMessage();

        try
        {
            var client = new Client(apiKey: apiKey);
            var config = WebSearchCheckBox.IsChecked == true
                ? new GenerateContentConfig
                {
                    Tools = [new Tool { GoogleSearch = new GoogleSearch() }]
                }
                : null;
            var collectedSources = new List<ChatSource>();

            await foreach (var chunk in client.Models.GenerateContentStreamAsync(
                               model: _selectedModelId,
                               contents: _conversationHistory.ToList(),
                               config: config,
                               cancellationToken: _generationCancellation.Token))
            {
                var chunkText = string.Concat(chunk.Candidates?
                    .SelectMany(candidate => candidate.Content?.Parts ?? [])
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrEmpty(text)) ?? []);

                if (!string.IsNullOrEmpty(chunkText))
                {
                    modelMessage.Text += chunkText;
                    ScrollToLatestMessage();
                }

                collectedSources.AddRange(ExtractSources(chunk));
            }

            if (string.IsNullOrWhiteSpace(modelMessage.Text))
            {
                modelMessage.Text = "응답 내용이 없습니다.";
            }

            modelMessage.Sources = collectedSources
                .DistinctBy(source => source.Uri)
                .ToList();
            modelMessage.IsStreaming = false;

            _chatStore.ReplaceLatestExchange(_currentConversationId, userMessage, modelMessage);
            _conversationHistory.Add(new Content
            {
                Role = "model",
                Parts = [new Part { Text = modelMessage.Text }]
            });
            userMessage.EditText = editedPrompt;
            SaveRememberedApiKey(showConfirmation: false);
            UpdateMessageActionAvailability();

            try
            {
                RefreshConversations();
                StatusText.Text = "메시지를 고치고 Gemini 답변도 다시 만들었어요 · 저장됨";
            }
            catch
            {
                StatusText.Text = "편집한 대화는 저장됨 · 대화 목록 새로고침 실패";
            }
        }
        catch (OperationCanceledException) when (_generationCancellation.IsCancellationRequested)
        {
            RestoreExchangeBeforeEdit(
                userMessage,
                modelMessage,
                originalUserText,
                editedPrompt,
                originalModelText,
                originalModelSources,
                originalUserContent,
                originalModelContent,
                historyPrefixCount);
            StatusText.Text = "답변 생성을 중지했어요 · 편집 내용을 다시 확인해 주세요";
        }
        catch (Exception exception)
        {
            RestoreExchangeBeforeEdit(
                userMessage,
                modelMessage,
                originalUserText,
                editedPrompt,
                originalModelText,
                originalModelSources,
                originalUserContent,
                originalModelContent,
                historyPrefixCount);
            StatusText.Text = "메시지 편집 후 답변 생성 실패";
            MessageBox.Show(
                $"편집한 메시지로 답변을 다시 생성하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "다시 생성 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            SetBusy(false);
            ScrollToLatestMessage();

            if (!userMessage.IsEditing)
            {
                PromptBox.Focus();
            }
        }
    }

    private void RestoreExchangeBeforeEdit(
        ChatMessage userMessage,
        ChatMessage modelMessage,
        string originalUserText,
        string editedPrompt,
        string originalModelText,
        IReadOnlyList<ChatSource> originalModelSources,
        Content originalUserContent,
        Content originalModelContent,
        int historyPrefixCount)
    {
        if (_conversationHistory.Count > historyPrefixCount)
        {
            _conversationHistory.RemoveRange(
                historyPrefixCount,
                _conversationHistory.Count - historyPrefixCount);
        }

        _conversationHistory.Add(originalUserContent);
        _conversationHistory.Add(originalModelContent);

        userMessage.Text = originalUserText;
        userMessage.EditText = editedPrompt;
        userMessage.IsEditing = true;
        _editingUserMessage = userMessage;

        modelMessage.Text = originalModelText;
        modelMessage.Sources = originalModelSources;
        modelMessage.IsStreaming = false;
        UpdateMessageActionAvailability();
    }

    private static string GetEditableUserText(ChatMessage message)
    {
        if (message.Attachments.Count == 0)
        {
            return message.Text;
        }

        var attachmentSummary = $"{System.Environment.NewLine}{System.Environment.NewLine}첨부: {string.Join(", ", message.Attachments.Select(item => item.Name))}";
        return message.Text.EndsWith(attachmentSummary, StringComparison.Ordinal)
            ? message.Text[..^attachmentSummary.Length]
            : message.Text;
    }

    private static string BuildUserMessageText(
        string prompt,
        IReadOnlyList<ChatAttachment> attachments)
    {
        return attachments.Count == 0
            ? prompt
            : $"{prompt}{System.Environment.NewLine}{System.Environment.NewLine}첨부: {string.Join(", ", attachments.Select(item => item.Name))}";
    }

    private async void RegenerateResponseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not ChatMessage modelMessage)
        {
            return;
        }

        await RegenerateLatestResponseAsync(modelMessage);
    }

    private async Task RegenerateLatestResponseAsync(ChatMessage modelMessage)
    {
        if (_generationCancellation is not null ||
            !modelMessage.CanRegenerate ||
            !ReferenceEquals(Messages.LastOrDefault(), modelMessage) ||
            _currentConversationId is null ||
            _conversationHistory.Count < 2 ||
            !string.Equals(_conversationHistory[^1].Role, "model", StringComparison.Ordinal))
        {
            return;
        }

        var apiKey = NormalizeApiKey(ApiKeyBox.Password);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(
                "Gemini API 키를 입력해 주세요.",
                "API 키 필요",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ApiKeyBox.Focus();
            return;
        }

        if (apiKey != ApiKeyBox.Password)
        {
            ApiKeyBox.Password = apiKey;
        }

        var originalText = modelMessage.Text;
        var originalSources = modelMessage.Sources.ToList();
        var originalContent = _conversationHistory[^1];
        _conversationHistory.RemoveAt(_conversationHistory.Count - 1);

        modelMessage.CanRegenerate = false;
        modelMessage.Text = string.Empty;
        modelMessage.Sources = [];
        modelMessage.IsStreaming = true;
        _generationCancellation = new CancellationTokenSource();
        SetBusy(true);
        ScrollToLatestMessage();

        try
        {
            var client = new Client(apiKey: apiKey);
            var config = WebSearchCheckBox.IsChecked == true
                ? new GenerateContentConfig
                {
                    Tools = [new Tool { GoogleSearch = new GoogleSearch() }]
                }
                : null;
            var collectedSources = new List<ChatSource>();

            await foreach (var chunk in client.Models.GenerateContentStreamAsync(
                               model: _selectedModelId,
                               contents: _conversationHistory.ToList(),
                               config: config,
                               cancellationToken: _generationCancellation.Token))
            {
                var chunkText = string.Concat(chunk.Candidates?
                    .SelectMany(candidate => candidate.Content?.Parts ?? [])
                    .Select(part => part.Text)
                    .Where(text => !string.IsNullOrEmpty(text)) ?? []);

                if (!string.IsNullOrEmpty(chunkText))
                {
                    modelMessage.Text += chunkText;
                    ScrollToLatestMessage();
                }

                collectedSources.AddRange(ExtractSources(chunk));
            }

            if (string.IsNullOrWhiteSpace(modelMessage.Text))
            {
                modelMessage.Text = "응답 내용이 없습니다.";
            }

            modelMessage.Sources = collectedSources
                .DistinctBy(source => source.Uri)
                .ToList();
            modelMessage.IsStreaming = false;

            _chatStore.ReplaceLatestModelMessage(_currentConversationId, modelMessage);
            _conversationHistory.Add(new Content
            {
                Role = "model",
                Parts = [new Part { Text = modelMessage.Text }]
            });
            SaveRememberedApiKey(showConfirmation: false);
            UpdateMessageActionAvailability();

            try
            {
                RefreshConversations();
                StatusText.Text = "Gemini 답변을 다시 만들었어요 · 저장됨";
            }
            catch
            {
                StatusText.Text = "다시 만든 답변은 저장됨 · 대화 목록 새로고침 실패";
            }
        }
        catch (OperationCanceledException) when (_generationCancellation.IsCancellationRequested)
        {
            RestoreOriginalResponse(modelMessage, originalText, originalSources, originalContent);
            StatusText.Text = "답변 다시 생성을 중지했어요";
        }
        catch (Exception exception)
        {
            RestoreOriginalResponse(modelMessage, originalText, originalSources, originalContent);
            StatusText.Text = "답변 다시 생성 실패";
            MessageBox.Show(
                $"답변을 다시 생성하지 못했습니다.{System.Environment.NewLine}{exception.Message}",
                "다시 생성 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _generationCancellation?.Dispose();
            _generationCancellation = null;
            SetBusy(false);
            ScrollToLatestMessage();
            PromptBox.Focus();
        }
    }

    private void RestoreOriginalResponse(
        ChatMessage modelMessage,
        string originalText,
        IReadOnlyList<ChatSource> originalSources,
        Content originalContent)
    {
        modelMessage.Text = originalText;
        modelMessage.Sources = originalSources;
        modelMessage.IsStreaming = false;
        _conversationHistory.Add(originalContent);
        UpdateMessageActionAvailability();
    }

    private void UpdateMessageActionAvailability()
    {
        foreach (var message in Messages)
        {
            if (message.IsUser)
            {
                message.CanEdit = false;
            }
            else
            {
                message.CanRegenerate = false;
            }
        }

        if (Messages.LastOrDefault() is { IsUser: false, IsStreaming: false } lastMessage &&
            _currentConversationId is not null &&
            _conversationHistory.LastOrDefault() is { Role: "model" })
        {
            if (_editingUserMessage is null)
            {
                lastMessage.CanRegenerate = true;
            }

            if (Messages.Count >= 2 &&
                Messages[Messages.Count - 2] is { IsUser: true } lastUserMessage &&
                _conversationHistory.Count >= 2 &&
                string.Equals(_conversationHistory[^2].Role, "user", StringComparison.Ordinal))
            {
                lastUserMessage.CanEdit = true;
            }
        }
    }

    private void SetBusy(bool isBusy)
    {
        var isEditing = _editingUserMessage?.IsEditing == true;
        SendButton.IsEnabled = isBusy || !isEditing;
        SendButton.Content = isBusy ? "중지" : "보내기";
        NewChatButton.IsEnabled = !isBusy && !isEditing;
        ConversationList.IsEnabled = !isBusy && !isEditing;
        DeleteChatButton.IsEnabled = !isBusy && !isEditing;
        AttachButton.IsEnabled = !isBusy && !isEditing;
        ClearAttachmentsButton.IsEnabled = !isBusy && !isEditing && _attachments.Count > 0;
        WebSearchCheckBox.IsEnabled = !isBusy;
        ModelSelector.IsEnabled = !isBusy;
        ApiKeyBox.IsEnabled = !isBusy;
        RememberApiKeyCheckBox.IsEnabled = !isBusy;
        PromptBox.IsEnabled = !isBusy && !isEditing;

        if (isBusy)
        {
            StatusText.Text = "Gemini가 답변하는 중…";
        }
    }

    private void ScrollToLatestMessage()
    {
        Dispatcher.BeginInvoke(() => ChatScrollViewer.ScrollToEnd());
    }

    protected override void OnClosed(EventArgs e)
    {
        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        base.OnClosed(e);
    }
}

public sealed class ChatMessage : INotifyPropertyChanged
{
    private string _text;
    private string _editText;
    private IReadOnlyList<ChatSource> _sources;
    private bool _canEdit;
    private bool _canRegenerate;
    private bool _isEditing;
    private bool _isStreaming;

    public ChatMessage(
        string text,
        bool isUser,
        IReadOnlyList<ChatSource> sources,
        IReadOnlyList<ChatAttachment> attachments)
    {
        _text = text;
        _editText = text;
        IsUser = isUser;
        _sources = sources;
        Attachments = attachments;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
            {
                return;
            }

            _text = value;
            OnPropertyChanged();
        }
    }

    public bool IsUser { get; }

    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value)
            {
                return;
            }

            _editText = value;
            OnPropertyChanged();
        }
    }

    public bool CanEdit
    {
        get => _canEdit;
        set
        {
            if (_canEdit == value)
            {
                return;
            }

            _canEdit = value;
            OnPropertyChanged();
        }
    }

    public bool CanRegenerate
    {
        get => _canRegenerate;
        set
        {
            if (_canRegenerate == value)
            {
                return;
            }

            _canRegenerate = value;
            OnPropertyChanged();
        }
    }

    public bool IsStreaming
    {
        get => _isStreaming;
        set
        {
            if (_isStreaming == value)
            {
                return;
            }

            _isStreaming = value;
            OnPropertyChanged();
        }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ChatSource> Sources
    {
        get => _sources;
        set
        {
            if (ReferenceEquals(_sources, value))
            {
                return;
            }

            _sources = value;
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<ChatAttachment> Attachments { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record ChatSource(string Title, string Uri);

public sealed record AttachmentItem(string Name, string Path, long Size, string MimeType);

public sealed record ChatAttachment(
    string Name,
    string Path,
    long Size,
    string MimeType,
    bool IsImage)
{
    public string DisplaySize => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:0.#}MB"
        : $"{Math.Max(1, Size / 1024d):0.#}KB";
}

public sealed class AttachmentPreviewConverter : IValueConverter
{
    public object? Convert(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, System.Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
