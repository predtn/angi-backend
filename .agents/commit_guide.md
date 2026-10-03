# ANGI — Commit Guide

Theo chuẩn **Conventional Commits**. Commit message viết bằng **tiếng Anh**.
Task được quản lý trên **Jira** (mã dạng `ANGI-123`); mọi nhánh, commit và PR đều phải truy được về một task.

## 1. Format

```
<type>(<scope>): <subject>

<body>        ← tùy chọn

<footer>      ← tùy chọn
```

Ví dụ:

```
feat(auth): add JWT login use case

Validate credentials via IPasswordService and issue an access token
through IJwtService. Returns 401 on wrong password.

Refs: ANGI-12
```

## 2. Type

| Type | Dùng khi | Ví dụ |
|---|---|---|
| `feat` | Thêm tính năng | `feat(auth): add login endpoint` |
| `fix` | Sửa lỗi | `fix(webapi): return 404 when item not found` |
| `refactor` | Đổi cấu trúc code, không đổi hành vi | `refactor(application): split validators by feature` |
| `test` | Thêm/sửa test | `test(application): add LoginUseCase tests` |
| `docs` | Chỉ sửa tài liệu | `docs: update README setup steps` |
| `chore` | Việc vặt: package, config, `.gitignore` | `chore(deps): add FluentValidation` |
| `build` | Build system, dependency của build | `build: target net10.0` |
| `ci` | Cấu hình CI/CD | `ci: add GitHub Actions workflow` |
| `perf` | Tối ưu hiệu năng | `perf(infra): add index on User.Email` |
| `style` | Format code, không đổi logic | `style: format Program.cs` |
| `revert` | Hoàn tác commit | `revert: feat(auth): add login endpoint` |

## 3. Scope

Scope là phần bị ảnh hưởng, **tùy chọn**. Dùng tên layer hoặc feature:

| Scope | Ý nghĩa |
|---|---|
| `domain` | ANGI.Domain |
| `application` | ANGI.Application |
| `infra` | ANGI.Infrastructure |
| `webapi` | ANGI.WebApi |
| `test` | ANGI.Test |
| `auth`, `restaurant`, ... | Theo module, khi thay đổi đi qua nhiều layer |

Thay đổi thuộc một chức năng, dù đi qua nhiều layer → dùng scope theo module (`feat(auth): ...`).

Thay đổi thuần kỹ thuật, không gắn với chức năng nào -> dùng scope theo layer (`chore(infra) :...`, `refactor(application): ...`).

## 4. Subject — 5 quy tắc

1. Thể mệnh lệnh: `add`, `fix`, `remove` (không phải `added`, `fixes`).
2. Chữ thường ở đầu.
3. Không có dấu chấm cuối.
4. Tối đa ~70 ký tự.
5. Nói rõ **làm gì**, không viết chung chung.

| ❌ Sai | ✅ Đúng |
|---|---|
| `update` | `refactor(infra): extract JwtService from AuthService` |
| `fix bug` | `fix(auth): reject expired refresh token` |
| `Added login.` | `feat(auth): add login use case` |
| `wip` | (commit khi đã xong một phần việc hoàn chỉnh) |

## 5. Body — khi nào cần?

Thêm body khi lý do không hiển nhiên từ diff. Body trả lời **vì sao**, không phải **sửa thế nào**. Cách header một dòng trống.

```
fix(auth): reject expired refresh token

Expiry was compared in local time while the token stores UTC,
so tokens stayed valid up to 7 hours past expiry.
```

## 6. Footer

| Mục đích | Cú pháp |
|---|---|
| Gắn task Jira (**bắt buộc** với `feat`, `fix`) | `Refs: ANGI-123` |
| Gắn nhiều task | `Refs: ANGI-123, ANGI-124` |
| Breaking change | `BREAKING CHANGE: <mô tả>` hoặc thêm `!` sau type |

Không dùng `Closes #12`: team không dùng GitHub Issues. Trạng thái task được chuyển bằng tay trên Jira.

```
feat(webapi)!: wrap all responses in ApiResponse<T>

BREAKING CHANGE: clients must read data from the `data` field.
Refs: ANGI-21
```

## 7. Quy tắc làm việc

- **Một commit = một thay đổi logic.** Nếu message cần chữ "and", hãy tách commit.
- **Mỗi commit phải build được** (`dotnet build` không lỗi).
- Chạy `dotnet build` (và `dotnet test` nếu đã có test) trước khi commit.
- Không commit: `bin/`, `obj/`, secrets, `appsettings.*.local.json` (đã có trong `.gitignore`).
- Không dùng `git commit -m "..."` cho thay đổi lớn cần giải thích — mở editor để viết body.

## 8. Tên nhánh

```
<type>/<ANGI-mã>-<mô-tả-ngắn>
```

