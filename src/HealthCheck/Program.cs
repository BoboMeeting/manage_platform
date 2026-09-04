// 容器健康检查小工具：对目标 URL 发起 HTTP GET，2xx 视为健康（exit 0），否则 exit 1。
// 用法：dotnet HealthCheck.dll <url>
// 由 docker-compose 健康检查在容器内调用，替代不存在的 curl。

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: HealthCheck <url>");
    return 1;
}

// 3 秒超时，需小于 compose 健康检查的 timeout: 5s
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

try
{
    using var response = await http.GetAsync(args[0]);
    if (response.IsSuccessStatusCode)
    {
        Console.WriteLine($"healthy: {(int)response.StatusCode} {args[0]}");
        return 0;
    }

    Console.Error.WriteLine($"unhealthy: {(int)response.StatusCode} {args[0]}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"unhealthy: {ex.Message}");
    return 1;
}
