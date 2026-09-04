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
    string? TagTypeSummary = null,
    bool IsRootOffline = false,
    string? RootOfflineNotice = null);

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
        IReadOnlyList<AutomaticTag> automaticTags,
        bool isRootOffline = false)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);
        ArgumentNullException.ThrowIfNull(userTags);
        ArgumentNullException.ThrowIfNull(automaticTags);

        var rootNotice = isRootOffline ? "管理根目录当前离线，文件与标签已妥善保留，等待介质重新连接" : null;

        if (selectedFiles.Count == 0)
        {
            return new FileDetailsModel(
                Title: "未选择文件",
                Name: null,
                Path: null,
                Extension: null,
                SizeText: null,
                ModifiedText: null,
                StatusText: isRootOffline ? "离线" : "无",
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
                CanCopyPath: false,
                IsRootOffline: isRootOffline,
                RootOfflineNotice: rootNotice);
        }

        if (selectedFiles.Count > 1)
        {
            var multiUserTagsText = userTags.Count == 0
                ? "无"
                : string.Join("、", userTags.Select(t => t.Name));
            var multiAutoTagsText = automaticTags.Count == 0
                ? "无"
                : string.Join("、", automaticTags.Select(t => t.Name));

            return new FileDetailsModel(
                Title: $"已选择 {selectedFiles.Count} 个文件",
                Name: null,
                Path: null,
                Extension: null,
                SizeText: null,
                ModifiedText: null,
                StatusText: isRootOffline ? "多选 (离线)" : "多选",
                IdentityStateText: "多选",
                UserTagsText: multiUserTagsText,
                AutomaticTagsText: multiAutoTagsText,
                Diagnostic: null,
                IsSingleFileSelected: false,
                IsMultipleFilesSelected: true,
                SelectedCount: selectedFiles.Count,
                CanOpen: false,
                CanReveal: false,
                CanReidentify: false,
                CanCopyPath: false,
                IsRootOffline: isRootOffline,
                RootOfflineNotice: rootNotice);
        }

        var file = selectedFiles[0];
        var statusText = isRootOffline ? "离线" : (file.IsOnline ? "在线" : "离线");
        var isStable = string.Equals(file.IdentityKind, "stable", StringComparison.OrdinalIgnoreCase);
        var identityStateText = isStable
            ? "稳定身份已绑定"
            : "⚠️ 身份跟踪有限：当前介质不支持底层稳定文件 ID。同路径原地修改可保留标签；跨目录移动或重命名时可能需要重新关联标签。";

        var userTagsText = userTags.Count == 0
            ? "无"
            : string.Join("、", userTags.Select(t => t.Name));

        var autoTagsText = automaticTags.Count == 0
            ? "无"
            : string.Join("、", automaticTags.Select(t => t.Name));

        var canPerformOnlineAction = file.IsOnline && !isRootOffline;

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
            CanOpen: canPerformOnlineAction,
            CanReveal: canPerformOnlineAction,
            CanReidentify: canPerformOnlineAction,
            CanCopyPath: true,
            IsRootOffline: isRootOffline,
            RootOfflineNotice: rootNotice);
    }
}
