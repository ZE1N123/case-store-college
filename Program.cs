using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<StoreDataService>();
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "case-store-session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
var store = app.Services.GetRequiredService<StoreDataService>();
await store.InitializeAsync();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/store", async () =>
{
    var data = await store.GetPublicDataAsync();
    return Results.Ok(data);
});

app.MapPost("/api/auth/register", async (RegisterRequest request, HttpContext context) =>
{
    if (string.IsNullOrWhiteSpace(request.Username) ||
        request.Username.Trim().Length is < 3 or > 30 ||
        string.IsNullOrWhiteSpace(request.Password) ||
        request.Password.Length < 8 ||
        request.Password.Length > 128)
    {
        return Results.BadRequest(new { message = "Укажите имя от 3 до 30 символов и пароль от 8 символов." });
    }

    var user = await store.RegisterAsync(request.Username.Trim(), request.Email?.Trim() ?? "", request.Password);
    if (user is null)
    {
        return Results.Conflict(new { message = "Пользователь с таким именем уже существует." });
    }

    await SignInAsync(context, user);
    return Results.Ok(new { username = user.Username, isAdmin = user.IsAdmin });
});

app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext context) =>
{
    var user = await store.AuthenticateAsync(request.Username?.Trim() ?? "", request.Password ?? "");
    if (user is null)
    {
        return Results.Json(new { message = "Неверное имя пользователя или пароль." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    await SignInAsync(context, user);
    return Results.Ok(new { username = user.Username, isAdmin = user.IsAdmin });
});

app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
});

app.MapGet("/api/account", [Authorize] async (ClaimsPrincipal principal) =>
{
    var username = principal.Identity?.Name ?? "";
    return Results.Ok(await store.GetAccountAsync(username));
});

app.MapPost("/api/orders", [Authorize] async (OrderRequest request, ClaimsPrincipal principal) =>
{
    if (request.Items is null || request.Items.Count is < 1 or > 20 ||
        request.Items.Any(item => item.Quantity is < 1 or > 20))
    {
        return Results.BadRequest(new { message = "Проверьте состав заказа." });
    }

    var order = await store.CreateOrderAsync(principal.Identity?.Name ?? "", request.Items);
    return order is null
        ? Results.BadRequest(new { message = "Один из кейсов больше недоступен." })
        : Results.Ok(order);
});

var admin = app.MapGroup("/api/admin").RequireAuthorization(policy =>
    policy.RequireClaim(ClaimTypes.Role, "Admin"));

admin.MapPost("/upload", async (HttpRequest request, IWebHostEnvironment environment) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Нужен файл изображения или видео." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0 || file.Length > 20 * 1024 * 1024)
    {
        return Results.BadRequest(new { message = "Размер файла должен быть от 1 байта до 20 МБ." });
    }

    var extension = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".mp4", ".webm" };
    if (!allowedExtensions.Contains(extension))
    {
        return Results.BadRequest(new { message = "Поддерживаются изображения JPG, PNG, WebP, GIF и видео MP4, WebM." });
    }

    var uploadDirectory = Path.Combine(environment.WebRootPath ?? "wwwroot", "uploads");
    Directory.CreateDirectory(uploadDirectory);
    var fileName = $"{Guid.NewGuid():N}{extension}";
    var destination = Path.Combine(uploadDirectory, fileName);
    await using (var output = File.Create(destination))
    {
        await file.CopyToAsync(output);
    }

    return Results.Ok(new { url = $"/uploads/{fileName}" });
});

admin.MapPost("/cases", async (CaseRequest request) =>
{
    var result = await store.AddCaseAsync(request);
    return result is null
        ? Results.BadRequest(new { message = "Укажите название, описание и цену от 0 до 1 000 000." })
        : Results.Ok(result);
});
admin.MapPut("/cases/{id:guid}", async (Guid id, CaseRequest request) =>
{
    var result = await store.UpdateCaseAsync(id, request);
    return result is null
        ? Results.BadRequest(new { message = "Кейс не найден или данные некорректны." })
        : Results.Ok(result);
});
admin.MapDelete("/cases/{id:guid}", async (Guid id) =>
    await store.DeleteCaseAsync(id) ? Results.NoContent() : Results.NotFound());

