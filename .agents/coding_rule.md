# ANGI — Coding Rules

## 1. Project Structure & Responsibilities

```
ANGI.Domain/
  Common/           ← BaseEntity only
  Entities/         ← Pure C# classes, no framework attributes
  Enums/            ← Business enums only

ANGI.Application/
  Common/
    Exceptions/     ← AppException.cs (base, carries ErrorCode), BadRequestException.cs, NotFoundException.cs, UnauthorizedException.cs, ForbiddenException.cs, ConflictException.cs, ServiceUnavailableException.cs
    Interfaces/
      Repositories/ ← IUserRepository, IUnitOfWork, ...
      Services/     ← IJwtService, IPasswordService, ICurrentUserService, ...
      UseCases/     ← ILoginUseCase, IGetRestaurantItemUseCase, ...
    Models/
  DTOs/
    Auth/           ← LoginRequestDto, LoginResponseDto, RegisterRequestDto
    RestaurantItem/          ← RestaurantItemResponseDto, CreateRestaurantItemRequestDto
    Chapter/        ← ChapterResponseDto, CreateChapterRequestDto
    ...             ← one subfolder per feature
  UseCases/         ← Use case implementations
    Validators/     ← FluentValidations
  DependencyInjection.cs

ANGI.Infrastructure/
  Persistences/
    Configs/        ← EF Fluent API configurations
    Migrations/     ← Auto-generated, do not edit manually
    Repositories/   ← Repository implementations
    ANGIContext.cs
  Services/         ← JwtService, PasswordService, CurrentUserService, ...
  DependencyInjection.cs

ANGI.WebAPI/
  Configs/          ← JwtConfig, CorsConfig, OpenApiConfig (OpenAPI + Scalar), RateLimitConfig
  Controllers/      ← Thin controllers only
  Middlewares/      ← ExceptionHandlingMiddleware
  Common/
    Models/
      ApiResponse.cs  ← ApiResponse<T> pattern, used by Controllers and Middleware
  DependencyInjection.cs
  Program.cs
```

### Layer Responsibilities

| Layer | Owns | Must NOT |
|---|---|---|
| Domain | Entities, Enums, base classes | Reference any other layer or NuGet |
| Application | Use cases, DTOs, interfaces, validators | Know EF Core, HTTP, SQL |
| Infrastructure | DbContext, repositories, external services | Contain business logic |
| WebAPI | HTTP pipeline, controllers, middleware | Inject DbContext or Infrastructure types directly |

---

## 2. Dependency Rules

```
Domain        ←  Application  ←  Infrastructure
                      ↑                ↑
                   WebAPI  ────────────┘ (DI registration only)
```

- `Domain` has **zero** dependencies.
- `Application` references `Domain` only.
- `Infrastructure` references `Domain` + `Application`.
- `WebAPI` references `Application` + `Infrastructure`.
- **Controllers must never inject** `ANGIContext`, any Repository class, or any Infrastructure type directly. Use interfaces from `Application.Common.Interfaces` only.

---

## 3. Naming Conventions

Follow Microsoft C# conventions:

| Element | Convention | Example |
|---|---|---|
| Class, Interface, Enum | PascalCase | `LoginUseCase`, `IUserRepository` |
| Method, Property | PascalCase | `ExecuteAsync`, `PasswordHash` |
| Local variable, parameter | camelCase | `loginRequest`, `userId` |
| Private field | `_camelCase` | `_userRepository` |
| Interface | Prefix `I` | `ILoginUseCase` |
| Enum value | PascalCase | `UserStatus.Active` |
| DTO suffix | `RequestDto` / `ResponseDto` | `LoginRequestDto`, `LoginResponseDto` |
| UseCase suffix | `UseCase` | `LoginUseCase` |
| Repository suffix | `Repository` | `UserRepository` |
| Controller suffix | `Controller` | `AuthController` |

---

## 4. Adding a New Feature — Checklist

Every new feature must follow this order:

```
1.  Domain        → Add/update Entity or Enum if needed
2.  Infrastructure → Add/update Fluent API config in Persistences/Configs/ if schema changed
3.  Infrastructure → Add EF migration (after Fluent config is complete)
4.  Application   → Add/update IRepository interface in Common/Interfaces/Repositories/ if needed
5.  Application   → Create DTOs subfolder DTOs/<Feature>/ if it does not exist
6.  Application   → Add RequestDto + ResponseDto inside DTOs/<Feature>/
7.  Application   → Add IUseCase interface in Common/Interfaces/UseCases/
8.  Application   → Add FluentValidation validator in Application/UseCases/Validators/<Feature>/
9.  Application   → Implement UseCase in UseCases/
          ├─ Inject IValidator<TRequestDto> — call ValidateAndThrowAsync() as first statement
          ├─ Inject ICurrentUserService if the feature requires the authenticated user's identity (see §13)
          └─ Inject IUnitOfWork for all write operations — call SaveChangesAsync() once at the end (see §14)
10. Application   → Register UseCase as AddScoped<> in DependencyInjection.cs
11. Infrastructure → Implement repository method
12. Infrastructure → Register in DependencyInjection.cs
13. WebAPI        → Add Controller endpoint
14. WebAPI        → Add [Authorize] if authentication required
```

