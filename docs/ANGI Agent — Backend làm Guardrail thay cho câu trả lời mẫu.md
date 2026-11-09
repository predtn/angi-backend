# ANGI Agent — Backend làm Guardrail thay cho câu trả lời mẫu

Nov 5, 2026 · @Hung

## Kết luận

Nên chuyển backend từ vai **người nói thay** sang **người cấp dữ kiện và giữ cửa**. AI đọc và trả lời mọi tin nhắn hội thoại; thứ cần gỡ là lời văn cứng, còn quyền quyết định trạng thái và dữ kiện vẫn thuộc backend. Câu cố định chỉ còn ở các chỗ nêu tại mục 4.

Guardrail phải nằm **trước** (dữ kiện vào qua slot, quyết định do backend chốt), không chỉ **sau** (lọc văn bản). Chỉ lọc phía sau sẽ cho câu chữ tự nhiên hơn nhưng mở lại đúng loại lỗi bịa và tự ý đổi trạng thái đã sửa sau conversation 10.

## Ba lớp mà câu trả lời mẫu đang gánh

Cảm giác máy móc đến từ lớp lời văn. Rủi ro bịa và tự ý thay đổi nằm ở hai lớp còn lại.

| Lớp | Ví dụ | Ai giữ |
| --- | --- | --- |
| Lời văn | "Chào bạn, mình là ANGI…", cách mở đầu preview, cách hỏi lại | **AI** — gỡ template ở đây là đúng |
| Dữ kiện | Tên món, quán, giá, tổng tiền, số ngày, mức vị đã áp dụng | **Backend cung cấp**, AI chỉ đặt vào câu |
| Trạng thái và quyết định | Lưu roadmap, `pendingSave`, xác nhận đổi ràng buộc, taste chưa rõ thì chưa lập kế hoạch | **Backend**, duyệt và giữ; AI chỉ đề xuất action |

## Vì sao chỉ lọc phía sau là chưa đủ

1. **Lọc văn bản tự do luôn có lỗ hổng.** `AgentReplyGuard` chỉ bắt được tên món viết thường khi chữ đó đứng đầu từ 5 tên món trở lên, nên món hiếm vẫn lọt. Một tên món bịa bằng tiếng Việt trông y như câu văn bình thường.
2. **Có câu mà nội dung chính xác quyết định trạng thái.** "ok" của người dùng phải gắn với đúng proposal ID và version. Mức khẩu vị đi vào vector xếp hạng, nên không được là con số do AI đoán.
3. **Chi phí và độ trễ.** Mỗi lượt do AI trả lời đều tốn một lời gọi provider. Số đo thật: một lượt "bạn là" mất 5,7 giây và 10.085 token, với prompt khoảng 30.000 ký tự. Cần đặt mục tiêu rút gọn prompt, thay vì giữ thêm luồng cố định chỉ để tiết kiệm.
4. **Kiểm thử.** Hơn 3900 unit test đang dựa trên hành vi xác định. Văn bản tự do chỉ kiểm được theo thuộc tính (ví dụ "không chứa giá ngoài plan"), khó hơn nhiều.

## Kiến trúc đề xuất

AI hiểu ý và diễn đạt; backend duyệt action, cấp dữ kiện và kiểm tra câu. AI không bao giờ tự viết con số hay tên riêng, nên được tự do giọng văn mà không bịa được dữ kiện.

Luồng một lượt: người dùng nhắn → AI hiểu ý và chọn action → backend duyệt action, cấp dữ kiện → AI viết câu quanh slot → backend điền slot và kiểm tra → người dùng đọc.

**0. AI hiểu ý, backend duyệt action theo rủi ro.** Backend không tự đoán ý bằng luật, vì văn nói biến hóa quá nhiều. Mọi cách nói được AI quy về một số ít action có schema. Backend chỉ cần hiểu action, không cần hiểu tiếng Việt.

- Đã có: `create_proposal`, `patch_proposal`, `clarify {kind}`, `answer`, `chat`, `food_info`, `out_of_scope`, `discard_proposal`, `reset_conversation`, tool `suggest_dish`.
- Cần thêm: `request_save {proposalId, version}`.

