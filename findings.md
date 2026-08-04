# Findings

## Источники

- `docs/TZ.md` — нормативная Windows P0 спецификация v1.3.2.
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

## Шестой Claude Opus 5 review

- `BeginInvoke` защищал только постановку делегата в очередь, а не его тело.
  `InvokeWindow`, queued actions и drain теперь вызывают существующий
  `RunSafely`; сбой UI-команды не закрывает окно.
- Save dialog принимал `movie.txt`, хотя loader такой файл отвергал. Единый
  `ScheduleFileKinds` используется во всех путях, а SaveDraft повторно
  проверяет расширение перед записью.
- Соседние строки могли быть далеки по времени. Merge теперь отвергает любой
  неявный разрыв; регрессия `30:00` + `05:00` закреплена тестом.
- Решение `UpdateState` о dirty/source/runtime draft вынесено в чистый
  `DraftReconciliation` и полностью покрыто таблицей тестов.
- Фактический blur хранится в `FilterPlan`, поэтому apply log не зависит от
  конкурентной смены настройки. Неизвестный audio preset безопасно выбирает
  `off`.
- Диагностический ZIP получает только логи, принадлежащие расширению. Restore
  script проверяет Node.js и результат чтения lock file; compile-reference
  policy явно записана рядом с source pin.
- Статическая валидация `ScheduleDocument` убрала лишнее копирование списков.
  Future-schema по-прежнему не перезаписывается, но причина теперь видна в OSD.

## Седьмой Claude Opus 5 review

- Пустое или полностью нормализованное за границы фильма расписание оставляло
  прежний blur активным. Все такие пути теперь используют общий teardown и
  очищают active/pending state вместе с фильтрами.
- Проверка длительности повторно читала нестабильное свойство mpv. Сравнение
  использует значение, уже зафиксированное при загрузке текущей media session.
- Локальный журнал маскировал только известные пути, а ZIP мог захватывать
  диагностический хвост вместе с путём. Общий redactor обрабатывает любые
  абсолютные пути и сохраняет `(code N)` и `:line N`; это закреплено тестом.
- Каталог расписаний сохраняется только после подтверждения открытого фильма и
  только при реальном изменении. Состояние черновика обновляется без лишнего
  `MarkSaved`, а ручной импорт SRT/VTT требует подтверждения.
- Подтверждено по закреплённому исходнику mpv.net: `CommandV` журналирует ошибку
  команды и не бросает исключение, поэтому удаление нескольких labels уже
  продолжает обработку и проверяется последующим чтением `vf`.
- Удалены недостижимый лимит 50 фильтров и дублирующая ветвь нормализации;
  sidecar-тест теперь действительно проверяет TXT → SRT → VTT и исключает
  обычный `Film.srt`.

## Восьмой Claude Opus 5 review

- Массовое заполнение `DataGridView` выполнялось с layout после каждой строки.
  Полная перерисовка теперь обёрнута в `SuspendLayout`/`ResumeLayout`, а
  read-only обновление сводки защищено общим `_rendering` guard.
- Если mpv не сообщал `pause`, замена графа продолжалась без защиты. Теперь
  чтение, установка и readback паузы обязательны; ошибка прерывает операцию и
  оставляет проверенный граф или паузу до безопасного восстановления.
- Future-schema намеренно не понижается автоматически, но теперь имеет явный
  repair-flow в UI: известные настройки сохраняются как schema 1, неизвестные
  поля удаляются, исходник остаётся в `settings.json.pre-repair`.
- Compile reference получает воспроизводимый общий SHA-256 на macOS arm64 и
  Windows x64. Маленькое file-based .NET-приложение читает lock file и считает
  хеш, поэтому отдельная зависимость от Node.js больше не нужна.
- `ExtensionLog` держит дневной файл открытым, сбрасывает запись для live-export
  и переоткрывает файл при смене UTC-даты или ошибке.
- Диагностика сериализует расписание только с согласием. Watchdog снимает
  `WARNING`, когда labels снова на месте. Atomic writer скрывает временный файл
  на Windows и удаляет оставшиеся более суток temp-файлы при следующей записи.
