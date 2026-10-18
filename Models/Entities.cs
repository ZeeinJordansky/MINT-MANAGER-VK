using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MintVkBot.Models;

[Table("ChatMembers")]
public class ChatMember
{
    [Key]
    public int Id { get; set; }

    public long PeerId { get; set; }

    public long UserId { get; set; }

    public string? NickName { get; set; }

    public ChatRole Role { get; set; } = ChatRole.User;

    public long? AssignedByUserId { get; set; }

    public DateTime? AssignedAt { get; set; }

    public int WarningsCount { get; set; } = 0;

    public DateTime? MutedUntil { get; set; }

    public string? MuteReason { get; set; }

    public long? MutedByUserId { get; set; }

    public DateTime? BannedUntil { get; set; }

    public string? BanReason { get; set; }

    public long? BannedByUserId { get; set; }

    public int MessagesToday { get; set; } = 0;

    public int TotalMessages { get; set; } = 0;

    public DateTime? LastMessageDate { get; set; }

    public DateTime? LastActivityDate { get; set; }
}

[Table("ChatSettings")]
public class ChatSettings
{
    [Key]
    public long PeerId { get; set; }

    public int WarnLimit { get; set; } = 3;
}

[Table("ChatWarns")]
public class ChatWarn
{
    [Key]
    public int Id { get; set; }

    public long PeerId { get; set; }

    public long UserId { get; set; }

    public long IssuedByUserId { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTime IssuedAt { get; set; } = DateTime.UtcNow;

    public DateTime? ExpiresAt { get; set; }
}
