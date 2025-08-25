using System.Security.Claims;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FriendsController : ControllerBase
{
    private readonly AppDbContext _db;
    public FriendsController(AppDbContext db) => _db = db;

    // --------------------------------------------------
    // Helpers
    // --------------------------------------------------
    private Guid MeId
    {
        get
        {
            var id = User.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? User.FindFirstValue("sub");
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("No user id in claims.");
            return Guid.Parse(id);
        }
    }

    // --------------------------------------------------
    // Список принятых друзей (как раньше)
    // GET /api/friends/list
    // --------------------------------------------------
    [HttpGet("list")]
    public async Task<ActionResult<IEnumerable<FriendDto>>> List()
    {
        var me = MeId;

        var friendIds = await _db.Friendships
            .Where(f => f.Status == FriendshipStatus.Accepted &&
                        (f.RequesterId == me || f.AddresseeId == me))
            .Select(f => f.RequesterId == me ? f.AddresseeId : f.RequesterId)
            .Distinct()
            .ToListAsync();

        var friends = await _db.Users
            .Where(u => friendIds.Contains(u.Id))
            .OrderBy(u => u.UserName)
            .Select(u => new FriendDto(u.Id, u.UserName, u.Email))
            .ToListAsync();

        return Ok(friends);
    }

    // --------------------------------------------------
    // Отправить заявку (idempotent). Встречная — автопринятие (как было)
    // POST /api/friends/request/{userId}
    // --------------------------------------------------
    [HttpPost("request/{userId:guid}")]
    public async Task<IActionResult> SendRequest(Guid userId)
    {
        var me = MeId;
        if (me == userId) return BadRequest(new { error = "Cannot befriend yourself." });

        var userExists = await _db.Users.AnyAsync(u => u.Id == userId);
        if (!userExists) return NotFound(new { error = "User not found." });

        var existing = await _db.Friendships
            .Where(f => (f.RequesterId == me && f.AddresseeId == userId) ||
                        (f.RequesterId == userId && f.AddresseeId == me))
            .OrderBy(f => f.CreatedAt)
            .FirstOrDefaultAsync();

        if (existing is null)
        {
            _db.Friendships.Add(new Friendship
            {
                Id = Guid.NewGuid(),
                RequesterId = me,
                AddresseeId = userId,
                Status = FriendshipStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            return Ok(new { created = true, accepted = false });
        }

        // встречная ожидала — принимаем
        if (existing.Status == FriendshipStatus.Pending &&
            existing.RequesterId == userId && existing.AddresseeId == me)
        {
            existing.Status = FriendshipStatus.Accepted;
            await _db.SaveChangesAsync();
            return Ok(new { created = false, accepted = true });
        }

        // уже друзья или уже моя pending
        return Ok(new
        {
            created = false,
            accepted = existing.Status == FriendshipStatus.Accepted,
            pending = existing.Status == FriendshipStatus.Pending && existing.RequesterId == me
        });
    }

    // --------------------------------------------------
    // Входящие заявки (я адресат)
    // GET /api/friends/requests/incoming
    // --------------------------------------------------
    [HttpGet("requests/incoming")]
    public async Task<ActionResult<IEnumerable<FriendRequestDto>>> IncomingRequests()
    {
        var me = MeId;

        var list = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending && f.AddresseeId == me)
            .OrderByDescending(f => f.CreatedAt)
            .Join(_db.Users.AsNoTracking(),
                  f => f.RequesterId,
                  u => u.Id,
                  (f, u) => new FriendRequestDto(
                      f.Id,
                      u.Id,
                      u.UserName,
                      u.Email,
                      f.CreatedAt,
                      "incoming"))
            .ToListAsync();

        return Ok(list);
    }

    // --------------------------------------------------
    // Исходящие заявки (я отправитель)
    // GET /api/friends/requests/outgoing
    // --------------------------------------------------
    [HttpGet("requests/outgoing")]
    public async Task<ActionResult<IEnumerable<FriendRequestDto>>> OutgoingRequests()
    {
        var me = MeId;

        var list = await _db.Friendships
            .AsNoTracking()
            .Where(f => f.Status == FriendshipStatus.Pending && f.RequesterId == me)
            .OrderByDescending(f => f.CreatedAt)
            .Join(_db.Users.AsNoTracking(),
                  f => f.AddresseeId,
                  u => u.Id,
                  (f, u) => new FriendRequestDto(
                      f.Id,
                      u.Id,
                      u.UserName,
                      u.Email,
                      f.CreatedAt,
                      "outgoing"))
            .ToListAsync();

        return Ok(list);
    }

    // --------------------------------------------------
    // Принять заявку (только адресат)
    // POST /api/friends/requests/{requestId}/accept
    // --------------------------------------------------
    [HttpPost("requests/{requestId:guid}/accept")]
    public async Task<IActionResult> Accept(Guid requestId)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.AddresseeId == me);

        if (req is null) return NotFound(new { error = "Request not found." });

        req.Status = FriendshipStatus.Accepted;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // --------------------------------------------------
    // Отклонить заявку (только адресат)
    // POST /api/friends/requests/{requestId}/decline
    // --------------------------------------------------
    [HttpPost("requests/{requestId:guid}/decline")]
    public async Task<IActionResult> Decline(Guid requestId)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.AddresseeId == me);

        if (req is null) return NotFound(new { error = "Request not found." });

        req.Status = FriendshipStatus.Declined;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // --------------------------------------------------
    // Отменить свою исходящую (только отправитель)
    // DELETE /api/friends/requests/{requestId}
    // --------------------------------------------------
    [HttpDelete("requests/{requestId:guid}")]
    public async Task<IActionResult> Cancel(Guid requestId)
    {
        var me = MeId;

        var req = await _db.Friendships.FirstOrDefaultAsync(f =>
            f.Id == requestId &&
            f.Status == FriendshipStatus.Pending &&
            f.RequesterId == me);

        if (req is null) return NotFound(new { error = "Request not found." });

        _db.Friendships.Remove(req);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
