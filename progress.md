# Progress

## 2026-08-04

- Пользователь подтвердил физическую Windows-проверку exact head `d5456e0`;
  PR #1 переведён в ready, CI и merge state были зелёными.
- Полный Claude Code review через Opus 5/xhigh не нашёл блокирующего runtime
  дефекта и оставил пять замечаний по стоимости редактора, записи и упаковке.
- Проверка кода и официального SPDX 2.3 подтвердила три практических изменения;
  SPDX-required и `MarkSaved` findings классифицированы как ложные без изменения
  безопасной семантики.
- Начата финальная remediation-волна: lightweight render validation,
  single-serialization writer, явные SBOM-поля, self-contained source archive
  и узкие регрессионные проверки.
- Реализация завершена минимальным diff: full validation остаётся на
  Apply/Save, writer проверяет один сериализованный текст, stale `MarkSaved`
  contract закреплён loader-smoke, source archive и SBOM verifier усилены.
- Локальные gates: `160/160` Core-тестов, Release build без предупреждений и
  ошибок, `dotnet format --verify-no-changes` и `git diff --check` — PASS.
- Delta-review Claude Opus 5/xhigh для `d5456e0..508ab7b` дал verdict
  `ship it` без correctness-дефектов. Два полезных minor замечания закрываются
  тестами size/serialization writer branches и удалением тавтологичного assert;
  size-specific exception остаётся подробностью журнала.
- После minor test cleanup повторно проходят `162/162` Core-теста, Release
  build без warnings/errors, format verify и diff-check.
- Повторный Opus 5/xhigh review всего `d5456e0..ba1fa80` подтвердил отсутствие
  correctness/data-loss дефектов и нашёл одну редкую UI-гонку: отложенный
  рендер мог скрыть document-level ошибку размера.
- Ошибка теперь остаётся в устойчивом сообщении редактора; writer сохраняет
  подробность неудачного повторного разбора, а Core и Windows loader-smoke
  проверяют обе ветки. Локально снова зелёные `162/162`, Release build,
  format verify и diff-check.
- Следующий Opus-pass поймал до CI case-sensitive JSON fixture и потерю той же
  диагностики при полном рендере. Fixture использует явные JSON options, а
  document-level ошибки привязаны к immutable revision черновика, сохраняются
  в notice и списке предупреждений и сбрасываются после изменения документа
  или настроек. Локальные gates снова зелёные.
- Финальный Opus-pass подтвердил безопасность writer и packaging, но показал,
  что cache усложняет обычный сценарий 10–20 сцен и ухудшает live-feedback.
  Cache удалён; row/full render снова выполняют полную проверку. SPDX verifier
  принимает любое непустое license value, UTF-8 test различает байты и символы,
  а текст size-limit имеет один источник истины.
- Контрольный pass нашёл PowerShell-ловушку `@($null).Count == 1` и stale notice
  после исправления строки. Verifier фильтрует null/пустые license-поля до
  подсчёта, row render очищает устаревшее сообщение, а writer использует
  один parse-back для size/round-trip и одинаково сохраняет detail ошибок.
- Последний Opus-pass дал verdict без blocking correctness/data-loss defects.
  Minor UI-hygiene закрыта одним provenance-флагом: validation notice переживает
  row/full render, меняется вместе с ошибкой и исчезает после исправления.
  Loader-smoke проверяет document-level и row-level ветки; лишние SPDX и writer
  условия удалены. Локальные `162/162`, build, format и diff-check зелёные.
- Фокусный review `ac091f0..3f98860` снова не нашёл blocking defects. Закрыт
  последний test gap: row-level notice проверяется до и после исправления,
  остаётся видимым при detached runtime refresh и не повторяет OSD. В writer
  возвращён явный marker внутреннего round-trip failure.

## 2026-08-01

- Шестнадцатый полный Opus 5 review дал verdict `solid, ship-able`; packaging и
  concurrency замечаний не получили. Он запросил явный контракт версии
  mpv.net, дополнительное покрытие чистой extension-логики и несколько
  bounded-исправлений.
- README теперь поддерживает ровно проверенный mpv.net `7.1.2.0`. Диалог
  несовпадения длительности получил общий 30-секундный timeout, а смена blur
  preset больше не использует бесконечный optimistic retry.
