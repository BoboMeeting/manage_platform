using System.ComponentModel;
using System.Text;
using ManagerPlatform.Models;
using ManagerPlatform.Stores;
using ModelContextProtocol.Server;

namespace ManagerPlatform.Mcp;

/// <summary>
/// MCP 工具集合：为 AI Agent 提供查询会议系统数据的能力。
///
/// 约定：工具方法签名为首参数可附加 IServiceProvider（由 MCP 框架自动注入），
/// 其余参数为 Agent 可见的工具入参（通过 [Description] 标注供 LLM 理解含义）。
/// </summary>
[McpServerToolType]
public static class McpTools
{
    // ==================== 用户相关 ====================

    /// <summary>查询用户个人信息（真实数据，来自用户仓储）。</summary>
    [McpServerTool(Name = "get_user_info")]
    [Description("查询某人的个人信息，包括昵称、角色、账号状态等。请传入用户昵称或账号进行模糊匹配。")]
    public static async Task<string> GetUserInfoAsync(
        IServiceProvider services,
        [Description("要查询的人的昵称或账号")] string name)
    {
        using var scope = services.CreateScope();
        var userStore = scope.ServiceProvider.GetRequiredService<IUserStore>();

        var users = await userStore.FindAsync(name, null, null);
        if (users.Count == 0)
            return $"未找到与 \"{name}\" 相关的用户。";

        var sb = new StringBuilder();
        foreach (var u in users.Take(5))
        {
            sb.AppendLine($"昵称：{u.Nickname}");
            sb.AppendLine($"账号：{u.Account} ({u.AccountKind})");
            sb.AppendLine($"角色：{u.Role}");
            sb.AppendLine($"状态：{u.Status}");
            sb.AppendLine();
        }
        if (users.Count > 5)
            sb.AppendLine($"（共匹配 {users.Count} 条，仅展示前 5 条）");

        return sb.ToString();
    }

    // ==================== 会议相关 ====================

    /// <summary>查询指定日期范围内的会议列表（真实数据）。</summary>
    [McpServerTool(Name = "query_conferences")]
    [Description("查询指定日期范围内的所有会议（会议场次），返回时间、主题、状态、参会人数等信息。")]
    public static async Task<string> QueryConferencesAsync(
        IServiceProvider services,
        [Description("查询起始日期，例如 2026-10-01")] DateTime startDate,
        [Description("查询结束日期（含），例如 2026-10-31")] DateTime endDate)
    {
        using var scope = services.CreateScope();
        var roomStore = scope.ServiceProvider.GetRequiredService<IRoomStore>();
        var conferenceStore = scope.ServiceProvider.GetRequiredService<IConferenceStore>();
        var participantStore = scope.ServiceProvider.GetRequiredService<IParticipantStore>();

        // 先查房间，再查会议；按日期过滤
        var rooms = await roomStore.GetAllAsync();
        var conferences = new List<Conference>();
        foreach (var room in rooms)
        {
            var list = await conferenceStore.GetByRoomAsync(room.Id);
            conferences.AddRange(list);
        }

        var filtered = conferences
            .Where(c => c.StartedAt.Date >= startDate.Date && c.StartedAt.Date <= endDate.Date)
            .OrderByDescending(c => c.StartedAt)
            .ToList();

        if (filtered.Count == 0)
            return $"{startDate:yyyy-MM-dd} 至 {endDate:yyyy-MM-dd} 期间未找到任何会议记录。";

        var sb = new StringBuilder();
        sb.AppendLine($"共找到 {filtered.Count} 场会议：");
        sb.AppendLine();
        foreach (var c in filtered.Take(20))
        {
            var room = rooms.FirstOrDefault(r => r.Id == c.RoomId);
            var participants = await participantStore.GetByConferenceAsync(c.Id);
            sb.AppendLine($"会议主题：{room?.Title ?? "(未知房间)"}");
            sb.AppendLine($"时间：{c.StartedAt:yyyy-MM-dd HH:mm} ~ {(c.EndedAt?.ToString("HH:mm") ?? "进行中")}");
            sb.AppendLine($"状态：{c.Status}");
            sb.AppendLine($"参会人数：{participants.Count}");
            sb.AppendLine();
        }
        if (filtered.Count > 20)
            sb.AppendLine($"（仅展示前 20 条，共 {filtered.Count} 条）");

        return sb.ToString();
    }

