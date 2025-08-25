using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace JaeZoo.Server.Hubs;

[Authorize]
public class ChatHub : Hub
{
    private readonly AppDbContext _db;
    private readonly IPresenceTracker _presence;

    public ChatHub(AppDbContext db, IPresenceTracker presence)
    {
        _db = db;
        _presence = presence;
    }

    private Guid MeId => Guid.Parse(Context.User!.FindFirst(ClaimTypes.NameIdentifier)!.Value);
    private static (Guid a, Guid b) OrderPair(Guid x, Guid y) => x < y ? (x, y) : (y, x);

    private async Task<bool> AreFriends(Guid me, Guid other) =>
        await _db.Friendships.AnyAsync(f =>
            f.Status == FriendshipStatus.Accepted &&
            ((f.RequesterId == me && f.AddresseeId == other) ||
             (f.RequesterId == other && f.AddresseeId == me)));

    private async Task<DirectDialog> GetOrCreateDialog(Guid aId, Guid bId)
    {
        var (u1, u2) = OrderPair(aId, bId);
        var dlg = await _db.DirectDialogs.FirstOrDefaultAsync(d => d.User1Id == u1 && d.User2Id == u2);
        if (dlg is not null) return dlg;
        dlg = new DirectDialog { User1Id = u1, User2Id = u2 };
        _db.DirectDialogs.Add(dlg);
        await _db.SaveChangesAsync();
        return dlg;
    }

    // ===== Presence (с учётом ShowOnline) =====

    public override async Task OnConnectedAsync()
    {
        var userId = MeId.ToString();
        var first = await _presence.UserConnected(userId, Context.ConnectionId);

        if (first)
        {
            // Проверяем приватность
            var canShow = await _db.Users
                .Where(u => u.Id == MeId)
                .Select(u => u.ShowOnline)
                .FirstOrDefaultAsync();

            if (canShow)
                await Clients.AllExcept(Context.ConnectionId).SendAsync("UserOnline", userId);
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = MeId.ToString();
        var last = await _presence.UserDisconnected(userId, Context.ConnectionId);

        if (last)
        {
            var canShow = await _db.Users
                .Where(u => u.Id == MeId)
                .Select(u => u.ShowOnline)
                .FirstOrDefaultAsync();

            if (canShow)
                await Clients.All.SendAsync("UserOffline", userId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Список userId, которые и подключены, и не скрывают присутствие.</summary>
    public async Task<List<string>> GetOnlineUsers()
    {
        var online = await _presence.GetOnlineUsers(); // все подключённые id (string)
        if (online.Count == 0) return online;

        var onlineGuids = online.Select(Guid.Parse).ToList();
        var visible = await _db.Users
            .Where(u => onlineGuids.Contains(u.Id) && u.ShowOnline)
            .Select(u => u.Id.ToString())
            .ToListAsync();

        // упорядочим, чтобы клиент получал стабильный список
        visible.Sort(StringComparer.Ordinal);
        return visible;
    }

    // ===== Direct messages =====

    public async Task SendDirectMessage(Guid targetUserId, string text)
    {
        text = (text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return;

        var me = MeId;

        if (!await AreFriends(me, targetUserId))
            throw new HubException("Вы не друзья.");

        var dlg = await GetOrCreateDialog(me, targetUserId);

        var msg = new DirectMessage
        {
            DialogId = dlg.Id,
            SenderId = me,
            Text = text,
            SentAt = DateTime.UtcNow
        };
        _db.DirectMessages.Add(msg);
        await _db.SaveChangesAsync();

        // Отправляем обоим юзерам
        var payload = new { senderId = msg.SenderId, text = msg.Text, sentAt = msg.SentAt };
        await Clients.Users(me.ToString(), targetUserId.ToString())
            .SendAsync("ReceiveDirectMessage", payload);
    }
}
