# План разработки Censor Extension v1.3.1

**Статус:** findings второго Claude Opus 5 review исправлены локально в
`codex/implement-censor-p0`. Проходят 64 Core-теста, Release-сборка без
предупреждений и проверка закреплённой compile reference. Осталось подтвердить
Windows runtime/installer smoke в CI, получить чистый следующий review через
Claude Opus 5 и физически проверить новый DLL. Полный portable/installer
по-прежнему нельзя публиковать до закрытия source-provenance gate.
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
- Blur presets: `Moderate = sigma 30 / steps 2`, default
  `Balanced = sigma 40 / steps 2`, `Maximum = sigma 50 / steps 3`.
- Синтетический 4K30 с software decode остаётся известным ограничением
  плавности из issue #3, но не блокирует P0, пока каждый показанный кадр
  проходит через censor filter.
- P0 выпускается только для Windows x64 под `GPL-2.0-only`; RC не подписан
  Authenticode. Installer работает без прав администратора и сохраняет
  пользовательские данные при обычном удалении.

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

- Реализовать `MediaSessionCoordinator` с монотонным ID и
  `CancellationTokenSource` на сессию.
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
- В GitHub Actions выполнять locked restore, Release build, tests, package,
  installer, checksums и SBOM. До закрытия source-provenance gate загружать
  только DLL расширения и исходный код самого проекта.
- На Windows пройти Intel/NVIDIA/AMD, 1080p30/60, software decoding и
  full-film soak. Реальные 4K-фильмы и OBS проверяются отдельно в issue #3.

**Выход:** выполнены все 15 acceptance criteria ТЗ; нет `BLOCKER/HIGH`, есть Windows evidence для mpv interaction и rollback artifact.

### 8. Post-review fixes и аудиокомпрессия

- Закрыть installer update, stale settings, disabled-watchdog, draft-limit,
  atomic-write, recovery, UTC-log, hotkey timestamp и truthful-status findings
  итогового Fable-review без изменения `MediaSessionCoordinator`.
- Добавить глобальную аудиокомпрессию с default `off` и пресетами Film,
  Anime, Night и Adaptive через единственную метку
  `@censor_audio_compression`; пользовательский `af` не менять.
- Применять аудиопресет сразу, подтверждать через readback `af` и показывать
  русский OSD только после успешного применения; при ошибке откатывать
  прежний пресет.
- Доказать точные графы на закреплённом Windows runtime, усилить installer
  update/uninstall smoke и повторить полный Claude Opus 5 review через `cc`.

**Выход:** локальные проверки и точный Windows CI зелёные, Claude Opus 5 не
оставил actionable findings, пользователю передан один итоговый DLL для
физического теста.

### 9. Исправления после Claude Opus 5 review

- Санитизировать каждую JSONL-запись журнала при экспорте диагностики: пути и
  тексты ошибок заменять SHA-256, повреждённые строки не копировать дословно.
- При недоступном каталоге логов загружать расширение с отключённым журналом;
  завершение работы не должно ждать занятый filter gate дольше пяти секунд.
- Удалять recovery-файл после возврата черновика в сохранённое состояние,
  возвращать отклонённые настройки в форму и ограничить очередь действий окна.
- Заморозить снимки `ScheduleDraft`, переиспользовать validation result и при
  редактировании одной границы обновлять только затронутую строку таблицы.
- Свести durable temp/flush/replace в `AtomicFile`, убрать пустой `T(...)`,
  повторяющийся event dispatch и отдельный `ScheduleOptionsResolver`.
- Закрепить рекурсивное включение Core sources, retry загрузок и независимый от
  текущего каталога путь аудиосмока.

**Выход:** privacy/robustness findings закрыты регрессионными тестами, оба
Windows CI зелёные, повторный `cc review` на `claude-opus-5` не оставляет
actionable findings.

### 10. Исправления после повторного Claude Opus 5 review

- Канонизировать регистронезависимый ID аудиопресета при загрузке настроек и
  закрепить этот контракт Core-тестом до создания WinForms-окна.
- Сериализовать сохранение настроек отдельной блокировкой: immutable snapshot
  брать под `_stateLock`, а durable write выполнять после его освобождения.
- Выполнять normalize/compile черновика до записи расписания, чтобы ошибка
  подготовки filter plan не оставляла на диске неподтверждённый файл.
- Сохранять читаемый текст ошибок в диагностическом ZIP, хешируя только
  абсолютные пути; невалидные JSONL-строки по-прежнему заменять маркером.
- Исправить чтение `MPV_FORMAT_FLAG`, ложный `watchdog-recovered`, имя после
  импорта `.censor.srt/.vtt` и асимметрию redo; удалить ложную команду
  `censor-diagnostics` из примера.
- Удалить лишний Extension writer wrapper и generic `Dispatch` overloads,
  объединить test helper и зафиксировать порядок блокировок. Validation cache
  и zero-copy `Freeze` сохранить как защиту сценария с 10 000 интервалов.
- Не использовать общий `compileReferenceSha256`: одинаковый source commit и
  SDK дают разные DLL на macOS и Windows. Закреплять commit и требовать чистый
  mpv.net source checkout перед каждой сборкой.

**Выход:** второй набор findings закрыт минимальными регрессионными проверками,
два Windows CI зелёные, следующий `cc review` на `claude-opus-5` не оставляет
actionable findings.

## Обязательные проверки и review gates

- Parser: malformed timestamps, `start >= end`, BOM/Unicode, metadata ambiguity, limits, SRT/VTT fixtures и round-trip.
- Compiler: `[start,end)` boundaries, locale decimal separator, chunks/labels, escaping и non-zero-start timeline.
- Lifecycle: seek/pause/speed/chapter, early interval guard, filter loss и весь cancellation/race matrix.
- Data safety: temp/replace/backup failures, отмена dialog и exception isolation.
- Для parser, compiler и media lifecycle запускать полный Claude Opus 5 review через `cc`.
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
| `CS8752` in new draft-limit test | Used target-typed `new()` as the sole argument of a `params` call | Named `CensorInterval` explicitly; production code was unaffected |
| PowerShell parser rejected `15_000` in Windows audio smoke | Used a C#-style digit separator in a PowerShell numeric literal | Replaced it with `15000`; packaging and pinned Inno had already passed |
| `mpvnet.com` audio smoke timed out on `film-balanced` | Infinite `anullsrc` left mpv.net in idle state after the requested length | Made the lavfi source finite and set `idle=no`, `keep-open=no`; timeout now preserves console output |
| PR audio smoke passed Film then timed out on Anime while push-run passed all presets | mpv.net defaults to `process-instance=single`, so consecutive smoke processes could race through single-instance forwarding | Added the documented `--process-instance=multi` option to isolate every preset run |
| Push audio smoke still timed out nondeterministically after process isolation | The WinForms EOF path depends on ordering of separate `end-file` and `playlist-pos` events | Switched the pinned mpv.net smoke to its built-in headless `--o=` event loop and supplied a complete two-second adaptive-analysis window |
| Local loader smoke requires `Microsoft.WindowsDesktop.App` | macOS can compile the Windows target but cannot execute its WinForms host | Keep loader execution as a required `windows-latest` CI gate; local Release build still verifies compilation |
| `compileReferenceSha256` passed on macOS but failed Windows CI `30669608246` | `libmpvnet.dll` is not bit-identical across the two build platforms | Removed the misleading cross-platform artifact pin; exact source commit and clean checkout remain enforced |