---

## 5. ApiResponse Pattern

```
WebAPI/Common/Models/ApiResponse.cs  ← HTTP response shape, used in Controllers + Middleware
```

All HTTP responses — success and error — must use `ApiResponse<T>`. This is the format defined in `ANGI_API_Design_Ver1.6` (sheet *Tổng quan* → "Định dạng response", sheet *DTO* → `ApiResponse<T>`). The fields described for each endpoint in the API Design are the content of `Data`.

```csharp
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }      // user-facing message, usually null on success
    public string? ErrorCode { get; set; }    // null on success; on error a code from sheet "Mã lỗi"
    public T? Data { get; set; }              // null on error or when the endpoint returns nothing

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string[]>? Errors { get; set; }  // only for VALIDATION_FAILED: field → messages
}
```

JSON uses camelCase: `{ "success": true, "message": null, "errorCode": null, "data": { ... } }`.

The internal Recommendation API (FastAPI) is **not** wrapped; only the backend reads it.

### Success path (Controllers)

Controllers set both the HTTP status code **and** the body:

| Operation | HTTP status | Body |
|---|---|---|
| Fetch / update | 200 OK | `Success = true`, `Data = dto` |
| Create new resource | 201 Created | `Success = true`, `Data = dto` |
| Delete / no content (logout, resend email, change password, ...) | 200 OK | `Success = true`, `Data = null` |
| Background job accepted (only RM-10, IMP-01) | 202 Accepted | `Success = true`, `Data = dto` |

Never return `204 No Content` or `NoContent()`: every response has an `ApiResponse` body. Use exactly the status written in the API Design for each endpoint.

```csharp
// GET / PUT
return Ok(new ApiResponse<RestaurantItemResponseDto> { Success = true, Data = result });

// POST (create)
return StatusCode(StatusCodes.Status201Created, new ApiResponse<RestaurantItemResponseDto> { Success = true, Data = result });

// DELETE
return Ok(new ApiResponse<object> { Success = true, Data = null });
```

### Error path (ExceptionHandlingMiddleware)

The middleware must set **both** `context.Response.StatusCode` (HTTP header) and write the `ApiResponse<T>` body. The exception-to-status-code mapping is defined in §7.

```csharp
context.Response.StatusCode = StatusCodes.Status404NotFound;      // header
await context.Response.WriteAsJsonAsync(new ApiResponse<object>   // body
{
    Success = false,
    Message = exception.Message,
    ErrorCode = exception.ErrorCode,   // e.g. "RESTAURANT_NOT_FOUND"
    Data = null
});
```

Responses that do not pass through the middleware must use the same shape:

| Case | Where to configure | Status / ErrorCode |
|---|---|---|
| Missing or invalid token | `JwtBearerEvents.OnChallenge` | 401 `UNAUTHORIZED` |
| Wrong role / missing permission | `JwtBearerEvents.OnForbidden` (or an `IAuthorizationMiddlewareResultHandler`) | 403 `FORBIDDEN` |
| Model binding fails (bad JSON, wrong type) | `ApiBehaviorOptions.InvalidModelStateResponseFactory` | 400 `VALIDATION_FAILED` + `Errors` |
| Rate limit exceeded | `RateLimiterOptions.OnRejected` (also set `Retry-After`) | 429 `TOO_MANY_REQUESTS` |

- Never construct raw JSON error objects outside of `ApiResponse<T>`.
- Never set only the status code without a body, or only the body without the status code.

---

## 6. DTO & Validation Rules

- DTOs live in `Application/DTOs/<Feature>/` — one subfolder per feature, never a flat folder.
- **Only FluentValidation** — no DataAnnotations on DTOs.
- Each UseCase has its own validator. Placement rule: validators in `Application/UseCases/Validators/<Feature>/`.
- Validators are registered automatically via `AddValidatorsFromAssembly()` in `Application/DependencyInjection.cs`.
- Validation runs **inside the UseCase** before any business logic. Trigger it by injecting `IValidator<TRequest>` and calling `await _validator.ValidateAndThrowAsync(request, ct)` as the first statement in `ExecuteAsync`.

