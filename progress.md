# Progress

## 2026-07-31

- Принят post-review план: исправления runtime/data/installer и четыре
  аудиопресета с немедленным применением и OSD.
- Начата реализация на чистом `eb606d4`; `MediaSessionCoordinator` решено не
  менять.
- Локально подтверждены mpv `v0.41.0-60-g85bf9f4ff`, FFmpeg
  `N-122394-g272c273d3` и наличие выбранных audio filters.
- Итоговый review по указанию пользователя будет выполнен Claude Opus 5 через
  `cc`; Fable больше не является gate этой волны.
- Core post-review wave реализован: 60/60 Release-тестов проходят. Добавлены
  audio preset IDs/graphs, лимиты draft/writer, settings backup + durable
  writes, recovery statuses, durable UiState и UTC invariant logs.
- Runtime/UI wave реализован: узкий payload настроек, точный hotkey timestamp,
  честное состояние активной `vf`, disabled-watchdog guard и независимая
  транзакция `af` с readback, rollback и русским OSD.
- Installer теперь отдельно обновляет DLL, сохраняет пользовательские конфиги
  при update/default uninstall и удаляет данные только по явному согласию или
  `/DELETEUSERDATA=1`. Smoke проверяет stale DLL и оба сценария удаления.
- Inno Setup 6.7.1 закреплён официальными URL, размером и SHA-256; GitHub
  Actions закреплены полными commit SHA. Добавлен Windows smoke всех четырёх
  аудиографов на штатном mpv.net.
- Первый Windows run `30661245085` подтвердил build/tests/loader/package и
  закреплённый Inno, затем нашёл синтаксическую ошибку `15_000` в новом
  PowerShell smoke. Литерал исправлен на `15000`; требуется повторный run.
- README, ТЗ и Windows checklist синхронизированы; строки интерфейса проверены
  через `humanizer-ru`. Локально: 60/60, extension build без предупреждений,
  `dotnet format --verify-no-changes`, JSON/YAML и shell syntax — PASS.

- Прочитаны ТЗ v1.3, пример schedule и ADR-003.
- Инициализирован пустой Git-репозиторий с веткой `main`; исходные документы оставлены untracked.
- Выполнен foreground Claude Code Fable review всего working tree.
- Зафиксированы 1 Medium и 6 Low групп замечаний; `BLOCKER/HIGH` не найдено.
- Созданы `task_plan.md` и `findings.md`; реализация продукта не начиналась.
- Получен итоговый Fable review: `BLOCKER/HIGH` нет; Medium про два источника истины принят.
- Создана feature-ветка `codex/implement-censor-p0`.
- Проверен toolchain: на macOS отсутствует .NET SDK; установка требуется до bootstrap.
- Проверен официальный upstream: mpv.net v7.1.2.0 и .NET 10 targets.
- Установлен .NET SDK 10.0.302 в `/Users/g.mehrenin/.local/share/dotnet` и добавлена ссылка `/opt/homebrew/bin/dotnet`.
- Serena C# LSP повторно не увидел runtime из-за PATH, унаследованного MCP-процессом при старте.
- Portable mpv.net оказался single-file bundle без отдельного `libmpvnet.dll`; compile reference переключён на сборку pinned upstream source.
- Нормативные файлы приведены к TZ v1.3.1 и каноническим путям `docs/`, `docs/adr/`, `tests/fixtures/`.
- Создан solution из `Censor.Core`, `Censor.MpvNet.Extension` и `Censor.Core.Tests`; включены locked restore, analyzers и warnings-as-errors.
- Реализованы TXT parser/serializer, атомарная запись через `File.Replace`, exact sidecar lookup, SRT/WebVTT import/export, нормализация и filter compiler.
- После adversarial Claude Code Fable review устранены mixed-EOL, parse-after-save, timestamp >99h, failure simulation и `gblur` range gaps.
- Реализован session-scoped mpv.net host: lifecycle invalidation, cancellation, serialized `vf` mutations, duration gate и label readback.
- Реализован watchdog: push + polling detection, pause-near-interval, bounded recovery и main-player shutdown barrier до `mpv_destroy`.
- После нескольких узких Fable lifecycle review устранён native teardown UAF; финальный verdict `approve`, `BLOCKER/HIGH/MEDIUM` нет.
- Локальный Release build проходит без warnings; 36/36 Core tests проходят.
- Создан публичный репозиторий `CBEPX/mpvnet-censor-extension`; первый Windows CI run `30596392355` зелёный.
- GitHub Actions обновлены до подтверждённых актуальных major tags; feature-branch CI выполняется далее.
- Feature-branch Windows CI run `30598637241` зелёный за 49 секунд на actions v7/v6/v7.
- Добавлен WinForms tool window: namespaced client messages, picker, drag-and-drop, reload, disable, duration confirmation и read-only interval summary.
- Обычный Claude Code Fable review UI diff выполнен; устранены broadcast collision, stale status, dead window thread и повторный O(n) grid rebuild.
- Повторный Fable UI verdict: `solid`, `BLOCKER/HIGH/MEDIUM` нет; закрыты same-session generation race и потеря state до создания WinForms handle.
- Adversarial Fable re-check нашёл один High и один Medium в generation/dialog publication; оба исправлены общей session-aware проверкой status/OSD.
- Повторный запуск Fable после исправлений остановлен внешним session limit до 08:00 Europe/Moscow; Release analyzers/build и 36/36 tests повторно зелёные.
- Физический Windows smoke artifact `58004d4` подтвердил blocking packaging fail: `Censor.Core.dll` не разрешается stock `Assembly.LoadFile/GetTypes`.
- Extension переведён на single-DLL contract; добавлен Windows loader-smoke, повторяющий точку отказа до публикации artifact.
- Физический Windows retest на `440269a` подтвердил loader, меню/окно,
  переключение `30/2 → 40/2 → 50/3`, сохранение user `vf`, watchdog и
  1080p30/60. Синтетический 4K30 принят как известное ограничение и вынесен в
  issue #3.
