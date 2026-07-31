# План разработки Censor Extension v1.3.1

**Статус:** Core, session host, watchdog, manual tool window и single-DLL Windows CI реализованы на `codex/implement-censor-p0`; первый физический smoke выявил packaging blocker, исправленный artifact ожидает повторной проверки.
**Цель P0:** Windows-extension для зафиксированной stock-версии mpv.net, который применяет полноэкранный blur по session-scoped расписанию без базы данных.

## Зафиксированные решения

- Два проекта: кроссплатформенный `Censor.Core` и Windows-specific `Censor.MpvNet.Extension`.
- Расписание загружается вручную либо как exact sidecar `<basename>.censor.{txt,srt,vtt}` и живёт только в текущем `media_session_id`.
- SQLite, fingerprint, каталог фильмов, сеть, auto-update, launcher и форк mpv.net в P0 отсутствуют.
- Интервалы имеют семантику `[start_ms, end_ms)` и компилируются в labeled FFmpeg `enable` expressions; удаляются только `@censor_*` filters.
- Любой async-результат проверяет актуальный session ID и отменяется при смене media.
- Core benchmark: 1 000 интервалов parse + normalize + compile ≤ 250 мс; полный прогретый UI-flow загрузки ≤ 500 мс.
- Runtime-состояния: `IDLE`, `NO_SCHEDULE`, `LOADING`, `APPLYING`, `ACTIVE`, `WARNING`, `ERROR`. Редактирование — режим UI, не состояние playback.
- Global offset существует только в рабочей session; `Сохранить` записывает его в `# offset-ms`, но не создаёт per-film setting.
- `Сохранить` всегда пишет исходные timestamps и отдельный `# offset-ms`; offset никогда не запекается в границы.
- Допустимый `offset-ms`: от `-86_400_000` до `+86_400_000` включительно, вычисления выполняются в checked `Int64`.
- Неизвестные `# key: value` и комментарии сохраняются дословно и в исходном относительном порядке; duplicate известного ключа является ошибкой.
- Watchdog использует `earlyIntervalGuardMs` как единственный порог «следующий интервал близко».
- Начальные blur presets: `Strong = sigma 30 / steps 2`, `Maximum = sigma 50 / steps 3`; итоговые значения фиксируются после GPU-проверки.

## Этапы

### 0. Репозиторий и нормативная база

- Создать solution, pinned .NET SDK/dependencies, warnings-as-errors и locked restore.
- Перенести ТЗ в `docs/TZ.md`, ADR в `docs/adr/ADR-003-session-scoped-schedules-no-database.md`, пример в `tests/fixtures/example.censor.txt`.
- Выпустить нормативную TZ v1.3.1 и внести в неё все принятые Fable-поправки, чтобы план и ТЗ не были двумя источниками истины.
- Явно перечислить в ADR automatic sidecars `.censor.txt`, `.censor.srt`, `.censor.vtt`.
- Убрать неразрешимые ссылки на ADR-002/TZ v1.2, если исходники не добавляются в историю.
- Добавить минимальные `README.md`, `AGENTS.md`, `CLAUDE.md`, CI build/test skeleton и `deps.lock.json`; включить `csharp` в Serena после создания solution.

**Выход:** чистые restore/build/test на macOS с Windows targeting и на GitHub Actions Windows; документация и фактические пути не расходятся.

### 1. Phase 0 — риск-ориентированный mpv.net spike

- Зафиксировать версии mpv.net/libmpv/FFmpeg и доказать загрузку DLL, создание отдельного mpv client, lifecycle events и чтение `path`, `duration`, `time-pos`, `pause`, `speed`, `vf`.
- Применить один labeled `gblur`, подтвердить label через readback `vf`, удалить только свой filter и сохранить пользовательский filter.
- Проверить обычный файл и media с ненулевым start time: сравнить временную ось FFmpeg `t` с mpv `time-pos`.
- Выполнить seek назад/вперёд, pause/resume, speed 0.5x/1x/1.5x/2x, chapter seek, watch-later resume и A → B.
- Проверить отдельное tool window, script message, file picker и drag-and-drop без форка.

**Выход:** ADR с `stock extension sufficient`, выбранным filter syntax, hwdec profile и измеренными лимитами. Если `t` и `time-pos` расходятся, разрешена только доказанная session-level rebasing strategy; при неконстантном расхождении дальнейшая реализация останавливается до смены архитектуры.

### 2. Schedule core

- Реализовать immutable-модели `ScheduleDocument`, `Interval`, `ParseDiagnostic`, `NormalizationOptions`.
- Разобрать UTF-8 `*.censor.txt`, metadata, dot/comma timestamps, notes и неизвестную необязательную metadata с line/column diagnostics.
- Добавить минимальные SRT/WebVTT import/export adapters: cue → blur interval, cue text → note.
- Реализовать validation, lead-in/out, offset, clamp к нулю, sort, overlap/`merge_gap_ms` merge и canonical UTF-8 без BOM serializer.
- Ограничить файл 2 MiB и 10 000 исходных интервалов; сохранять atomically через temp + replace с backup.
- Использовать xUnit и FsCheck.Xunit; отдельный fuzz dependency не добавлять до измеримой необходимости.

**Выход:** round-trip fixtures и deterministic unit/property/fuzz checks проходят; повреждённый input не меняет активную модель и не портит существующий файл.