- Разбор `censor-*`, выбор TXT/SRT/WebVTT parser и защита текста исключений
  вынесены в чистый Core и покрыты тестами. Пределы окна и timestamp имеют один
  источник истины.
- Bounded JSONL-канал считает вытесненные записи и напрямую пишет
  `log-events-dropped` с их количеством. Локально: Release без warnings,
  111/111 тестов, format и diff-check — PASS.
- Пятнадцатый полный `cc review` на `claude-opus-5` подтвердил отсутствие
  потери данных и deadlock, но нашёл ложноположительную проверку `vf`: прежний
  фильтр с тем же label мог быть принят за новый.
- Readback теперь сверяет полный текст каждого ожидаемого filter и отклоняет
  дубликаты и лишние labels из пространства `@censor_blur_*`. Очистка удаляет
  все обнаруженные labels расширения, а runtime-smoke подменяет граф тем же
  label и проверяет восстановление точных параметров `40/2`.
- Та же общая проверка закрывает одинаковый риск у аудиопресетов: OSD сообщает
  успех только после подтверждения точного выбранного `af`, а не одного label.
- Первые Windows run `30682415070` и `30682416136` подтвердили все gates до
  runtime и правильно отклонили буквальное сравнение исходного `lavfi=[…]` с
  каноническим readback mpv. Диагностический run `30682633359` зафиксировал
  формат `lavfi=graph=%N%GRAPH`; временный вывод readback после этого удалён.
- Core теперь детерминированно строит штатную length-quoted форму по длине
  UTF-8 graph. Она сохраняет точную проверку параметров и выражения без разбора
  произвольного FFmpeg-синтаксиса.
- Закрыты четыре узких замечания: runtime diagnostics не исчезают без
  черновика, несвязанный черновик показывает свой источник, pause recovery
  охватывает исключение подтверждения паузы, а смена watchdog-настроек безопасна
  при параллельном shutdown. Локально: Release build без warnings, 91/91 тест
  и `dotnet format --verify-no-changes` — PASS.
- Финальный head восьмого цикла `d8cf0dc` подтверждён Windows CI push
  `30677800019` и PR `30677801518`; оба прошли закреплённый compile SHA,
  build/test/loader, package, аудиографы и installer smoke.
- Девятый полный `cc review` на `claude-opus-5` нашёл рассинхронизацию blur
  preset при ошибке apply, несогласованный settings snapshot, отрицательный
  seek/editor timestamp, неверную ширину mpv flag и пробел в runtime-тестах.
- Владелец продукта уточнил реальный профиль: 10–20 сцен на фильм. Виртуальный
  режим таблицы для 10 000 строк не добавляется; это число остаётся защитным
  пределом входных данных.
- Blur сохраняется после подтверждённого apply и откатывает settings/UI при
  ошибке. Загрузка расписания использует один снимок; отрицательная метка
  времени черновика и переход к нулю согласованы; флаг mpv передаётся как `int`.
- Добавлена Windows CI-проверка настоящего runtime расширения: применение
  sidecar, чтение `vf`, владение паузой, восстановление watchdog, Disable и
  сохранение чужого фильтра.
- Первый запуск проверки в push `30678742565` и PR `30678743907` обнаружил
  гонку самого теста: файл из командной строки загружался до подписки extension
  на lifecycle. Smoke теперь запускает пустой mpv.net и загружает файл через
  IPC после подключения.
- Runtime-smoke на `6b582b5` прошёл в push `30678953180` и PR `30678955090`,
  но созданный им `%LOCALAPPDATA%\CensorPlayer` корректно заблокировал
  installer-smoke. Подмена переменной окружения не изменила Windows Known
  Folder в runs `30679429841`/`30679431187`, поэтому защищённый installer-smoke
  теперь выполняется первым, а runtime-smoke — последним в CI и release;
  защитный guard установщика не ослаблен.
- Десятый полный `cc review` на `claude-opus-5` признал ветку ship-able и нашёл
  мёртвый `logging.level`, потерю OSD при занятом gate, отсутствие окна для
  duration-confirmation из `censor-load` и несколько малых контрактов.
- Уровень журнала теперь применяется, OSD получает одну bounded deferred
  попытку, duration-dialog открывает штатное окно, а redactor поддерживает
  русские кавычки. Синхронная запись и обычная таблица сохранены для реальных
  10–20 сцен без ненужной виртуализации.