admin.MapPut("/settings", async (SettingsRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.StoreName) || request.StoreName.Trim().Length > 50 ||
        !string.IsNullOrWhiteSpace(request.WhatsAppUrl) &&
        (!Uri.TryCreate(request.WhatsAppUrl, UriKind.Absolute, out var uri) ||
         uri.Scheme is not ("https" or "http")))
    {
        return Results.BadRequest(new { message = "Проверьте название магазина и ссылку WhatsApp." });
    }

    return Results.Ok(await store.UpdateSettingsAsync(request));
});

admin.MapPost("/posts", async (PostRequest request) =>
{
    var result = await store.AddPostAsync(request);
    return result is null
        ? Results.BadRequest(new { message = "Укажите заголовок и текст публикации." })
        : Results.Ok(result);
});
admin.MapPut("/posts/{id:guid}", async (Guid id, PostRequest request) =>
{
    var result = await store.UpdatePostAsync(id, request);
    return result is null
        ? Results.BadRequest(new { message = "Публикация не найдена или данные некорректны." })
        : Results.Ok(result);
});
admin.MapDelete("/posts/{id:guid}", async (Guid id) =>
    await store.DeletePostAsync(id) ? Results.NoContent() : Results.NotFound());

app.Run();

static async Task SignInAsync(HttpContext context, StoreUser user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username)
    };
    if (user.IsAdmin)
    {
        claims.Add(new Claim(ClaimTypes.Role, "Admin"));
    }

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));
}

