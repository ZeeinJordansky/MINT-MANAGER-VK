namespace MintVkBot.Models;

public enum ChatRole
{
    User = 0,               // Пользователь Mint
    Moderator = 1,          // Модератор
    SeniorModerator = 2,    // Старший модератор
    Administrator = 3,      // Администратор
    SeniorAdministrator = 4,// Старший администратор
    ChiefAdministrator = 5, // Главный администратор
    ChatLeader = 6,         // Руководитель беседы
    ChatOwner = 7           // Владелец беседы
}

public static class ChatRoleExtensions
{
    public static string GetRoleName(this ChatRole role) => role switch
    {
        ChatRole.ChatOwner => "Владелец беседы",
        ChatRole.ChatLeader => "Руководитель беседы",
        ChatRole.ChiefAdministrator => "Главный администратор",
        ChatRole.SeniorAdministrator => "Старший администратор",
        ChatRole.Administrator => "Администратор",
        ChatRole.SeniorModerator => "Старший модератор",
        ChatRole.Moderator => "Модератор",
        _ => "Пользователь Mint"
    };

    public static int GetPermissionLevel(this ChatRole role) => (int)role;
}