- Локально: закреплённый restore SHA и его failure path, locked restore,
  Release build без предупреждений, 82/82 теста, format, JSON/YAML и shell
  syntax — PASS. PowerShell/runtime/installer остаются Windows CI evidence.
- Одиннадцатый полный `cc review` на `claude-opus-5` дал verdict `Solid`, без
  blocking defects. Исправлены гонка smoke при быстром push-recovery, тихое
  stale-save, нулевая позиция новой сцены, UI-thread OSD wait, сообщение
  future-schema и утечка parser message без diagnostics consent.
- Commit `f0b4260` подтверждён Windows push `30679852270` и PR
  `30679853687`: installer выполняется на чистом runner state, затем настоящий
  mpv.net runtime применяет, восстанавливает и отключает blur.
- Двенадцатый полный Opus-review признал ветку `ship-able`. Практические
  замечания закрыты точным pre-repair backup, UTF-8 UX, честным watchdog OSD,
  variable-hour round-trip, ComboBox refresh и тестируемой save policy.
  Виртуализация и native scan не добавлены для обычных 10–20 сцен.
- Тринадцатый полный Opus-review не нашёл критических дефектов. Закрыты UX
  перезагрузки таймингов и безопасной паузы, пути переведены на HMAC-SHA-256 с
  локальным ключом, URL сохранены, а JSON-настройки читаются без учёта регистра.
- Локально после исправлений: Release build без предупреждений и 86/86 Core
  тестов — PASS; Windows runtime и installer остаются следующей границей CI.
- На `900bf6e` PR-run `30681082928` прошёл полностью, а push-run
  `30681082042` воспроизвёл startup race самого runtime-smoke. Вместо секунды
  ожидания тест теперь получает явный ready-marker extension до `loadfile`.
- Handshake-fix `b432a2c` подтверждён двумя полными Windows CI: push
  `30681416995` и PR `30681418345` прошли installer и настоящий runtime smoke.
- Четырнадцатый Opus-review подтвердил продуктовый pipeline и нашёл
  path-dependent compile hash. Две сборки из разных каталогов воспроизвели
  проблему; детерминированные параметры дали общий SHA `d796740a…a945`.
- Закрыты четыре малых finding: shutdown exception boundary, распространённые
  варианты таймкодов SRT/VTT, понятный staged blur UX и ранняя проверка draft.
  Локально Release build без предупреждений и 88/88 тестов — PASS.
- Commit `5cbef7b` подтверждён двумя полными Windows CI: push `30676321729` и
  PR `30676323633`. Оба прошли 78 тестов, loader, package, аудиографы,
  release verification и installer smoke.
- Восьмой полный `cc review` на `claude-opus-5` признал ветку ship-able и не
  нашёл blocker. Оставлены UI-scaling, future-schema repair, pause fail-open,
  compile hash, log I/O и три небольших замечания.
- Массовый grid render приостанавливает layout, программный offset защищён от
  событий, pause требует readback, а восстановленный `vf` снимает `WARNING`.
- На вкладке диагностики добавлена явная перезапись future-schema с
  `settings.json.pre-repair`.
  Логи используют один дневной handle; schedule сериализуется только с consent;
  аварийные temp-файлы скрываются на Windows и очищаются по возрасту.
- Lock reader переведён с Node.js на file-based .NET 10 helper. Старые
  platform-specific hashes были подтверждены CI, но позже заменены общим
  воспроизводимым hash после нормализации build paths.
- Локально: Release build без предупреждений и 79/79 тестов — PASS; полный
  набор format/restore/syntax проверок выполняется перед коммитом.
- Commit `f73be51` подтверждён двумя полными Windows CI: push `30675372654` и
  PR `30675373590`. Оба прошли build, 78 тестов, stock loader, package,
  аудиографы, release verification и installer smoke.
- Седьмой полный `cc review` на `claude-opus-5` не нашёл критических дефектов,
  но выявил сохранение старого blur после пустого плана, неполное маскирование
  путей, повторное чтение duration и несколько UI/настроечных шероховатостей.
- Пустой план теперь выполняет общий teardown. Duration сравнивается со
  снимком media session; локальный лог и диагностический ZIP используют один
  redactor, сохраняющий код ошибки и номер строки после пути.
