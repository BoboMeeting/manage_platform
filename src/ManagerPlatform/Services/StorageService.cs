namespace ManagerPlatform.Services;

/// <summary>
/// 媒体文件存储抽象。本期实现为本地磁盘；上线替换为对象存储实现（S3/OSS）时，
/// 仅 StorageKey 语义不变，业务代码零改动（入戏方案：内容层与工程层解耦）。
/// </summary>
public interface IStorageService
{
    /// <summary>写入内容并返回实际存储键（实现方可能追加归档路径）。</summary>
    Task<string> SaveAsync(string key, Stream content, CancellationToken ct = default);

    /// <summary>删除对象；对象不存在时静默返回。</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>打开只读流；对象不存在返回 null。</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>对象是否存在。</summary>
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// 对象物理路径。仅本地磁盘实现支持（用于 ffprobe 与 Range 流式播放）；
    /// 对象存储实现返回 null（届时预览改为签名 URL 直连 CDN）。
    /// </summary>
    string? GetPhysicalPath(string key);
}

/// <summary>本地磁盘实现：{StorageRoot}/{key}，key 中的 '/' 映射为目录分隔。</summary>
public sealed class LocalFileStorageService(Microsoft.Extensions.Options.IOptions<ManagerPlatform.Options.MediaOptions> options, ILogger<LocalFileStorageService> logger) : IStorageService
{
    private readonly string _root = ResolveRoot(options.Value.StorageRoot);
    private readonly ILogger<LocalFileStorageService> _logger = logger;

    private static string ResolveRoot(string configured)
    {
        var root = Path.IsPathRooted(configured)
            ? configured
            : Path.Combine(AppContext.BaseDirectory, configured);
        Directory.CreateDirectory(root);
        return root;
    }

    private string Physical(string key)
    {
        // key 约定由服务端生成（videos/{id}/...），防御性拒绝路径穿越
        if (key.Contains("..") || Path.IsPathRooted(key))
            throw new ArgumentException($"非法存储键：{key}", nameof(key));
        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task<string> SaveAsync(string key, Stream content, CancellationToken ct = default)
    {
        var path = Physical(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await content.CopyToAsync(fs, ct);
        _logger.LogInformation("存储写入：{Key}（{Bytes} 字节）", key, fs.Length);
        return key;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = Physical(key);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("存储删除：{Key}", key);
        }
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = Physical(key);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(Physical(key)));

    public string? GetPhysicalPath(string key)
    {
        var path = Physical(key);
        return File.Exists(path) ? path : null;
    }
}