sealed class StoreDataService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoreData _data = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StoreDataService(IWebHostEnvironment environment)
    {
        _filePath = Path.Combine(environment.ContentRootPath, "App_Data", "store.json");
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            if (File.Exists(_filePath))
            {
                await using var input = File.OpenRead(_filePath);
                _data = await JsonSerializer.DeserializeAsync<StoreData>(input, JsonOptions) ?? new StoreData();
            }

            var adminName = Environment.GetEnvironmentVariable("CASESTORE_ADMIN_USER") ?? "admin";
            var adminPassword = Environment.GetEnvironmentVariable("CASESTORE_ADMIN_PASSWORD") ?? "Admin123!";
            if (!_data.Users.Any(user => user.IsAdmin))
            {
                _data.Users.Add(new StoreUser
                {
                    Username = adminName,
                    Email = "",
                    PasswordHash = HashPassword(adminPassword),
                    IsAdmin = true
                });
                await SaveAsync();
            }

            if (_data.Cases.Count == 0)
            {
                _data.Cases.AddRange(new[]
                {
                    new StoreCase { Name = "Первый дроп", Description = "Яркий старт для охоты за редким скином.", Price = 490, ImageUrl = "https://images.unsplash.com/photo-1612287230202-1ff1d85d1bdf?auto=format&fit=crop&w=900&q=85", Tag = "ПОПУЛЯРНОЕ", Accent = "#f7a543" },
                    new StoreCase { Name = "Неоновый рейд", Description = "Неоновые оттенки и эффектные находки.", Price = 790, ImageUrl = "https://images.unsplash.com/photo-1603481546238-487240415921?auto=format&fit=crop&w=900&q=85", Tag = "НОВИНКА", Accent = "#9d75ff" },
                    new StoreCase { Name = "Тайник агента", Description = "Тактический набор для ценителей деталей.", Price = 1290, ImageUrl = "https://images.unsplash.com/photo-1593305841991-05c297ba4575?auto=format&fit=crop&w=900&q=85", Tag = "РЕДКИЙ", Accent = "#5ac7a5" }
                });
                await SaveAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PublicStoreData> GetPublicDataAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return new PublicStoreData(_data.Cases.ToList(), _data.Settings, _data.Posts.OrderByDescending(post => post.CreatedAt).ToList());
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreUser?> RegisterAsync(string username, string email, string password)
    {
        await _gate.WaitAsync();
        try
        {
            if (_data.Users.Any(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var user = new StoreUser { Username = username, Email = email, PasswordHash = HashPassword(password) };
            _data.Users.Add(user);
            await SaveAsync();
            return user;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreUser?> AuthenticateAsync(string username, string password)
    {
        await _gate.WaitAsync();
        try
        {
            var user = _data.Users.FirstOrDefault(candidate =>
                string.Equals(candidate.Username, username, StringComparison.OrdinalIgnoreCase));
            return user is not null && VerifyPassword(password, user.PasswordHash) ? user : null;
        }
        finally { _gate.Release(); }
    }

    public async Task<AccountData> GetAccountAsync(string username)
    {
        await _gate.WaitAsync();
        try
        {
            var user = _data.Users.First(candidate => candidate.Username == username);
            return new AccountData(user.Username, user.Email, user.IsAdmin, user.Orders.OrderByDescending(order => order.CreatedAt).ToList());
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreOrder?> CreateOrderAsync(string username, List<OrderItemRequest> items)
    {
        await _gate.WaitAsync();
        try
        {
            var selections = items.GroupBy(item => item.CaseId).Select(group => new OrderItemRequest(group.Key, group.Sum(item => item.Quantity))).ToList();
            var orderItems = new List<OrderItem>();
            foreach (var selection in selections)
            {
                var product = _data.Cases.FirstOrDefault(item => item.Id == selection.CaseId);
                if (product is null || selection.Quantity is < 1 or > 20)
                {
                    return null;
                }

                orderItems.Add(new OrderItem(product.Name, selection.Quantity, product.Price));
            }

            var order = new StoreOrder { Items = orderItems, Total = orderItems.Sum(item => item.Price * item.Quantity) };
            var user = _data.Users.First(candidate => candidate.Username == username);
            user.Orders.Add(order);
            await SaveAsync();
            return order;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreCase?> AddCaseAsync(CaseRequest request)
    {
        if (!ValidCase(request)) return null;
        await _gate.WaitAsync();
        try
        {
            var item = new StoreCase();
            ApplyCase(item, request);
            _data.Cases.Add(item);
            await SaveAsync();
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreCase?> UpdateCaseAsync(Guid id, CaseRequest request)
    {
        if (!ValidCase(request)) return null;
        await _gate.WaitAsync();
        try
        {
            var item = _data.Cases.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null) return null;
            ApplyCase(item, request);
            await SaveAsync();
            return item;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteCaseAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var item = _data.Cases.FirstOrDefault(candidate => candidate.Id == id);
            if (item is null) return false;
            _data.Cases.Remove(item);
            await SaveAsync();
            return true;
        }
        finally { _gate.Release(); }
    }

    public async Task<StoreSettings> UpdateSettingsAsync(SettingsRequest request)
    {
        await _gate.WaitAsync();
        try
        {
            _data.Settings = new StoreSettings
            {
                StoreName = request.StoreName.Trim(),
                WhatsAppUrl = request.WhatsAppUrl?.Trim() ?? "",
                WhatsAppLabel = request.WhatsAppLabel?.Trim() ?? "Написать нам"
            };
            await SaveAsync();
            return _data.Settings;
        }
        finally { _gate.Release(); }
    }

    public async Task<BlogPost?> AddPostAsync(PostRequest request)
    {
        if (!ValidPost(request)) return null;
        await _gate.WaitAsync();
        try
        {
            var post = new BlogPost();
            ApplyPost(post, request);
            _data.Posts.Add(post);
            await SaveAsync();
            return post;
        }
        finally { _gate.Release(); }
    }

    public async Task<BlogPost?> UpdatePostAsync(Guid id, PostRequest request)
    {
        if (!ValidPost(request)) return null;
        await _gate.WaitAsync();
        try
        {
            var post = _data.Posts.FirstOrDefault(candidate => candidate.Id == id);
            if (post is null) return null;
            ApplyPost(post, request);
            await SaveAsync();
            return post;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeletePostAsync(Guid id)
    {
        await _gate.WaitAsync();
        try
        {
            var post = _data.Posts.FirstOrDefault(candidate => candidate.Id == id);
            if (post is null) return false;
            _data.Posts.Remove(post);
            await SaveAsync();
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync()
    {
        var temporaryPath = _filePath + ".tmp";
        await using (var output = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(output, _data, JsonOptions);
        }

        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private static bool ValidCase(CaseRequest request) =>
        !string.IsNullOrWhiteSpace(request.Name) && request.Name.Trim().Length <= 60 &&
        !string.IsNullOrWhiteSpace(request.Description) && request.Description.Trim().Length <= 400 &&
        request.Price is >= 0 and <= 1_000_000 &&
        (string.IsNullOrWhiteSpace(request.ImageUrl) ||
         Uri.TryCreate(request.ImageUrl, UriKind.Relative, out _) ||
         Uri.TryCreate(request.ImageUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http");

    private static void ApplyCase(StoreCase item, CaseRequest request)
    {
        item.Name = request.Name.Trim();
        item.Description = request.Description.Trim();
        item.Price = request.Price;
        item.ImageUrl = request.ImageUrl?.Trim() ?? "";
        item.Tag = request.Tag?.Trim() ?? "";
        item.Accent = request.Accent?.Trim() ?? "#f7a543";
    }

    private static bool ValidPost(PostRequest request) =>
        !string.IsNullOrWhiteSpace(request.Title) && request.Title.Trim().Length <= 100 &&
        !string.IsNullOrWhiteSpace(request.Content) && request.Content.Trim().Length <= 5000 &&
        (string.IsNullOrWhiteSpace(request.MediaUrl) ||
         Uri.TryCreate(request.MediaUrl, UriKind.Relative, out _) ||
         Uri.TryCreate(request.MediaUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http");

    private static void ApplyPost(BlogPost post, PostRequest request)
    {
        post.Title = request.Title.Trim();
        post.Content = request.Content.Trim();
        post.MediaUrl = request.MediaUrl?.Trim() ?? "";
        post.MediaType = request.MediaType == "video" ? "video" : "image";
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string encoded)
    {
        var parts = encoded.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

sealed class StoreData
{
    public List<StoreUser> Users { get; set; } = [];
    public List<StoreCase> Cases { get; set; } = [];
    public List<BlogPost> Posts { get; set; } = [];
    public StoreSettings Settings { get; set; } = new();
}

sealed class StoreUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public bool IsAdmin { get; set; }
    public List<StoreOrder> Orders { get; set; } = [];
}

sealed class StoreCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Accent { get; set; } = "#f7a543";
}

sealed class StoreSettings
{
    public string StoreName { get; set; } = "CASE ROOM";
    public string WhatsAppUrl { get; set; } = "";
    public string WhatsAppLabel { get; set; } = "Написать нам";
}

sealed class BlogPost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Content { get; set; } = "";
    public string MediaUrl { get; set; } = "";
    public string MediaType { get; set; } = "image";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

sealed class StoreOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<OrderItem> Items { get; set; } = [];
    public decimal Total { get; set; }
}

sealed record OrderItem(string Name, int Quantity, decimal Price);
sealed record OrderItemRequest(Guid CaseId, int Quantity);
sealed record OrderRequest(List<OrderItemRequest>? Items);
sealed record RegisterRequest(string? Username, string? Email, string? Password);
sealed record LoginRequest(string? Username, string? Password);
sealed record CaseRequest(string Name, string Description, decimal Price, string? ImageUrl, string? Tag, string? Accent);
sealed record PostRequest(string Title, string Content, string? MediaUrl, string? MediaType);
sealed record SettingsRequest(string StoreName, string? WhatsAppUrl, string? WhatsAppLabel);
sealed record PublicStoreData(List<StoreCase> Cases, StoreSettings Settings, List<BlogPost> Posts);
sealed record AccountData(string Username, string Email, bool IsAdmin, List<StoreOrder> Orders);
