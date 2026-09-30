# ANGI — Commit Guide

Theo chuẩn **Conventional Commits**. Commit message viết bằng **tiếng Anh**.

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

Closes #12
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
| Đóng issue | `Closes #12` |
| Tham chiếu issue | `Refs #34` |
| Breaking change | `BREAKING CHANGE: <mô tả>` hoặc thêm `!` sau type |

```
feat(webapi)!: wrap all responses in ApiResponse<T>

BREAKING CHANGE: clients must read data from the `data` field.
Closes #21
```

## 7. Quy tắc làm việc

- **Một commit = một thay đổi logic.** Nếu message cần chữ "and", hãy tách commit.
- **Mỗi commit phải build được** (`dotnet build` không lỗi).
- Chạy `dotnet build` (và `dotnet test` nếu đã có test) trước khi commit.
- Không commit: `bin/`, `obj/`, secrets, `appsettings.*.local.json` (đã có trong `.gitignore`).
- Không dùng `git commit -m "..."` cho thay đổi lớn cần giải thích — mở editor để viết body.

## 8. Tên nhánh

```
<type>/<mô-tả-ngắn>
```

Ví dụ: `feat/login-usecase`, `fix/null-user`, `chore/add-packages`, `refactor/split-validators`.

### Luồng nhánh

```
feat/login-usecase ──PR──▶ dev ──(khi quyết định phát hành)──▶ main
```

| Nhánh | Vai trò |
|---|---|
| `main` | Bản ổn định, chỉ nhận merge từ `dev` khi quyết định phát hành cuối |
| `dev` | Nhánh tích hợp, gom các feature để chạy thử |
| `<type>/<mô-tả>` | Nhánh làm việc, **tạo từ `dev`** |

- Không commit trực tiếp vào `main` và `dev`; làm trên nhánh riêng rồi mở Pull Request.
- Sau khi PR được merge, **xóa nhánh làm việc** (không xóa `main`, `dev`).

## 9. Pull Request

- **Base branch của PR là `dev`, không phải `main`.** Chỉ PR từ `dev` sang `main` mới trỏ vào `main`.
- Tiêu đề PR theo cùng format commit: `feat(auth): add login use case`.
- Mô tả ngắn gồm: làm gì, vì sao, cách kiểm tra.
- Nếu merge bằng **squash**, tiêu đề PR chính là commit message cuối cùng → phải đúng format.

## 10. Checklist trước khi commit

- [ ] Type đúng?
- [ ] Subject bắt đầu bằng động từ, chữ thường, không dấu chấm?
- [ ] Commit chỉ chứa một thay đổi logic?
- [ ] Build qua?
- [ ] Không lẫn file rác hoặc secrets?
- [ ] Đang ở nhánh riêng (tạo từ `dev`), PR trỏ vào `dev`?
