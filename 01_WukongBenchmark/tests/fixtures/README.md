# Искусственный пример для OCR

`synthetic-result.png` создан скриптом `../make-ocr-fixture.ps1`.
Средний FPS 60.5, минимальный 40, максимальный 90 выбраны **только как тестовые числа**.
Это не скриншот Black Myth: Wukong и не результаты пользовательского компьютера.

`synthetic-result.tsv` получен настоящим распознаванием изображения Tesseract.js 7.0.0
(WASM-движок Tesseract, английский язык, PSM 11) при подготовке решения.
Он используется для проверки совместимости парсера с реальным форматом TSV.
Продуктовое приложение вызывает нативный `tesseract.exe`; его запуск на реальных
скриншотах игры здесь не проверен. Node.js/Tesseract.js для самого решения не нужны.

Повторить тест именно нативным Tesseract:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\test-ocr.ps1
```

Проверить уже сохранённый TSV (без установленного OCR):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
.\bin\Tests.exe .\tests\fixtures\synthetic-result.tsv
```