```csharp
public async Task<RestaurantItemResponseDto> ExecuteAsync(CreateRestaurantItemRequestDto request, CancellationToken ct)
{
    await _validator.ValidateAndThrowAsync(request, ct); // always first
    // business logic follows...
}
```

### Mapping rules

- **Entity → DTO mapping belongs in the UseCase** — never in the Controller.
- **Repository with `Include()` (full entity graph):** repository returns the entity; the UseCase maps it to a DTO manually.
- **Repository with `Select()` (projection):** the repository may project directly into a DTO inline inside the query. This is the one exception where DTO construction occurs outside the UseCase, and it is intentional — projecting in-query avoids loading unnecessary columns. Do not add a second mapping step in the UseCase for these queries.

```csharp
// ✅ Include() path — UseCase maps entity to DTO
var restaurantItem = await _restaurantItemRepository.GetWithDetailsAsync(id, ct); // returns RestaurantItem entity
return new RestaurantItemResponseDto { Id = restaurantItem.Id, Title = restaurantItem.Title, ... };

// ✅ Select() path — repository projects into DTO directly (no double-mapping in UseCase)
var restaurantItems = await _restaurantItemRepository.GetPublishedAsync(ct); // returns IEnumerable<RestaurantItemResponseDto>
return restaurantItems;
```

---

## 7. Error Handling

### Flow

```text
Controller
    ↓
UseCase / Service
    ↓
throw CustomException when error occurs
    ↓
ExceptionHandlingMiddleware
    ↓
ApiResponse<T>
```
Controllers must not catch exceptions only to convert them into HTTP responses.

### Error Response

All expected and unexpected errors must be thrown as exceptions.

Examples:

Every custom exception inherits `AppException` and carries an `ErrorCode`. Error codes and their HTTP status come from sheet **Mã lỗi** of `ANGI_API_Design_Ver1.6`; the errors specific to an endpoint are in column *Mã lỗi riêng* of sheet *Backend API*. Do not invent a code that is not in the API Design; add it there first.

```csharp
public abstract class AppException : Exception
{
    public string ErrorCode { get; }
    protected AppException(string errorCode, string message) : base(message) => ErrorCode = errorCode;
}

throw new BadRequestException("TOKEN_EXPIRED", "Liên kết đã hết hạn.");
throw new NotFoundException("RESTAURANT_NOT_FOUND", "Không tìm thấy nhà hàng.");
throw new UnauthorizedException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.");
throw new ForbiddenException("NOT_OWNER", "Bạn không phải chủ sở hữu.");
throw new ConflictException("REVIEW_ALREADY_EXISTS", "Bạn đã đánh giá nhà hàng này.");
throw new ServiceUnavailableException("SERVICE_UNAVAILABLE", "Hệ thống gợi ý tạm thời không phản hồi.");
```

`Message` is shown to the user, so write it in Vietnamese. The client branches on `ErrorCode`, never on `Message`.

### Exception Mapping

`ExceptionHandlingMiddleware` must map exceptions to HTTP status codes:

```text
ValidationException          -> 400 Bad Request          ErrorCode = VALIDATION_FAILED, Errors = failures grouped by property (camelCase)
BadRequestException          -> 400 Bad Request          ErrorCode from the exception
UnauthorizedException        -> 401 Unauthorized         ErrorCode from the exception
ForbiddenException           -> 403 Forbidden            ErrorCode from the exception
NotFoundException            -> 404 Not Found            ErrorCode from the exception
ConflictException            -> 409 Conflict             ErrorCode from the exception
ServiceUnavailableException  -> 503 Service Unavailable  ErrorCode from the exception
Exception                    -> 500 Internal Server Error ErrorCode = INTERNAL_ERROR, generic message, log the real exception
```

The API Design has no 422, 410, 413, 415 or 204: data and business-rule errors (expired token, file too large, wrong file type, ...) are all `BadRequestException` with their own `ErrorCode`. Never return exception details or stack traces in `Message` for a 500.

---

## 8. Query Rules — Preventing N+1

**Lazy loading is strictly forbidden.**

```csharp
// Always use explicit Include()
var restaurantItems = await _ctx.RestaurantItems
    .Include(n => n.Chapters)
    .Include(n => n.RestaurantItemTags)
        .ThenInclude(nt => nt.Tag)
    .ToListAsync(ct);
```

**Additional rules:**
- Use `AsNoTracking()` on all read-only queries — never track entities that will not be modified.
- Use `Select()` to project only needed fields — never load a full entity when only a few fields are needed.
  - **When using `Select()`, do NOT add `Include()`** — EF Core resolves the join automatically during projection. Adding `Include()` alongside `Select()` is redundant.
  - Use `Include()` only when you need the **full entity graph** (i.e. you are mapping navigation properties in the UseCase, not projecting inline).
