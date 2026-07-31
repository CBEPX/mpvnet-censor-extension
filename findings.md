# Findings

## Источники

- `docs/TZ.md` — нормативная Windows P0 спецификация v1.3.1.
- `docs/adr/ADR-003-session-scoped-schedules-no-database.md` — принятое решение о session-scoped расписаниях без БД.
- `tests/fixtures/example.censor.txt` — пример schema 1 с metadata и тремя интервалами.

## Подтверждённые сильные стороны

- Полуоткрытые границы `[start_ms, end_ms)` совпадают с `gte(t,start)*lt(t,end)`.
- Exact `.censor.` sidecar не конфликтует с обычными subtitle-файлами.
- Session isolation закреплена в ADR, lifecycle, race tests и merge policy.
- Запрет shell construction и числовой compiler резко сужают injection surface.
- Отказ от SQLite соответствует сценарию одноразового просмотра и удаляет лишние recovery/migration paths.

## Поправки после Claude Code Fable review

1. **Medium:** до основной реализации проверить FFmpeg `t` против mpv `time-pos` на media с ненулевым start time; без доказанного rebasing архитектура не проходит Phase 0.
2. **Low:** развести targets: Core parse/normalize/compile ≤ 250 мс, полный прогретый UI-flow ≤ 500 мс.
3. **Low:** перенести и переименовать пример в `tests/fixtures/example.censor.txt`.
4. **Low:** не оставлять ссылки на отсутствующие ADR-002/TZ v1.2.
5. **Low:** привести размещение документов к структуре из §20.
6. **Low:** убрать `READY_TO_APPLY` и `EDITING` из runtime state machine; editing оставить UI-режимом.
7. **Low:** явно определить blur presets и session-only semantics global offset/save.

## Результат implementation review

- Fable нашёл platform-dependent `AppendLine`; serializer переведён на явный LF и byte-exact test.
- Save-path теперь отвергает multiline metadata и timestamps `>= 100h`, выполняет parse-back до замены файла.
- `File.Copy + File.Move` заменены на `File.Replace(temp, target, backup)`; failure simulation доказывает сохранность старого файла и cleanup temp.
- `gblur` ограничен `sigma 0.01..1024`, `steps 1..6`; числовое форматирование invariant и round-trip.
- Locator больше не рекламирует неподдерживаемые форматы: SRT/WebVTT adapters реализованы и протестированы.
- Lifecycle review обнаружил и закрыл teardown UAF: оба shutdown caller теперь ждут общий completion barrier после drain всех native calls.
- Watchdog ограничивает только три последовательные ошибки. Успешный
  readback сбрасывает счётчик, поэтому разнесённые потери фильтра продолжают
  восстанавливаться без пожизненного лимита.
- Финальный Fable lifecycle verdict: `approve`; остался только Low residual risk, если synchronous mpv filter call зависнет дольше upstream 10-second shutdown timeout.
- UI review подтвердил корректную STA/window и lock architecture; generic broadcast verbs заменены на `censor-*`, чтобы чужой `script-message reload/disable` не мог снять filters.
- Два picker внутри одной media session разделяются cancellation token gates до и после duration confirmation; stale операция не применяет filter и не должна менять итоговый status.
- Повторный Fable UI review дал verdict `solid` без блокирующих дефектов; manual load теперь повышает generation ID, а `Shown` повторно читает state и закрывает startup/shutdown race окна.
- Финальный adversarial Fable pass обнаружил ошибочно размещённый generation increment и stale status после duration dialog; increment перенесён в `StartNewOperation`, а status/OSD publication централизованно проверяет session ID и token.
- Автоматический re-check исправленного diff не завершился из-за лимита Fable до 08:00 Europe/Moscow; локальные analyzers, Release build и 36/36 tests проходят, но это не заменяет физический Windows gate.
- Load/reload теперь использует parse-then-swap: прежний активный filter
  остаётся до успешных parse, compile, apply и readback. При ошибке swap
  восстанавливается предыдущий plan.