- Settings записываются только при изменении каталога и после проверки фильма.
  Ручной импорт SRT/VTT требует подтверждения; reconciliation обновляет подпись
  черновика, а sidecar-тест проверяет все три канонических расширения.
- Локально: Release build без предупреждений, 78/78 тестов и format — PASS.
- Commit `cdefe84` подтверждён двумя полными Windows CI: push `30674187416` и
  PR `30674252331`. Оба прошли build, 67 тестов, stock loader, package, новый
  .NET audio helper, четыре аудиографа и installer smoke.
- Шестой полный `cc review` на `claude-opus-5` не нашёл blocker, но выявил
  незакрытые UI exceptions, сохранение с неподдерживаемым расширением и merge
  двух далёких по времени сцен.
- UI actions теперь проходят через `RunSafely`. Общий `ScheduleFileKinds`
  синхронизирует loader, picker, drag-and-drop, sidecar, import и export;
  неподдерживаемый путь не записывается.
- Merge разрешён только без временного разрыва. Чистая функция reconciliation
  покрывает все ветки сохранения dirty draft; локально проходят 78/78 тестов.
- `FilterPlan` хранит фактический blur, неизвестный audio preset откатывается
  на `off`, diagnostics берёт только `censor-extension-*.log`, а future-schema
  получает понятное OSD при отказе записи.
- Restore script отдельно сообщает об ошибке чтения `deps.lock.json`.
  Валидация документа больше не создаёт throwaway draft; политика
  воспроизводимого compile-reference записана в lock file.
- Пятый полный `cc review` на `claude-opus-5` подтвердил runtime/package
  архитектуру и нашёл два риска потери данных: lossy SRT/VTT считался обычным
  сохранением, а future-schema настроек незаметно понижалась до schema 1.
- SRT/VTT теперь экспортируются только после предупреждения и не меняют
  dirty/recovery state. Future-schema сохраняется в памяти без понижения, а
  общий validator запрещает перезапись неизвестного формата.
- Merge ограничен соседними строками; subtitle import получает настроенные
  лимиты, active schedule хранит parse diagnostics, а `/` в записи `3 / 4`
  больше не принимается за Unix-путь.
- Watchdog counters переведены на `Interlocked`; поздний `FileLoaded` не читает
  disposed token, blur plan компилируется вне state lock, picker сначала
  показывает окно. Лишний validation cache удалён.
- PowerShell больше не загружает .NET 10 assembly: каталог аудиопресетов
  выдаёт существующий `Censor.LoaderSmoke` helper. Локально Release build без
  предупреждений и 67/67 core-тестов — PASS; Windows runtime smoke ожидает CI.
- Push CI `30671776521` и PR CI `30671778545` на `1383ca9` полностью прошли:
  build, 64 теста, stock loader, package, аудиофильтры, local-safety guard и
  настоящий installer smoke зелёные.
- Четвёртый полный `cc review` на `claude-opus-5` одобрил concurrency,
  filter transactions и packaging, но нашёл четыре merge findings: потерю
  pending schedule при смене blur, повторную перерисовку dirty grid, ложное
  metadata-warning для комментариев с двоеточием и off-lock reads настроек.
- Pending schedule теперь получает новый ticket и blur plan, оставаясь
  `READY TO APPLY`; при staged schedule прежний active graph не запускает
  конкурирующий reapply.
- Окно отдельно помнит последний runtime interval identity и draft source.
  Повторный watchdog warning обновляет сообщения, не очищая таблицу и текущую
  ячейку.
- Parser предупреждает только о metadata-подобных неизвестных ключах. Тест
  закрепляет сохранение `# см. https://example.com` без ложного warning.
- `_settings` и `_blurSettings` публикуются как volatile immutable snapshots;
  Apply после stop не читает disposed token. Убраны dead note fallback и
  неочевидный nullable pattern, добавлены комментарии к fail-closed pause и
  внутренним Core helpers в single-DLL.
- Локально: Release build без предупреждений, 65/65 тестов и format — PASS.
- Третий полный `cc review` на `claude-opus-5` нашёл один блокирующий риск:
  installer smoke мог удалить настоящую локальную установку и данные. Скрипт
  теперь работает только в GitHub Actions, отказывается идти поверх
  существующих каталогов и закрепляет `/DIR` для каждого install/update.
- В CI добавлена исполняемая проверка локального safety guard до настоящего
  installer smoke.