Ví dụ: `feat/ANGI-12-login-usecase`, `fix/ANGI-87-null-user`. Việc kỹ thuật không có task Jira thì bỏ mã: `chore/add-packages`.

Một nhánh = một task Jira, merge trong **2–3 ngày**. Task lớn hơn thì chia nhỏ trên Jira trước.

### Luồng nhánh

```
feat/ANGI-12-login-usecase ──PR──▶ dev ──(khi quyết định phát hành)──▶ main
```

| Nhánh | Vai trò |
|---|---|
| `main` | Bản ổn định, chỉ nhận merge từ `dev` khi quyết định phát hành cuối |
| `dev` | Nhánh tích hợp, gom các feature để chạy thử |
| `<type>/<ANGI-mã>-<mô-tả>` | Nhánh làm việc, **tạo từ `dev`** |

- Không commit trực tiếp vào `main` và `dev`; làm trên nhánh riêng rồi mở Pull Request.
- Sau khi PR được merge, **xóa nhánh làm việc** (không xóa `main`, `dev`).
- Mỗi ngày trước khi code: `git pull origin dev` vào nhánh của mình để bắt conflict sớm, lúc còn nhỏ.

## 9. Pull Request

- **Base branch của PR là `dev`, không phải `main`.** Chỉ PR từ `dev` sang `main` mới trỏ vào `main`.
- Tiêu đề PR theo cùng format commit: `feat(auth): add login use case`.
- Dòng đầu mô tả PR ghi mã task: `Jira: ANGI-12`. Mô tả ngắn gồm: làm gì, vì sao, cách kiểm tra.
- Cần **1 approve** của người review được phân công trên Jira trước khi merge.
- Người review phải review trong **24 giờ** kể từ lúc PR được mở. Có comment thì người mở PR sửa rồi báo lại, không để PR treo qua ngày code due.
- **Code freeze mỗi iteration:** hết ngày code due (20/10, 03/11, 17/11) mọi PR phải được merge vào `dev`. Ngày hôm sau Dũng chạy toàn hệ thống từ `dev`, smoke test và gắn tag `iteration-N`. Từ lúc freeze đến khi phát hành bản của iteration, `dev` chỉ nhận PR fix bug.
- **Luôn merge bằng squash.** Tiêu đề PR chính là commit message cuối cùng → phải đúng format, và thêm `Refs: ANGI-xxx` vào phần body khi squash.
- Sau khi merge: dán link PR vào comment của task Jira rồi mới chuyển task sang Done.

## 10. Tránh conflict

| Chỗ hay conflict | Quy tắc |
|---|---|
| EF Core migration (`Migrations/`, `*ModelSnapshot.cs`) | Pull `dev` mới nhất **ngay trước** khi `dotnet ef migrations add`. Mỗi PR tối đa 1 migration. Nếu PR bị conflict migration: xóa migration của mình, pull `dev`, tạo lại migration. **Không sửa tay** file snapshot. Schema `core` chỉ đổi qua EF migration, không chạy SQL tay lên DB chung. |
| Alembic migration (reco, `alembic/versions/`) | Schema `recommendation` chỉ đổi qua Alembic. Pull `dev` trước khi `alembic revision`; nếu hai revision cùng trỏ một `down_revision` thì tạo lại revision của mình trên head mới. |
| `Program.cs`, đăng ký DI | Mỗi module có extension method riêng (`AddAuthModule()`, `AddRestaurantModule()`...) trong thư mục của module; `Program.cs` mỗi module chỉ thêm 1 dòng. |
| `AppDbContext` | Cấu hình entity đặt trong `IEntityTypeConfiguration<T>` riêng từng file, `DbContext` dùng `ApplyConfigurationsFromAssembly`. |
| `appsettings.json`, `.csproj`, `package.json` | Chỉ thêm, không sắp xếp lại hay format lại cả file. Thêm package → commit riêng `chore(deps): ...`. |
| Format code | Không format cả file khi chỉ sửa vài dòng; dùng chung `.editorconfig`. |

Khi gặp conflict: tự giải quyết trên nhánh của mình (`git pull origin dev`, sửa, build, test), **không** giải conflict trên GitHub web cho file `.cs`. Không chắc phần code của người khác thì hỏi người đó trước khi chọn bên.

## 11. Checklist trước khi commit

- [ ] Type đúng?
- [ ] Subject bắt đầu bằng động từ, chữ thường, không dấu chấm?
- [ ] Commit chỉ chứa một thay đổi logic?
- [ ] Build qua?
- [ ] Không lẫn file rác hoặc secrets?
- [ ] Đang ở nhánh riêng (tạo từ `dev`), PR trỏ vào `dev`?
- [ ] Có `Refs: ANGI-xxx` (với `feat`, `fix`)?
- [ ] Đã pull `dev` mới nhất nếu có migration?
