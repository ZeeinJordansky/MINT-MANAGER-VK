using Microsoft.EntityFrameworkCore;
using MintVkBot.Data;
using MintVkBot.Services;
using VkNet;
using VkNet.Abstractions;
using VkNet.Model;

var builder = WebApplication.CreateBuilder(args);

// Настройка Kestrel для постоянного соединения (Keep-Alive) с Cloudflare
builder.WebHost.ConfigureKestrel(options =>
{
    // Cloudflare держит соединения до 100 секунд, устанавливаем тайм-аут Keep-Alive на сервере выше
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});

// Настройка сервисов
builder.Services.AddControllers();
builder.Services.AddHttpClient<VkUserService>();

// База данных SQLite (в папке приложения) или PostgreSQL/Supabase
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? "Data Source=mint_bot.db";

builder.Services.AddDbContext<BotDbContext>(options =>
{
    options.UseSqlite(connectionString);
});

// Регистрация VkNet Api
builder.Services.AddSingleton<IVkApi>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var token = Environment.GetEnvironmentVariable("VK_ACCESS_TOKEN") 
                ?? config["VK_ACCESS_TOKEN"] 
                ?? config["VkConfig:AccessToken"]
                ?? "vk1.a.ChdOUA-5nsExGGSiBzaKdFc-jNsQYuYnmoRkzgtfn7K5wB0QB175HBfyw5H2JZ7Cm7ZI_NxSXdsPZGsclZ8SpcyaCNGc90bnjwtjPR2FStGjd4joB-3K0Rq6NtLJSuzchROf3lUuN4wnKDYYG5wSx72Zc22AhQk1fani_3SkB40UJXpNTsmMEZCtMX84Cks6Vgb9BKDaUhZo1BcIz4VbRA";

    var api = new VkApi();
    api.Authorize(new ApiAuthParams
    {
        AccessToken = token
    });
    return api;
});

builder.Services.AddSingleton<CommandHandler>();

var app = builder.Build();

// Автоматическое применение миграций или создание БД при старте
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
    db.Database.EnsureCreated();
}

app.UseRouting();
app.MapControllers();

app.Run();