- Аудиопресет больше не вызывает `af remove`, если метки нет. OSD ждёт filter
  gate не дольше 250 мс, поэтому уведомление не блокирует lifecycle.
- Runtime diagnostics обновляются без полной перерисовки таблицы; пути recovery
  и UI state строятся от переданного local-data root. Dispose operation token
  выполняется под `_stateLock`, а filter gate намеренно остаётся доступным для
  уже поставленных в очередь callbacks.
- README очищен от удалённой команды, в Core project пояснена single-DLL
  компиляция. Вынос apply/rollback в отдельный state machine и замена Node.js
  не сделаны: сейчас они добавят код и второй источник сборочной логики, не
  закрывая нового подтверждённого дефекта.
- Локально: Release build без предупреждений, 64/64 теста — PASS. Loader smoke
  собран, но его запуск и installer guard остаются Windows CI evidence.
- Повторный полный `cc review` на `claude-opus-5` после первого remediation
  нашёл case-sensitive preset ID, disk I/O под `_stateLock`, потерю текста
  ошибок диагностики и запись draft до compile, а также семь low findings.
- Аудиопресет теперь канонизируется при загрузке; регрессионный тест проверяет
  `Film-Balanced` → `film-balanced` до создания окна.
- Сохранение настроек сериализовано отдельно от runtime locks, normalize и
  compile выполняются до записи schedule, а диагностический ZIP сохраняет
  сообщение ошибки и хеширует содержащийся в нём абсолютный путь.
- Исправлены mpv bool readback, ложный watchdog success, двойной `.censor` в
  имени, redo и ложный diagnostic hotkey. Event dispatch и atomic text wrapper
  упрощены, общий temporary-directory helper больше не дублируется.
- Windows CI `30669608246` доказал, что прежняя сборка не была bit-identical
  между платформами. Позже выяснилась причина — debug/path data; общий binary
  pin возвращён только после двух сборок из разных каталогов.
- `deps.lock.json` читается закреплённым .NET SDK без отдельного Node.js.
- Large-draft validation cache и zero-copy `Freeze` сохранены как защита от
  чрезмерного входного файла. Целевая UI-нагрузка — обычные 10–20 сцен фильма.
- Первый полный `cc review` на `claude-opus-5` одобрил session/revision и
  filter transaction design, но нашёл шесть edge findings: privacy ZIP,
  отказ логирования, shutdown wait, stale recovery, large-draft UI и очередь
  окна.
- Диагностический ZIP теперь заново санитизирует JSONL: значения `*path` и
  `*error` хешируются, невалидные строки заменяются безопасным маркером.
- Недоступный каталог журналов больше не мешает загрузке расширения; ожидание
  filter gate при shutdown ограничено пятью секундами.
- Recovery синхронизирован с `IsDirty`, отклонённые настройки возвращаются в
  форму, очередь действий окна ограничена 32 элементами и очищается при stop.
- `ScheduleDraft` хранит замороженные снимки, переиспользует validation result
  и не клонирует все интервалы при чтении. Cell edit и шаг ±100 мс обновляют
  одну строку без повторной сериализации всего расписания.
- Пять atomic writers сведены в `AtomicFile`; убраны пустой `T(...)`,
  повторяющийся event dispatch и отдельный `ScheduleOptionsResolver`.
- Локально: Release build без предупреждений, 64/64 теста, format, JSON/YAML и
  shell syntax — PASS. Loader smoke исполняется только в Windows CI, потому
  что macOS не содержит `Microsoft.WindowsDesktop.App`.

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
- Run `30661482265` снова подтвердил package и дошёл до mpv runtime, но
  бесконечный `anullsrc` оставил процесс в idle. Fixture сделан конечным;
  добавлены `idle=no`, `keep-open=no` и вывод лога при timeout.
- Push-run `30661744664` полностью прошёл, включая четыре аудиографа и новый
  installer smoke. Параллельный PR-run `30661747438` выявил single-instance
  race между последовательными `mpvnet.com`; smoke переведён в штатный
  `process-instance=multi` и должен пройти повторно в обоих событиях.
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

Завершить локальные gates финальной remediation-волны, выполнить delta-review
Claude Opus 5, exact-head Windows CI и squash merge PR #1. Физический smoke не
повторять без расширения diff за пределы validation/writer/package metadata.
Полный portable/installer не публикуется до закрытия issue #4.
