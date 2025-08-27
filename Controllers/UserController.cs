﻿using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;

namespace JaeZoo.Server.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly ILogger<UsersController> _log;
        private readonly IWebHostEnvironment _env;

        public UsersController(AppDbContext db, ILogger<UsersController> log, IWebHostEnvironment env)
        {
            _db = db;
            _log = log;
            _env = env;
        }

        private Guid MeId
        {
            get
            {
                var id = User.FindFirstValue(ClaimTypes.NameIdentifier);
                if (string.IsNullOrWhiteSpace(id))
                    throw new InvalidOperationException("No NameIdentifier claim.");
                return Guid.Parse(id);
            }
        }

        // ===== Поиск =====
        [HttpGet("search")]
        public async Task<ActionResult<IEnumerable<UserSearchDto>>> Search([FromQuery] string q, CancellationToken ct)
        {
            var meId = MeId;
            var qLower = (q ?? "").Trim().ToLowerInvariant();

            var prov = _db.Database.ProviderName ?? string.Empty;
            IQueryable<User> baseQuery = _db.Users.Where(u => u.Id != meId);

            if (prov.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            {
                baseQuery = baseQuery.Where(u =>
                    EF.Functions.ILike(u.UserName!, $"%{qLower}%") ||
                    EF.Functions.ILike(u.Email!, $"%{qLower}%"));
            }
            else
            {
                baseQuery = baseQuery.Where(u =>
                    (u.UserName ?? "").ToLower().Contains(qLower) ||
                    (u.Email ?? "").ToLower().Contains(qLower));
            }

            var res = await baseQuery
                .AsNoTracking()
                .OrderBy(u => u.UserName)
                .Select(u => new UserSearchDto(u.Id, u.UserName, u.Email))
                .Take(25)
                .ToListAsync(ct);

            return Ok(res);
        }

        // ===== Мой профиль =====
        [HttpGet("me")]
        public async Task<ActionResult<UserProfileDto>> Me(CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            return Ok(ToProfileDto(me));
        }

        // ===== Публичный профиль =====
        [HttpGet("{id:guid}")]
        [AllowAnonymous]
        public async Task<ActionResult<PublicUserDto>> GetPublic(Guid id, CancellationToken ct)
        {
            var u = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (u == null) return NotFound();

            return Ok(ToPublicDto(u));
        }

        // ===== Обновить профиль =====
        [HttpPut("profile")]
        public async Task<ActionResult<UserProfileDto>> UpdateProfile([FromBody] UpdateProfileRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);

            if (body.DisplayName != null)
            {
                me.DisplayName = body.DisplayName.Trim();
                if (me.DisplayName.Length == 0) me.DisplayName = null;
            }
            if (body.About != null)
            {
                me.About = body.About.Trim();
                if (me.About.Length == 0) me.About = null;
            }

            await _db.SaveChangesAsync(ct);
            return Ok(ToProfileDto(me));
        }

        // ===== Статус =====
        [HttpPut("status")]
        public async Task<ActionResult<UserProfileDto>> UpdateStatus([FromBody] UpdateStatusRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);

            me.Status = body.Status;
            me.CustomStatus = string.IsNullOrWhiteSpace(body.CustomStatus) ? null : body.CustomStatus.Trim();

            await _db.SaveChangesAsync(ct);
            return Ok(ToProfileDto(me));
        }

        // ===== Установить URL аватара вручную =====
        [HttpPut("avatar/url")]
        public async Task<ActionResult<UserProfileDto>> SetAvatarUrl([FromBody] SetAvatarUrlRequest body, CancellationToken ct)
        {
            var me = await _db.Users.FirstAsync(u => u.Id == MeId, ct);
            me.AvatarUrl = string.IsNullOrWhiteSpace(body.AvatarUrl) ? null : body.AvatarUrl.Trim();
            await _db.SaveChangesAsync(ct);
            return Ok(ToProfileDto(me));
        }

        // ===== Загрузка аватара =====
        [Authorize]
        [RequestSizeLimit(5 * 1024 * 1024)] // 5MB
        [HttpPost("avatar/upload")]
        public async Task<IActionResult> UploadAvatar(IFormFile file, CancellationToken ct)
        {
            if (file == null || file.Length == 0)
                return BadRequest("Файл не найден.");

            var allowed = new[] { "image/png", "image/jpeg", "image/webp" };
            if (!allowed.Contains(file.ContentType ?? "", StringComparer.OrdinalIgnoreCase))
                return BadRequest("Поддерживаются только PNG/JPEG/WEBP.");

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".webp")
                return BadRequest("Неверное расширение файла.");

            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();

            if (bytes.Length == 0)
                return BadRequest("Пустой файл.");

            var uid = MeId;
            var entity = new Avatar
            {
                UserId = uid,
                Data = bytes,
                ContentType = file.ContentType ?? "image/png",
                CreatedAt = DateTime.UtcNow
            };

            _db.Avatars.Add(entity);
            await _db.SaveChangesAsync(ct);

            var version = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var url = $"/avatars/{uid}?v={version}";

            var me = await _db.Users.FirstAsync(u => u.Id == uid, ct);
            me.AvatarUrl = url;
            await _db.SaveChangesAsync(ct);

            return Ok(new { url });
        }

        // ===== Выдача аватара =====
        [AllowAnonymous]
        [HttpGet("/avatars/{id:guid}")]
        public async Task<IActionResult> GetAvatar(Guid id, CancellationToken ct)
        {
            var avatar = await _db.Avatars.AsNoTracking()
                .Where(a => a.UserId == id)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct);

            if (avatar is null || avatar.Data.Length == 0)
            {
                // если нет аватара → отдать заглушку
                var path = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "avatars", "default.png");
                if (System.IO.File.Exists(path))
                    return PhysicalFile(path, "image/png");
                return NotFound();
            }

            var etag = $"W/\"{avatar.Data.Length}-{avatar.CreatedAt.ToUniversalTime():yyyyMMddHHmmss}\"";
            var ifNone = Request.Headers["If-None-Match"].ToString();
            if (!string.IsNullOrEmpty(ifNone) && string.Equals(ifNone, etag, StringComparison.Ordinal))
                return StatusCode(StatusCodes.Status304NotModified);

            Response.Headers.ETag = etag;
            Response.Headers.CacheControl = "public,max-age=3600";

            return File(avatar.Data, avatar.ContentType ?? "image/png");
        }

        // ===== helpers =====
        private static UserProfileDto ToProfileDto(User u) =>
            new UserProfileDto(
                u.Id, u.UserName, u.Email,
                u.DisplayName,
                string.IsNullOrWhiteSpace(u.AvatarUrl) ? $"/avatars/{u.Id}" : u.AvatarUrl,
                u.About,
                u.Status, u.CustomStatus,
                u.CreatedAt, u.LastSeen
            );

        private static PublicUserDto ToPublicDto(User u) =>
            new PublicUserDto(
                u.Id, u.UserName, u.DisplayName,
                string.IsNullOrWhiteSpace(u.AvatarUrl) ? $"/avatars/{u.Id}" : u.AvatarUrl,
                u.Status, u.CustomStatus, u.LastSeen
            );
    }
}