- Замечание о повторной компиляции Core в single-DLL отмечено reviewer как
  архитектурный компромисс, а не дефект; loader smoke и двойная сборка остаются
  обязательными gates.

## Девятый Claude Opus 5 review

- Целевая нагрузка редактора уточнена владельцем продукта: обычно 10–20 сцен на
  фильм. Поэтому `DataGridView` не переводится в сложный virtual mode, а лимит
  10 000 остаётся только защитой от чрезмерного входного файла. Из Phase 0
  удалено обещание интерактивной работы таблицы на 10 000 строк.
- Новый пресет размытия больше не записывается на диск до подтверждённого чтения
  `vf`. При ошибке рабочий граф откатывается существующим транзакционным
  механизмом, а
  `_settings` и ComboBox возвращаются к предыдущему пресету.
- Загрузка расписания использует один неизменяемый снимок настроек. Переход к
  отрицательному интервалу безопасно ищет с нулевой позиции, а редактор снова
  принимает отрицательное время, которое сам показывает для невалидного
  черновика.
- `MPV_FORMAT_FLAG` теперь передаётся нативному API как 32-битный `int`, без
  зависимости от little-endian x64 и обнулённой старшей половины `long`.
- Ручной импорт обычных `.srt`/`.vtt` явно отделён комментарием от строгого
  auto-discovery; packaging разрешает сам каталог `artifacts`, а не только его
  подкаталоги.
- Вместо крупного слоя имитации Windows CI запускает настоящий закреплённый mpv.net:
  автоматически применяет sidecar, проверяет labeled `vf`, pause ownership,
  watchdog recovery, Disable и сохранение пользовательского фильтра.

## Десятый Claude Opus 5 review

- `logging.level` валидировался и сохранялся, но не влиял на журнал. Теперь
  `ExtensionLog` фильтрует события по уровню, а ошибки записываются как `error`.
- OSD ждал общий filter gate 250 мс и молча терял сообщение. При занятости
  выполняется одна отложенная попытка с тем же gate и пределом пять секунд.
- Ручной `censor-load` с несовпадающей длительностью без открытого окна всегда
  отклонял файл. Подтверждение теперь ставится в штатную очередь UI, открывает
  окно и отменяется при смене фильма.
- Русские кавычки `«…»` добавлены в redactor абсолютных путей; fallback
  аудиопресета использует гарантированный `Off`, а README поясняет `.bak` для
  расписаний и субтитров.
- Синхронная надёжная запись редактора и обычный `DataGridView` оставлены
  сознательно: реальная нагрузка — 10–20 сцен, поэтому фоновые fsync и virtual
  mode создали бы больше гонок и кода без пользовательской пользы.
- Первый реальный runtime-smoke прошёл, но оставил `%LOCALAPPDATA%\CensorPlayer`
  и корректно заблокировал следующий installer-smoke. Runtime теперь получает
  последнюю позицию после installer-smoke: Windows Known Folder не подменился
  переменной окружения, а защитный отказ установщика не ослаблен. Этот же
  порядок добавлен в release workflow.

## Одиннадцатый Claude Opus 5 review

- Попытка увидеть краткое отсутствие blur label конкурировала с push-recovery
  самого расширения. Smoke доверяет успешной команде `vf remove` и проверяет
  только обязательное конечное восстановление.
- Запись расписания могла завершиться после смены operation и не показать
  результат. Файл теперь всегда подтверждается OSD, но stale ticket не меняет
  черновик и runtime нового расписания.
- Кнопка добавления больше не подставляет `00:00:00`, когда mpv занят; UI
  объясняет, что операцию нужно повторить. Аварийный OSD из UI callback
  отправляется через очередь и не удерживает интерфейс 250 мс.
- Сообщение о future-schema едино для всех путей сохранения. На границе ZIP
  parser messages заменяются безопасным текстом, если пользователь не разрешил
  включить расписание; поведение закреплено Core-тестом.
- Минимум `UiStateStore` согласован с окном `760×520`, мёртвый PowerShell
  callback удалён. Скрытие незавершённого atomic temp оставлено сознательно:
  оно не влияет на формат файла и уменьшает видимость crash-residue в Explorer.
