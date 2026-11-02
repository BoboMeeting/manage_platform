namespace ManagerPlatform.Services;

/// <summary>
/// 知识库原文件存储约定：key = kb/{kbId}/{docId}/{fileName}。
/// 上传落盘与删除级联清文件统一走这里，避免各端点各写一份路径拼接。
/// </summary>
public static class KnowledgeStorage
{
    public static string KeyPrefix(string knowledgeBaseId, string documentId) =>
        $"kb/{knowledgeBaseId}/{documentId}";

    public static string BuildKey(string knowledgeBaseId, string documentId, string fileName) =>
        $"{KeyPrefix(knowledgeBaseId, documentId)}/{fileName}";

    /// <summary>删除文档整目录（原文件；目录不存在时静默返回）。</summary>
    public static Task DeleteDocumentAsync(
        IStorageService storage, string knowledgeBaseId, string documentId, CancellationToken ct = default) =>
        storage.DeleteDirectoryAsync(KeyPrefix(knowledgeBaseId, documentId), ct);

    /// <summary>删除知识库整目录（全部文档原文件；删除知识库时级联清理）。</summary>
    public static Task DeleteKnowledgeBaseAsync(
        IStorageService storage, string knowledgeBaseId, CancellationToken ct = default) =>
        storage.DeleteDirectoryAsync($"kb/{knowledgeBaseId}", ct);
}