### 3. Filter compiler

- Преобразовать только нормализованные числовые интервалы в `gte(t,start)*lt(t,end)`.
- Генерировать стабильные labels `@censor_blur_NNN`, chunks до 500 интервалов и не более 50 filters.
- Централизовать mpv/FFmpeg escaping; schedule text никогда не попадает в command expression или shell.
- Возвращать `FilterPlan` как данные для typed/native mpv commands, без выполнения внутри Core.

**Выход:** точные boundary tests, большие расписания, locale-independence, escaping и benchmark ≤ 250 мс для 1 000 интервалов.

### 4. Media session integration

- Реализовать `MediaSessionCoordinator` с монотонным ID и `CancellationTokenSource` на сессию.
- На `StartFile` инвалидировать старую session, отменять операции и удалять прежние `@censor_*`; на `EndFile` очищать schedule/UI.
- Реализовать manual load, reload и exact sidecar lookup с приоритетом TXT → SRT → VTT; при нескольких sidecars показывать выбор.
- Проверять `media-duration-ms`: mismatch блокирует auto-load и требует явного подтверждения при manual load.
- Применять filters, читать `vf` обратно и переходить в `ACTIVE` только после подтверждения всех labels.
- Для раннего интервала сохранять pause, временно останавливать playback, применять/проверять filtergraph и восстанавливать pause.
- Если найден ровно один sidecar — загружать его автоматически; если несколько — показывать список в порядке TXT → SRT → VTT без молчаливого выбора.

**Выход:** race matrix A → B → C, смена media во время dialog/parse/apply, два picker, cancel, drop во время compile и reload не могут применить устаревший schedule.

### 5. Editor UI

- Реализовать одно Windows tool window с вкладками текущего фильма, интервалов, настроек и диагностики.
- Добавить grid, add/delete/duplicate/merge/split, захват `time-pos`, шаг ±100 мс, navigation, preview границы и undo/redo на 100 действий.
- Добавить F7/F8 и остальные authoring hotkeys с обнаружением конфликтов.
- Выполнять операции дольше 100 мс вне mpv event/UI thread; все callback закрыть exception boundary.
- `Save` сохраняет рабочие metadata/offset в текущий файл, `Save As` создаёт новый файл; скрытых привязок не создаётся.

**Выход:** UI smoke на физической Windows-машине, keyboard/drag-and-drop tests и simulated atomic-save failure без потери данных.

### 6. Watchdog, диагностика и совместимость

- Раз в `watchdogIntervalMs` проверять только ожидаемые labels; при потере безопасно pause/reapply/verify/restore.
- Добавить structured logs по `media_session_id`, parse/compile/apply statistics, incidents и exception stacks с privacy-safe media path.
- Собирать diagnostics bundle из manifest, sanitized settings, parse report, `vf` и environment; schedule включать только с согласием.
- Проверить allowlist сторонних mpv scripts и конфликты `vf clr`, playlist automation, F7/F8 и subprocess/auto-update.

**Выход:** filter-loss recovery не трогает пользовательские filters, mpv.net не падает от callback exception, soak test не выявляет stale-session apply.

### 7. Поставка и release QA

- Сначала собрать portable ZIP с isolated `portable_config`, pinned runtime, licenses/notices, checksums и SBOM.
- Затем добавить Windows installer, install/update/rollback test и сохранение предыдущего release artifact.
- В GitHub Actions выполнять locked restore, Release build, tests, package, installer, checksums, SBOM и artifacts.
- На self-hosted Windows runner пройти Intel/NVIDIA/AMD, 1080p30/60, 4K30, software decoding, full-film soak и OBS Window Capture.

**Выход:** выполнены все 15 acceptance criteria ТЗ; нет `BLOCKER/HIGH`, есть Windows evidence для mpv interaction и rollback artifact.

## Обязательные проверки и review gates

- Parser: malformed timestamps, `start >= end`, BOM/Unicode, metadata ambiguity, limits, SRT/VTT fixtures и round-trip.
- Compiler: `[start,end)` boundaries, locale decimal separator, chunks/labels, escaping и non-zero-start timeline.
- Lifecycle: seek/pause/speed/chapter, early interval guard, filter loss и весь cancellation/race matrix.
- Data safety: temp/replace/backup failures, отмена dialog и exception isolation.
- Для parser, compiler и media lifecycle запускать adversarial Claude Code Fable review через `cc`.
- Merge запрещён при `BLOCKER/HIGH`, stale-session риске либо отсутствии требуемого Windows runtime evidence.

## Вне P0

- macOS adapter и отдельное UI-ТЗ.
- База данных, central catalog, fingerprint, fuzzy matching, cloud/network и автоматическое скачивание.
- Форк mpv.net без зафиксированного провала Phase 0.

## Errors Encountered

| Error | Attempt | Resolution |
|---|---|---|
| Serena C# LSP reports `.NET runtime version 10.0 not found` | Installed SDK 10.0.302 locally, exposed it as `/opt/homebrew/bin/dotnet`, reactivated upstream project | Restart Codex/MCP so Serena inherits the new PATH; do not bypass semantic tooling |
| `mpvnet.dll` absent from portable release ZIP | Tried to extract a compile-time assembly after verifying the portable archive SHA-256 | Build only pinned upstream `MpvNet.csproj` and reference its `libmpvnet.dll` with `Private=false` |