- Структурный слой fake-mpv не добавлен: критический lifecycle проверяет
  настоящий закреплённый mpv.net, а dual-compile Core, loader и аудиографы
  остаются отдельными Windows gates.

## Двенадцатый Claude Opus 5 review

- Verdict повторного полного прохода на `f0b4260` — `ship-able`; оба Windows CI
  `30679852270` и `30679853687` зелёные, включая installer и runtime smoke.
- Save reconciliation вынесен в уже существующий `DraftReconciliation` и
  покрыт всеми состояниями без создания fake-mpv слоя. Runtime I/O остаётся в
  extension, чистое решение теперь проверяется Core-тестом.
- Strict UTF-8 сохранён, но `DecoderFallbackException` даёт понятную инструкцию
  пересохранить файл. Draft parser читает собственное отображение времени после
  99 часов, а обычный `# note:` сохраняется без ложного metadata warning.
- Явный repair сначала сохраняет точные исходные байты в
  `settings.json.pre-repair`. Последующие обычные сохранения меняют только
  rolling `.bak`; форма после SetSettings синхронизирует blur и audio ComboBox.
- Сообщение о сбое чтения `vf` теперь честно говорит о продолжении попыток.
  Normalizer возвращает массив и не публикует внутренний mutable list.
- Замечания о предельном объёме grid и большом `vf` относятся к защитному
  входному пределу, а не пользовательской нагрузке. Для обычных 10–20 сцен сохранён
  простой код; места возможного перехода к VirtualMode/native scan отмечены
  `ponytail`-комментариями.

## Тринадцатый Claude Opus 5 review

- Критических дефектов и риска потери данных не найдено. Изменение трёх
  параметров нормализации теперь честно требует перезагрузить уже открытое
  расписание; сообщение об ошибке применения объясняет безопасную паузу и
  способ снять её через меню.
- Обычный SHA-256 пути заменён на HMAC-SHA-256 с локальным ключом установки.
  Ключ не попадает в диагностический архив, а прежние SHA-псевдонимы в JSONL
  повторно защищаются при экспорте.
- URL больше не распознаётся как Unix-путь. Имена полей `settings.json`
  читаются без учёта регистра, а размер одного filter chunk явно связан с
  отдельным входным пределом расписания.
- `SemaphoreSlim` намеренно живёт до конца процесса из-за возможных поздних
  callbacks mpv.net; пользовательский blur-пресет уже устраняет дубли через
  сравнение `BlurSettings`, поэтому эти замечания не потребовали нового кода.
- PR-run `30681082928` прошёл, а push-run `30681082042` иногда загружал media
  до подписки extension на lifecycle. Фиксированная задержка удалена: runtime
  smoke ждёт `user-data/censor/ready`, опубликованный после полной инициализации.

## Четырнадцатый Claude Opus 5 review

- Единственный высокий finding подтверждён отдельной сборкой: старый
  `libmpvnet.dll` менял SHA вместе с абсолютным каталогом. `PathMap`, отсутствие
  debug directory и CI build сделали две независимые macOS-сборки
  bit-identical с SHA `d796740a…a945`; тот же хеш теперь обязателен на Windows.
- Shutdown callback проходит через общий `RunSafely`. Отдельно добавлять
  `ObjectDisposedException` в catches не нужно: он наследует
  `InvalidOperationException`, который уже обработан.
- Импорт обычных SRT/WebVTT принимает точку или запятую и 1–3 цифры долей
  секунды. Файл с действительно некорректным cue по-прежнему отклоняется целиком,
  чтобы случайно не пропустить требуемую сцену размытия.
- При staged schedule смена blur preset теперь объясняет необходимость нажать
  «Применить». `SaveDraft` проверяет документ до normalize/compile, а writer
  сохраняет собственную защитную валидацию на границе записи.

## Пятнадцатый Claude Opus 5 review

