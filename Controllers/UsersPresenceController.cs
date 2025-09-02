using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JaeZoo.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public class UsersPresenceController : ControllerBase
{
    // оставь как есть свою логику/инъекции — главное, что кеш вырублен атрибутом
}
