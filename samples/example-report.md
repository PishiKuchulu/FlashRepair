# گزارش عیب‌یابی فلش

- نشست: `d3c54b0d9d26`
- دستگاه: SimFlash 16GB (Virtual)
- شماره سریال: `SIM20260002`
- ظرفیت اعلامی: 16,000,000,000 بایت
- زمان شروع: 2026-10-23 02:19:02 UTC

## تشخیص

- دسته خرابی: **FilesystemCorruption** (اطمینان 85 %)
- توضیح: فایل‌سیستم خراب است ولی سطح حافظه و پارتیشن سالم به نظر می‌رسد؛ این رایج‌ترین و امیدوارکننده‌ترین نوع خرابی است.
- سرنوشت داده: قبل از هر تعمیری که بنویسد، اول باید برای نجات داده تلاش شود؛ فرمت یعنی پاک شدن داده.
- قدم بعدی پیشنهادی: قدم بعدی فاز ۲ است: اول تلاش برای نجات داده، بعد تعمیر فایل‌سیستم یا فرمت با تأیید شما.

## قدم‌های اجراشده

- [02:19:02] `get_device_details` (ریسک ReadOnly) — موفق: SimFlash 16GB serial SIM20260002, 16000000000 bytes, bus USB.
- [02:19:02] `get_disk_layout` (ریسک ReadOnly) — موفق: Layout GPT, 1 partition(s).
- [02:19:02] `check_filesystem` (ریسک ReadOnly) — موفق: Filesystem state: Raw. Volume is RAW: Windows cannot read the filesystem, data region reads OK.
- [02:19:02] `read_test_sample` (ریسک ReadOnly) — موفق: 7/7 sampled reads OK (dead ratio 0 %).
- [02:19:02] `detect_fake_capacity_risk` (ریسک ReadOnly) — موفق: Fake-capacity risk score 0.05. No fake-capacity signal in sampled reads.
- [02:19:02] `identify_controller` (ریسک ReadOnly) — موفق: Candidate controller: Silicon Motion (confidence 0.55). VID 0x090C = Silicon Motion-based; brand-level indication only, not a silicon ID.
- [02:19:02] `lookup_firmware_catalog` (ریسک ReadOnly) — موفق: No tested firmware candidate for 'Silicon Motion' in the local catalog.
- [02:19:02] `get_device_history` (ریسک ReadOnly) — موفق: 1 previous session(s).
- [02:19:02] `explain_finding` (ریسک ReadOnly) — موفق: The filesystem is corrupted while the surface and partition look intact; this is the most common and most repairable fault.

این گزارش فقط از خروجی واقعی ابزارها ساخته شده است؛ هیچ عددی حدس زده نشده است.