- Проверка только наличия label была недостаточной: при неудачном удалении
  старый blur-граф с тем же числом chunks мог выглядеть успешно применённым.
  Теперь readback подтверждает полный ожидаемый filter каждого chunk, ровно один
  экземпляр каждого label и отсутствие лишних `@censor_blur_*`.
- Тот же предикат применён к `af`: переключение компрессора не может подтвердить
  новый пресет по label, если mpv оставил прежний аудиограф.
- Нативный строковый readback mpv канонизирует `lavfi=[GRAPH]` в
  `lavfi=graph=%N%GRAPH`, где `N` — длина UTF-8 graph. Сравнение строит эту форму
  напрямую; временный Base64-вывод использовался только в диагностическом CI и
  удалён из итогового кода.
- Очистка собирает все labels зарезервированного пространства из текущего `vf`,
  поэтому stale chunk не остаётся после восстановления или Disable. Windows
  runtime-smoke воспроизводит подмену тем же label и добавляет отдельный stale
  label.
- После удаления черновика список снова показывает runtime diagnostics. При
  сохранении несвязанного черновика заголовок одновременно показывает текущее
  расписание и источник черновика.
- `HoldPauseIfNeeded` включён в общий `try/finally` восстановления, а гонка
  `_watchdog.Change` с Dispose подавляется только во время фактической
  остановки.
- Большое выделение state machine из `Extension` и отдельный fake-mpv слой не
  добавлены: найденный runtime-дефект закрывается одной общей проверкой и
  настоящим закреплённым mpv.net smoke.

## Шестнадцатый Claude Opus 5 review

- Verdict — `solid, ship-able`; дефектов packaging, lock order, fail-closed
  pause и media-session cancellation не найдено. Push `30682819170` и PR
  `30682821047` полностью прошли на точном `db4ffa6`.
- Ручная установка теперь явно ограничена проверенным mpv.net `7.1.2.0`:
  каноническое представление фильтров и native ABI не обещаются для произвольной
  версии проигрывателя.
- `ConfirmDurationMismatchAsync` использует тот же bounded timeout, что выбор
  sidecar. Смена blur preset компилирует обычный короткий план один раз под
  state lock вместо потенциально бесконечного optimistic retry.
- Чистые разбор client message, выбор parser и защита exception вынесены из
  extension и покрыты Core-тестами без fake-mpv. Runtime path остаётся под
  настоящим закреплённым smoke.
- Минимум окна и максимальная метка времени больше не дублируются. Bounded
  журнал явно фиксирует количество вытесненных событий.
- Precompute для большого `vf` не добавлен: обычное короткое расписание требует
  одного сравнения в секунду; граница перехода после измеренного роста уточнена
  в `ponytail`-комментарии.

## Post-acceptance Claude Opus 5 review

- Блокирующих correctness-дефектов не найдено. Подтверждены лишний полный
  round-trip при перерисовке, повторная сериализация в writer и отсутствие
  `.gitattributes` в project-source archive.
- UI остаётся рассчитан на обычные 10–20 сцен: VirtualMode и оптимизация под
  защитный предел 10 000 строк не добавляются. Полная проверка размера и
  round-trip переносится на границу Apply/Save.
- Замечание о SPDX оказалось основано на старом контракте: в SPDX 2.3
  `licenseInfoFromFiles` и `licenseInfoInFiles` необязательны, а отсутствие
  означает `NOASSERTION`. Поля выводятся явно только для совместимости.
- Раннее обновление `LastScheduleDirectory` в `MarkSaved` намеренно: Extension
  уже записал файл и настройки, а false означает только stale UI snapshot.
  Перенос присваивания после guard оставил бы picker со старым каталогом.
- Большой вынос ticket/orchestration state machine не входит в closeout PR и
  уже отслеживается post-P0 issue #5.
- Повторный delta-review признал изменения ship-able, но показал, что
  document-level ошибка могла исчезнуть из списка после queued row render.
  Специфичный текст перенесён в уже существующий durable authoring notice;
  тяжёлая валидация на каждом рендере не возвращается.
- Round-trip writer test теперь доказывает точную ветку и её parse detail, а
  Windows loader-smoke закрепляет сохранение сообщения после row render.

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
