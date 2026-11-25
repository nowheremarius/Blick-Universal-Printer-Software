# ENG

## Blick - Universal Printer Software

Blick scans documents and photos on Windows through WIA and saves them as PDF, JPG or PNG. If several scanners are connected, Blick looks for a Pantum first. If it can't find one, it uses the first scanner on the list.

To build Blick, run `собрать.bat` from the project folder. The script uses the .NET Framework 4.x compiler and writes `Blick.exe` to the same folder.

### Scan a single sheet

Choose the source, file format and destination folder on the right, then click **Scan**. Click **Preview** first if you want to check the alignment or scan part of the sheet. Drag across the preview to select the area you need.

![Source, resolution and file format in the main Blick window](screenshots/basic.png)

You can also crop a page after scanning. Click its thumbnail at the bottom, select the area you want to keep and press Enter.

### Scan several pages

To save the pages as a single PDF, choose PDF as the format and turn on **Merge into one file** under **More**. Turn on **Recognize text** if you want to search or copy text in the PDF.

For a double-sided document, turn on **Manual duplex** and scan the fronts. Click **To backs**, flip the stack and scan it again. Blick puts the pages in reading order.

If you place several receipts or photos on the scanner glass, turn on **Split into objects** to save each one as a separate page.

![Text recognition and manual duplex settings on the More tab](screenshots/more.png)

### Notes

- `Ctrl+R` or `F5` replaces the last scan. If you scanned a stack through the feeder, this replaces the whole batch.
- The clock on the left opens your scan history. Click the gear at the top to change the language or interface scale.
- Settings are stored in `%APPDATA%\Blick\settings.ini`. Scan history is in `history.txt` in the same folder.

# RU

## Blick

Сканирование в Windows через WIA. Сохраняет документы и фотографии в PDF, JPG или PNG. Если подключено несколько сканеров, Blick сначала ищет Pantum. Если его нет, берёт первый сканер в списке.

Для сборки запустить `собрать.bat` из корня проекта. Скрипт найдёт компилятор .NET Framework 4.x и соберёт `Blick.exe` в той же папке.

### Один лист

Выбрать источник, формат файла и папку справа, затем нажать «Сканировать». Просмотр нужен, если хочется проверить, ровно ли лежит лист, или снять только его часть. Область сканирования можно выделить мышью прямо на изображении.

![Выбор источника, разрешения и формата файла в главном окне Blick](screenshots/basic.png)

Уже готовый скан тоже можно обрезать. Для этого выбрать его миниатюру внизу, выделить то, что должно остаться, и нажать Enter.

### Несколько страниц

Для одного PDF выбрать формат PDF и включить «Объединять в один файл» на вкладке «Дополнительно». Там же включается распознавание, если потом понадобится искать или копировать текст.

Для двустороннего документа включить «Две стороны вручную». Сначала пропустить лицевые стороны, нажать «К оборотным», перевернуть стопку и пропустить её ещё раз. Blick сам расставит страницы по порядку.

Если на стекле лежат несколько чеков или фотографий, включить «Разделять фото и чеки». Тогда у каждого будет своя страница.

![Вкладка дополнительных настроек. Здесь включаются распознавание текста и ручное двустороннее сканирование](screenshots/more.png)

### На память

- `Ctrl+R` или `F5` заменяет результат последнего сканирования. Если через податчик прошла пачка, команда заменит всю пачку.
- Часы слева открывают историю сканов. Язык и масштаб меняются через шестерёнку вверху.
- Настройки хранятся в `%APPDATA%\Blick\settings.ini`, история сканов лежит рядом в `history.txt`.