- Итоговый Fable-review обнаружил реальный cross-schedule overwrite: окно
  передавало документ без его source path. Save contract теперь передаёт оба
  snapshot-значения, а detached draft и scratch принудительно открывают
  `Сохранить как…`.
- При неудачном apply/recovery pause снимается только после подтверждённого
  нового filtergraph или проверенного rollback. Неотменяемый sidecar-dialog
  теперь закрывается при смене media и по таймауту.
- Installer update сохраняет изменённый `portable_config`; smoke создаёт
  пользовательскую правку и runtime-файл, проверяет их после update и
  отсутствие хвостов после uninstall.

## Главные риски

| Риск | Контроль |
|---|---|
| Timeline `t` не совпадает с `time-pos` | Первый блокирующий эксперимент Phase 0 |
| Schedule A применяется к media B | Session ID + cancellation + readback + race matrix |
| Сторонний script удаляет filter | Reserved labels + watchdog + сохранение user filters |
| Большой expression превышает лимиты | Normalize + chunks + measured limits |
| Сохранение портит schedule | Temp + atomic replace + backup + failure simulation |
| UI callback завершает mpv.net | Общий exception boundary и actionable error |
| Stock loader не разрешает sibling dependency | Single `CensorExtension.dll` + Windows `LoadFile/GetTypes` smoke |
| Неполный соответствующий исходный код bundled `libmpv` | Полный пакет не публикуется; CI отдаёт только extension DLL и project source до фиксации всей статической dependency closure |

## Post-review wave

- `CensorWindow` отправляет устаревший полный `ExtensionSettings`; выбор файла
  создаёт отдельный settings work item и может отменить последующую загрузку.
  Исправление: узкий payload формы и один work item для выбранного пути.
- Выключенный timer watchdog не запрещает observer-driven recovery. Проверка
  `WatchdogEnabled` нужна непосредственно перед восстановлением.
- Installer сохраняет старый `CensorExtension.dll`, потому что весь
  `portable_config` установлен с `onlyifdoesntexist`; same-build smoke этого не
  замечает.
- mpv поддерживает именованные `af`; выбранная метка
  `@censor_audio_compression`. Применяются только Film, Anime, Night и
  Adaptive, default `off`; Music и Noisy Source в эту волну не входят.
- `MpvClient.CommandV` только журналирует отрицательный `mpv_error`, поэтому
  success определяется обязательным readback `af`; смена пресета выполняется
  remove/add/verify с восстановлением прежнего графа при ошибке.
- Пользователь переключил итоговый review с Fable на Claude Opus 5 через
  `cc`; post-implementation verdict Opus 5 остаётся обязательным gate.
- Официальный immutable asset Inno Setup 6.7.1 имеет размер 10 619 024 байта
  и SHA-256 `4d11e8050b6185e0d49bd9e8cc661a7a59f44959a621d31d11033124c4e8a7b0`.
  CI больше не зависит от изменяемого Chocolatey-состояния Windows runner.

## Третий Claude Opus 5 review

- Блокирующее замечание: `smoke-installer.ps1` использовал настоящие пути
  `%LOCALAPPDATA%` и завершался удалением данных. Исправление состоит из
  CI-only guard, отказа работать поверх существующих каталогов и явного
  `/DIR`; guard проверяется отдельным шагом workflow.
- Устаревшая `censor-diagnostics` удалена из README.
- `EnsureSavedAudioCompression` больше не удаляет отсутствующую метку, а OSD
  не ждёт занятую цепочку фильтров бесконечно.
- При неизменном наборе интервалов окно заново выводит runtime diagnostics из
  кеша, не перестраивая grid. Путь `UiState.json` больше не выводится из пути
  recovery через nullable parent traversal.
- `_filterGate` намеренно не освобождается в `Dispose`: callbacks уже могли
  встать в очередь до `_stopping` и должны безопасно увидеть остановку. Текущий
  operation token освобождается под `_stateLock`.
- Отдельный apply/rollback state machine отложен до воспроизводимого дефекта
  или второй реализации. Node.js сохранён как минимальный читатель единого
  `deps.lock.json`.