- Use `AsSplitQuery()` for queries with multiple collection includes to avoid cartesian explosion. Do not use `AsSplitQuery()` with `Select()` projections — it has no effect there.

```csharp
// Select() for projection — no Include() needed
var restaurantItems = await _ctx.RestaurantItems
    .AsNoTracking()
    .Where(n => n.Status == RestaurantItemStatus.Published)
    .Select(n => new RestaurantItemResponseDto { Id = n.Id, Title = n.Title, TagNames = n.RestaurantItemTags.Select(nt => nt.Tag.Name).ToList() })
    .ToListAsync(ct);

// Include() for full entity graph — UseCase maps to DTO manually
var restaurantItem = await _ctx.RestaurantItems
    .AsNoTracking()
    .Include(n => n.Chapters)
    .Include(n => n.RestaurantItemTags).ThenInclude(nt => nt.Tag)
    .AsSplitQuery()
    .FirstOrDefaultAsync(n => n.Id == id, ct);
```

---

## 9. Async & CancellationToken Rules

- All methods that touch I/O (DB, HTTP, file) must be `async Task<T>`.
- Never use `.Result` or `.Wait()` — causes deadlocks.
- Never use `async void` — except for event handlers.
- `SaveChanges()` is forbidden — always use `SaveChangesAsync()`.
- **`CancellationToken` is required** on all async methods — propagate from Controller all the way down to EF Core.

---

## 10. Controller Rules

Controllers must be **thin** — receive request, call use case, return response. Nothing else.

```csharp
[ApiController]
[Route("api/v1/restaurantItems")]
public class RestaurantItemController : ControllerBase
{
    private readonly ICreateRestaurantItemUseCase _createRestaurantItem;
    private readonly IGetRestaurantItemUseCase _getRestaurantItem;

    public RestaurantItemController(ICreateRestaurantItemUseCase createRestaurantItem, IGetRestaurantItemUseCase getRestaurantItem)
    {
        _createRestaurantItem = createRestaurantItem;
        _getRestaurantItem = getRestaurantItem;
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(long id, CancellationToken ct)
    {
        var result = await _getRestaurantItem.ExecuteAsync(id, ct);
        return Ok(new ApiResponse<RestaurantItemResponseDto> { Success = true, Data = result });
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create(CreateRestaurantItemRequestDto request, CancellationToken ct)
    {
        var result = await _createRestaurantItem.ExecuteAsync(request, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RestaurantItemResponseDto> { Success = true, Data = result });
    }
}
```

Controllers must not: contain business logic, catch exceptions to convert to HTTP responses, inject repositories or `ANGIContext`, or perform any mapping.

---

## 11. Enum & Status Rules

- All `varchar` status fields in DB are represented as Enums in Domain.
- Store enums as `string` in DB via `.HasConversion<string>()` in Fluent config.
- Never use magic strings for status checks — always use enum values.

```csharp
// Correct
if (restaurantItem.Status == RestaurantItemStatus.Published)
```

---

## 12. Dependency Injection Rules

- Each layer registers its own services via extension method.
- `Program.cs` calls only: `AddWebAPI()`, `AddApplication()`, `AddInfrastructure()`.
- Use `AddScoped<>` for use cases, repositories, and services.
- Use `AddSingleton<>` only for stateless utilities (e.g. token parser).
- Never register Infrastructure types in `Application/DependencyInjection.cs`.
- `ExceptionHandlingMiddleware` must be registered as `AddScoped<ExceptionHandlingMiddleware>()` **because it implements `IMiddleware`**. If the implementation is ever changed to the convention-based approach (a public `InvokeAsync` method instead of `IMiddleware`), this registration must change to `AddTransient<>` and `UseMiddleware<>` will still work — but `AddScoped` without `IMiddleware` does not.
- In `Program.cs`, register the middleware pipeline with `app.UseMiddleware<ExceptionHandlingMiddleware>()` **before** `app.UseAuthentication()` and `app.UseAuthorization()` so all exceptions — including auth exceptions — are caught.

```csharp
// Correct pipeline order in Program.cs
app.UseMiddleware<ExceptionHandlingMiddleware>(); // must be first
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

---

## 13. Current User Identity — ICurrentUserService

UseCases that need the authenticated user's identity (e.g. set `AuthorId`, check ownership) must use `ICurrentUserService` — **never inject `IHttpContextAccessor` directly into a UseCase**.

---

## 14. Transaction Rules

- **Always use `IUnitOfWork`** for all write operations — both single-entity and multi-entity. Individual repositories do NOT expose `SaveChangesAsync()` and must not be expected to.
- `SaveChangesAsync()` is called **once** at the end of the UseCase via `IUnitOfWork`.
- Transaction orchestration belongs in the **UseCase**, never in the Controller or Repository.
