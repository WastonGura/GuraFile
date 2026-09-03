namespace GuraFile.Storage;

public sealed record FileDetailsModel(
    string Title,
    string? Name,
    string? Path,
    string? Extension,
    string? SizeText,
    string? ModifiedText,
    string StatusText,
    string IdentityStateText,
    string UserTagsText,
    string AutomaticTagsText,
    string? Diagnostic,
    bool IsSingleFileSelected,
    bool IsMultipleFilesSelected,
    int SelectedCount,
    bool CanOpen,
    bool CanReveal,
    bool CanReidentify,
    bool CanCopyPath,
    bool IsTagSelected = false,
    string? TagTypeSummary = null);

public static class FileDetailsPresenter
{
    public static FileDetailsModel CreateForTag(string tagName, string tagTypeSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        ArgumentException.ThrowIfNullOrWhiteSpace(tagTypeSummary);

        return new FileDetailsModel(
            Title: $"标签：{tagName}",
            Name: tagName,
            Path: null,
            Extension: null,
            SizeText: null,
            ModifiedText: null,
            StatusText: "标签",
            IdentityStateText: tagTypeSummary,
            UserTagsText: "无",
            AutomaticTagsText: "无",
            Diagnostic: null,
            IsSingleFileSelected: false,
            IsMultipleFilesSelected: false,
            SelectedCount: 0,
            CanOpen: false,
            CanReveal: false,
            CanReidentify: false,
            CanCopyPath: false,
            IsTagSelected: true,
            TagTypeSummary: tagTypeSummary);
    }
    public static FileDetailsModel Create(
        IReadOnlyList<IndexedFile> selectedFiles,
        IReadOnlyList<UserTag> userTags,
        IReadOnlyList<AutomaticTag> automaticTags)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);
        ArgumentNullException.ThrowIfNull(userTags);
        ArgumentNullException.ThrowIfNull(automaticTags);

        if (selectedFiles.Count == 0)
        {
            return new FileDetailsModel(
                Title: "未选择文件",
                Name: null,
                Path: null,
                Extension: null,
                SizeText: null,
                ModifiedText: null,
                StatusText: "无",
                IdentityStateText: "无",
                UserTagsText: "无",
                AutomaticTagsText: "无",
                Diagnostic: null,
                IsSingleFileSelected: false,
                IsMultipleFilesSelected: false,
                SelectedCount: 0,
                CanOpen: false,
                CanReveal: false,
                CanReidentify: false,
                CanCopyPath: false);
        }

        if (selectedFiles.Count > 1)
        {
            return new FileDetailsModel(
                Title: $"已选择 {selectedFiles.Count} 个文件",
                Name: null,
                Path: null,
                Extension: null,
                SizeText: null,
                ModifiedText: null,
                StatusText: "多选",
                IdentityStateText: "多选",
                UserTagsText: "多选批量操作",
                AutomaticTagsText: "多选批量操作",
                Diagnostic: null,
                IsSingleFileSelected: false,
                IsMultipleFilesSelected: true,
                SelectedCount: selectedFiles.Count,
                CanOpen: false,
                CanReveal: false,
                CanReidentify: false,
                CanCopyPath: false);
        }

        var file = selectedFiles[0];
        var statusText = file.IsOnline ? "在线" : "离线";
        var isStable = string.Equals(file.IdentityKind, "stable", StringComparison.OrdinalIgnoreCase);
        var identityStateText = isStable
            ? "稳定身份已绑定"
            : "路径降级模式 (未绑定稳定 ID)";

        var userTagsText = userTags.Count == 0
            ? "无"
            : string.Join("、", userTags.Select(t => t.Name));

        var autoTagsText = automaticTags.Count == 0
            ? "无"
            : string.Join("、", automaticTags.Select(t => t.Name));

        return new FileDetailsModel(
            Title: file.Name,
            Name: file.Name,
            Path: file.Path,
            Extension: file.Extension,
            SizeText: $"{file.Size:N0} 字节",
            ModifiedText: file.Modified.LocalDateTime.ToString("g"),
            StatusText: statusText,
            IdentityStateText: identityStateText,
            UserTagsText: userTagsText,
            AutomaticTagsText: autoTagsText,
            Diagnostic: string.IsNullOrWhiteSpace(file.Diagnostic) ? null : file.Diagnostic,
            IsSingleFileSelected: true,
            IsMultipleFilesSelected: false,
            SelectedCount: 1,
            CanOpen: file.IsOnline,
            CanReveal: file.IsOnline,
            CanReidentify: file.IsOnline,
            CanCopyPath: true);
    }
}