## Четвёртый Claude Opus 5 review

- Смена blur удаляла `_pendingSchedule`, поэтому загруженное расписание и
  строки редактора исчезали до применения. Pending state теперь сохраняется и
  получает plan с выбранным blur; при staged schedule active reapply не
  запускается, чтобы не конкурировать с кнопкой «Применить».
- `_sourceIntervals` одновременно использовался как draft source и runtime
  identity. Добавлен отдельный runtime identity: dirty draft перестраивается
  один раз при реальной смене документа, но не на каждом watchdog tick.
- Любой комментарий с двоеточием считался неизвестной metadata. Warning теперь
  создаётся только для ключа `[a-z][a-z0-9-]*`; остальные комментарии
  сохраняются без шума.
- Immutable settings references читались и записывались из разных потоков.
  Поля опубликованы через `volatile`; это исключает устаревшие ссылки без
  дополнительной блокировки долгих parse/compile paths.

## Пятый Claude Opus 5 review

- Явный Save As в SRT/VTT терял metadata, но очищал dirty/recovery state.
  Эти форматы теперь считаются экспортом копии: перед записью показывается
  предупреждение, а канонический источник и состояние черновика не меняются.
- `Normalize` принудительно записывал `Schema = 1`, поэтому неизвестный
  `settings.json` мог быть перезаписан старой схемой. Версия сохраняется, и
  штатный validator блокирует durable write.
- Несоседний merge создавал перекрывающийся порядок интервалов. Операция теперь
  принимает только непрерывный диапазон выбранных строк.
- Счётчики watchdog имели два независимых lock-домена; все изменения переведены
  на `Interlocked`. Поздний `FileLoaded` проверяет stop до чтения operation
  token, а тяжёлая компиляция pending blur plan вынесена из `_stateLock`.
- Настроенные лимиты применяются и к SRT/VTT, diagnostics переходят из pending
  в active schedule, picker показывает скрытое окно, а redactor отличает дробь
  `3 / 4` от Unix-пути.
- Validation cache удалён как лишнее состояние. PowerShell audio smoke получает
  реальные строки пресетов от .NET 10 helper и больше не зависит от CLR pwsh.

## Граница доказательств

- Физический Windows smoke на `440269a` доказал extension API, filter timeline,
  preset switching, watchdog, session races и 1080p30/60.
- Новый редактор и полный portable/installer прошли Windows CI, включая
  install/update/uninstall smoke. Работа UI внутри mpv.net и поведение точного
  пакета на реальной Windows-машине будут доказаны отдельной физической
  проверкой текущего SHA.

## Environment discovery

- Рабочая машина: macOS 26.5.1 arm64; установлен .NET SDK 10.0.302; PowerShell, Wine и Inno Setup отсутствуют.
- Официальный mpv.net release на старте реализации: `v7.1.2.0` от 2026-01-09.
- Upstream `main` на `ef45baecbdd8e0a249eca9a621fe608143f75c4b` использует `net10.0` и `net10.0-windows7.0`; Windows host включает WPF и WinForms.
- Upstream ExampleExtension всё ещё указывает `net6.0`, поэтому его target framework не является источником истины для нового extension.
- Официальный .NET SDK разрешает cross-build WinForms/WPF на macOS при `EnableWindowsTargeting=true`.
- Глобальный trace-mcp закреплён за `/Users/g.mehrenin/project/infra` и не отражает этот репозиторий; для `player` используется активированный Serena до отдельной перенастройки trace root.
- `CensorExtension.dll` cross-build подтверждён на macOS с `EnableWindowsTargeting=true`; это compile evidence, а не Windows runtime evidence.
- Первый физический Windows smoke на `58004d4` дал blocking fail: stock mpv.net дошёл до `Assembly.LoadFile/GetTypes`, но не разрешил лежащий рядом `Censor.Core.dll`.
- Packaging contract изменён на одну `CensorExtension.dll`; Core продолжает тестироваться отдельно, но его исходники компилируются внутрь extension assembly.