| Action | Nếu AI hiểu sai | Backend xử lý |
| --- | --- | --- |
| Sửa proposal | Chỉ ra một preview mới, roadmap đã lưu không bị đụng | Cho làm luôn; người dùng thấy sai thì nói lại |
| Xóa / bỏ preview | Mất bản nháp | Hiện làm ngay. Nên thêm hỏi xác nhận hoặc cho hoàn tác, nhưng đó là đổi luồng và chưa chốt |
| Commit (lưu) | Ghi roadmap, sinh impression | Câu lưu rõ ràng khớp cả câu giữ như hiện tại. Khi AI đọc ra ý lưu từ câu khác (`request_save`), backend phải hỏi xác nhận, gắn đúng proposal ID và version (`pendingSave`) |
| Ngoài phạm vi | Làm việc ANGI không được làm | AI chỉ phân loại `out_of_scope`; câu từ chối do backend chọn trong các biến thể cố định, kèm câu hỏi đang mở, không hiện chữ nào của AI |
| Không rõ ý | — | AI phải trả `clarify` thay vì đoán |

Mục tiêu là làm cho việc hiểu sai trở nên rẻ. Không giữ đường tắt từ khóa cho hội thoại: mỗi đường tắt là một luồng thứ hai dễ lệch với luồng chính. Ngoại lệ duy nhất là câu lưu khớp cả câu trong danh sách đóng, vì đó là chỗ ghi dữ liệu. Nút trên UI là đường chính cho lưu và xóa. Nên có bộ vài trăm câu tiếng Việt thật đã gắn nhãn action để đo độ chính xác mỗi lần đổi prompt hay model; bộ hiện có 120 câu.

**1. Fact pack và slot cho mọi action.** Mở rộng cơ chế slot đang dùng cho `suggest_dish`:

- Backend chuẩn bị gói dữ kiện có cấu trúc, ví dụ `{action: preview_created, place, numDays, total, emptyMeals, tastesApplied, disclosures}`.
- AI viết câu tự nhiên, dữ kiện đi qua slot (`{total}`, `{name1}`).
- Backend điền giá trị thật, kiểm tra slot đủ và slot của mỗi món đứng cùng nhau, phần chữ quanh slot không có số hay tên lạ.
- Câu của AI về preview được viết trước khi backend sửa món, nên slot phải được **điền sau khi kiểm tra xong**. Như vậy không tốn thêm lời gọi. Câu chỉ đúng tùy kết quả, như bữa còn thiếu hay fallback, do backend nối thêm.

**2. Câu hỏi đổi trạng thái: AI diễn đạt, đáp án giữ cấu trúc.** AI hỏi tự nhiên ("Bạn thích cay cỡ nào cho chuyến này?"), kèm chip do backend tạo từ `clarify.kind`: Không / Nhẹ / Vừa / Nhiều / Rất nhiều; Cả chuyến / Một ngày / Một bữa. Chip cần thêm trường vào API. Người dùng gõ tự do thì AI đọc và trả mức, kèm `levelQuote` là nguyên văn người dùng đã viết; backend chỉ kiểm tra đoạn đó có thật, nên không phải đọc tiếng Việt mà AI vẫn không tự chọn được mức cho lịch. Riêng một lần `suggest_dish` không lưu gì nên vị không nêu mức được tính 0.75 và AI nói rõ cách hiểu để người dùng chỉnh.

**3. Template là fallback, không là mặc định.** Kiểm tra không đạt mới dùng template. Event `reply_replaced` đã có sẵn, dùng để đo tỉ lệ thay thế theo từng action.

**4. Chỗ vẫn giữ câu cố định hoặc gần cố định** (có thể vài biến thể để đỡ lặp):

- Từ chối ngoài phạm vi.
- Xác nhận lưu và đổi ràng buộc, vì dính tới ghi dữ liệu.
- Câu hỏi dựa trên điều backend quan sát được: thiếu món, vượt ngân sách, trùng tên quán.
- Thông báo lỗi hệ thống.
- Disclosure về fallback và soft preference, vì cần minh bạch.

## Lộ trình

Thứ tự thực hiện:

1. Đã làm (2026-11-05): chào hỏi, câu mơ hồ, "ok", gợi ý món, câu hỏi taste và novelty, kèm `levelQuote`.
2. Fact pack cho preview và mô tả thay đổi.
3. Phần hỏi địa điểm, số ngày, ngân sách trước khi lập lịch; tool vẫn chặn khi thiếu.
4. Đổi nơi đến theo ngày, xóa món theo tên.
5. `request_save`.

Điều kiện mỗi bước: chạy bộ câu đã gắn nhãn trên model thật, mọi câu bắt buộc từ chối phải đạt, và các test luồng giữ nguyên kết quả. Backend chưa có cơ chế bật theo nhóm người dùng, nên việc thử trên nhóm nhỏ chưa làm được.

Tỉ lệ `reply_replaced` cao chỉ ra action cần sửa prompt hoặc fact pack. Tỉ lệ thấp chưa đủ để kết luận action đã ổn, vì có thể chỉ do hàng rào bỏ sót, và nó không phát hiện AI chọn sai action. Phải đọc kèm kết quả bộ câu đã gắn nhãn.
