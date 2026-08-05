# ProcmonHelper

[English](#english) · [Русский](#русский)

![ProcmonHelper main window](docs/images/procmonhelper-main.png)

## English

Process Monitor records activity from the entire system. Starting a capture too early or stopping it too late fills the PML with unrelated events and makes the useful information harder to find.

ProcmonHelper starts capture immediately before launching the selected application and stops it at the required moment. This produces a smaller, cleaner, and more focused log containing the application startup and the activity needed for diagnosis.

### Why use it

- Capture begins immediately before the target application starts: early startup events are preserved without collecting unrelated activity beforehand.
- Capture stops at the required moment instead of continuing to fill the log with unnecessary events.
- Collection can stop automatically when the target closes, after a time limit, at a PML size limit, or when the free-space reserve is reached.
- Manual stop finalizes and preserves the collected PML.
- The PML is saved locally first, so a failed CSV/XML export or network copy does not discard the main trace.
- Reusable profiles keep launch, capture, stop, and save settings together.
- The status panel shows capture timing, active conditions, filters, and the saved file path.
- No installation is required: the release is a single portable EXE.

### Requirements

- Windows 10 or Windows 11 x64
- `Procmon64.exe` from the official [Microsoft Sysinternals Process Monitor page](https://learn.microsoft.com/sysinternals/downloads/procmon)
- Administrator approval when the capture worker starts

Process Monitor is not included and is never downloaded automatically. If its license dialog has not been accepted yet, start `Procmon64.exe` manually once before using ProcmonHelper.

### Quick start

1. Download and extract Process Monitor.
2. Start `ProcmonHelper.exe` and select `Procmon64.exe`.
3. Select the application whose launch you want to trace. Arguments and working directory are optional.
4. Configure capture mode and stop conditions.
5. Select the local folder where the PML should be saved.
6. Click **Start capture** and approve elevation.
7. Use the launched application normally. Stop it manually when needed, or let the configured condition stop the capture.

The completed PML path is shown in the status panel. PML files can be opened directly in Process Monitor.

### Capture modes and filters

- **All events** starts Process Monitor without inherited saved filters.
- **Selected processes** stores the process list in the profile and capture summary. It does not physically remove unrelated events from the raw PML.
- **PMC configuration** loads a user-prepared `.PMC` file. Use this mode when the PML itself must be filtered by Process Monitor rules.

ProcmonHelper is not added to the selected process list. In **All events** mode it can still appear in the raw PML; use an Exclude rule in a PMC file when physical exclusion is required.

### License

ProcmonHelper is distributed under the [MIT License](LICENSE).

---

## Русский

Process Monitor записывает активность всей системы. Если начать сбор слишком рано или остановить слишком поздно, PML заполняется посторонними событиями, среди которых сложнее найти полезную информацию.

ProcmonHelper начинает сбор непосредственно перед запуском выбранной программы и останавливает его в нужный момент. В результате получается меньший и более чистый лог, содержащий запуск программы и только необходимый для диагностики промежуток работы.

### Зачем использовать

- Сбор начинается непосредственно перед запуском целевой программы: ранние события старта сохраняются, а посторонняя активность до запуска не записывается.
- Сбор останавливается в нужный момент и не продолжает заполнять лог ненужными событиями.
- Сбор можно автоматически остановить после закрытия программы, по времени, размеру PML или при достижении резерва свободного места.
- Ручная остановка корректно завершает сбор и сохраняет записанный PML.
- PML сначала сохраняется локально: ошибка экспорта CSV/XML или сетевого копирования не уничтожает основной лог.
- Профили сохраняют вместе параметры запуска, сбора, остановки и сохранения.
- Информационная панель показывает время, активные условия, фильтры и путь к сохранённому файлу.
- Установка не требуется: релиз состоит из одного портативного EXE.

### Требования

- Windows 10 или Windows 11 x64
- `Procmon64.exe` с официальной [страницы Microsoft Sysinternals Process Monitor](https://learn.microsoft.com/sysinternals/downloads/procmon)
- Подтверждение запуска повышенного рабочего процесса

Process Monitor не входит в комплект и не скачивается автоматически. Если лицензионное окно ещё не было принято, один раз запустите `Procmon64.exe` вручную перед использованием ProcmonHelper.

### Быстрый старт

1. Скачайте и распакуйте Process Monitor.
2. Запустите `ProcmonHelper.exe` и выберите `Procmon64.exe`.
3. Выберите программу, запуск которой нужно записать. Аргументы и рабочая папка необязательны.
4. Настройте режим сбора и условия остановки.
5. Выберите локальную папку для сохранения PML.
6. Нажмите **Начать сбор** и подтвердите повышение прав.
7. Работайте с запущенной программой как обычно. При необходимости остановите сбор вручную или дождитесь выполнения заданного условия.

Путь к готовому PML отображается в информационной панели. PML-файл можно открыть непосредственно в Process Monitor.

### Режимы сбора и фильтры

- **Все события** запускает Process Monitor без ранее сохранённых фильтров.
- **Выбранные процессы** сохраняет список процессов в профиле и сводке сбора, но не удаляет посторонние события из исходного PML.
- **Конфигурация PMC** загружает подготовленный пользователем файл `.PMC`. Используйте этот режим, если сам PML должен быть отфильтрован правилами Process Monitor.

ProcmonHelper не добавляется в список выбранных процессов. В режиме **Все события** он всё равно может присутствовать в исходном PML; для физического исключения добавьте правило Exclude в PMC-файл.

### Лицензия

ProcmonHelper распространяется по условиям [лицензии MIT](LICENSE).
