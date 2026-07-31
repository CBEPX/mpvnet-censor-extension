# Progress

## 2026-07-31

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

## Следующий шаг

Опубликовать UI commit и выполнить `docs/WINDOWS_PHASE0.md` на физической Windows-машине; без этого `gblur`/timeline/GPU runtime не считается доказанным.