- Реализован полный редактор: четыре русские вкладки, grid operations,
  F7/F8, undo/redo, offset, preview, Save/Save As, conflict check и detached
  recovery.
- Каждая media session получает монотонный ID; смена фильма отменяет текущий
  `CancellationTokenSource`. Старый активный filter остаётся до успешного
  swap; preset пересобирается из in-memory intervals.
- Добавлены user settings, privacy-safe JSONL logs, diagnostic ZIP с consent,
  запоминание геометрии и список при нескольких sidecar.
- Добавлены GPL-2.0-only license/notices, изолированный portable, per-user
  Inno installer, SPDX 2.3 SBOM, `SHA256SUMS.txt`, installer smoke и ручной
  package-validation workflow.
- Итоговый Fable-review выявил неполный corresponding-source набор штатного
  `libmpv`. Публикация portable/installer отключена; CI загружает только DLL
  расширения и исходный код проекта до закрытия отдельного issue.
- README закрывает требования issue #2; Windows checklist синхронизирован с
  решением по issue #3.
- Итоговый Fable-review нашёл cross-schedule overwrite, сохранение scratch в
  относительный путь, небезопасное снятие pause, неотменяемый sidecar-dialog и
  потерю `portable_config` при update/uninstall. Все Critical/Major закрыты в
  коде и installer smoke.
- Локально проходят Release build без warnings, 54/54 Core tests,
  `dotnet format --verify-no-changes`, JSON/YAML и shell syntax checks.
- Post-fix Fable запуск упирается во внешний session limit до 13:00 МСК; это
  остаётся review gate и не заменяется локальными проверками.
- Windows CI для `194fe60` прошёл в push-run `30616857713` и PR-run
  `30616859951`: 54/54 теста, stock loader, package/source/checksum/SBOM
  verification и install/update/uninstall smoke зелёные.
- Evidence artifact `8787805777` содержит только DLL расширения, исходный код
  проекта, лицензии и пример `input.conf`; полный runtime не опубликован.
- README выполнил критерии issue #2, issue закрыт. Source-provenance gate для
  полного portable/installer вынесен в issue #4.
- Повторный Fable-review полного branch diff в 11:40 МСК снова получил
  внешний session limit до 13:00 МСК; post-fix verdict ещё не получен.

## Следующий шаг

Зафиксировать и отправить post-review wave, дождаться Windows CI, затем
выполнить полный review Claude Opus 5 через `cc`. После зелёного verdict
физически проверить точный SHA DLL на Windows. PR остаётся draft; полный
portable/installer не публикуется до закрытия issue #4.