    /// <summary>查询指定会议的参会者列表（真实数据）。</summary>
    [McpServerTool(Name = "query_conference_participants")]
    [Description("查询某场会议的参会者列表及其进出时间。请传入会议主题（模糊匹配）或会议日期。")]
    public static async Task<string> QueryConferenceParticipantsAsync(
        IServiceProvider services,
        [Description("会议主题关键词（可选，与日期二选一或组合）")] string? titleKeyword,
        [Description("会议日期，例如 2026-10-15（可选）")] DateTime? date)
    {
        using var scope = services.CreateScope();
        var roomStore = scope.ServiceProvider.GetRequiredService<IRoomStore>();
        var conferenceStore = scope.ServiceProvider.GetRequiredService<IConferenceStore>();
        var participantStore = scope.ServiceProvider.GetRequiredService<IParticipantStore>();

        var rooms = await roomStore.GetAllAsync();
        if (!string.IsNullOrWhiteSpace(titleKeyword))
        {
            rooms = rooms.Where(r => r.Title.Contains(titleKeyword, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var allConferences = new List<(Conference C, MeetingRoom R)>();
        foreach (var room in rooms)
        {
            var list = await conferenceStore.GetByRoomAsync(room.Id);
            allConferences.AddRange(list.Select(c => (c, room)));
        }

        if (date.HasValue)
        {
            allConferences = allConferences
                .Where(x => x.C.StartedAt.Date == date.Value.Date)
                .ToList();
        }

        if (allConferences.Count == 0)
            return "未找到匹配的会议。";

        var sb = new StringBuilder();
        foreach (var (conf, room) in allConferences.Take(5))
        {
            sb.AppendLine($"【{room.Title}】 {conf.StartedAt:yyyy-MM-dd HH:mm} ({conf.Status})");
            var participants = await participantStore.GetByConferenceAsync(conf.Id);
            if (participants.Count == 0)
            {
                sb.AppendLine("  （无参会者记录）");
            }
            else
            {
                foreach (var p in participants)
                {
                    var roleTag = p.IsAi ? "[AI]" : "";
                    sb.AppendLine($"  {roleTag} {p.Nickname}  进入={p.JoinTime:HH:mm}  离开={p.LeaveTime?.ToString("HH:mm") ?? "至今"}");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ==================== 对话查询（模拟数据，待接入真实转录）====================

    /// <summary>
    /// 对话查询工具：查询某人过去在某个时间、某个地点与某人的对话内容。
    /// 当前数据来源为模拟数据；后续接入会议转录/字幕服务后替换为真实查询。
    /// </summary>
    [McpServerTool(Name = "query_conversation")]
    [Description("查询某人过去在某个时间与另一个人的面对面对话内容。数据来源为历史会议记录。")]
    public static string QueryConversation(
        [Description("要查询的人的姓名")] string person,
        [Description("对话对象的姓名")] string otherPerson,
        [Description("对话发生的日期，例如 2026-09-15")] DateTime date,
        [Description("对话发生的地点，例如 3楼会议室")] string location)
    {
        var record = ConversationStore.Records.FirstOrDefault(r =>
            r.Date.Date == date.Date &&
            r.Participants.Any(p => p.Contains(person, StringComparison.OrdinalIgnoreCase)) &&
            r.Participants.Any(p => p.Contains(otherPerson, StringComparison.OrdinalIgnoreCase)) &&
            r.Location.Contains(location, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            return $"未找到 {date:yyyy-MM-dd} {person} 与 {otherPerson} 在 {location} 的对话记录。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"时间：{record.Date:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"地点：{record.Location}");
        sb.AppendLine($"参与人：{string.Join("、", record.Participants)}");
        sb.AppendLine("对话内容：");
        foreach (var message in record.Messages)
        {
            sb.AppendLine($"[{message.Time:HH:mm}] {message.Speaker}：{message.Content}");
        }

        return sb.ToString();
    }
}

// ==================== 模拟对话数据（待替换为真实转录源）====================

public sealed record ConversationMessage(DateTime Time, string Speaker, string Content);

public sealed record ConversationRecord(
    DateTime Date,
    string Location,
    IReadOnlyList<string> Participants,
    IReadOnlyList<ConversationMessage> Messages);

public static class ConversationStore
{
    public static readonly IReadOnlyList<ConversationRecord> Records =
    [
        new(
            new DateTime(2026, 9, 15, 10, 0, 0),
            "公司3楼会议室",
            ["张伟", "李娜"],
            [
                new(new DateTime(2026, 9, 15, 10, 0, 0), "张伟", "李娜，新版本的接口文档你这边整理得怎么样了？"),
                new(new DateTime(2026, 9, 15, 10, 1, 0), "李娜", "已经完成了大部分，还差支付模块，预计明天能给你。"),
                new(new DateTime(2026, 9, 15, 10, 3, 0), "张伟", "好的，那我们周五一起对一下联调进度。"),
            ]),
        new(
            new DateTime(2026, 9, 16, 14, 30, 0),
            "星巴克国贸店",
            ["王强", "陈静"],
            [
                new(new DateTime(2026, 9, 16, 14, 30, 0), "王强", "客户对这次的报价还有什么反馈吗？"),
                new(new DateTime(2026, 9, 16, 14, 32, 0), "陈静", "整体满意，就是希望交付时间能提前两周。"),
            ]),
    ];
}
