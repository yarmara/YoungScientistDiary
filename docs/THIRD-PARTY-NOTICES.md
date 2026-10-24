# Сторонние библиотеки и уведомления

Исходный код приложения «Дневник молодого ученого», созданный в этом проекте, распространяется по **Apache License 2.0**; полный текст — в [`LICENSE`](../LICENSE). Указанная лицензия не заменяет лицензии сторонних компонентов. Их оригинальные тексты и уведомления сохранены в каталоге [`licenses`](../licenses).

Ниже приведены **все 16 прямых и транзитивных NuGet-пакетов**, разрешённых для текущих проектов .NET 8 при сборке версии 1.0.0. Источник списка — фактический результат `dotnet restore` (`project.assets.json`), а не только три прямые ссылки из `.csproj`. Метаданные каждого исходного пакета (`.nuspec`) сохранены рядом с его лицензией. Файлы `licenses/dependency-inventory.json` и `licenses/source-downloads.json` содержат версии, контрольные суммы пакетов, происхождение текстов и SHA-256 сохранённых файлов.

## Прямые зависимости

| Пакет | Версия | Для чего используется | Лицензия и оригинальный текст |
|---|---|---|---|
| Microsoft.Data.Sqlite | 8.0.21 | Локальная база SQLite, транзакции, согласованные резервные копии | MIT — [LICENSE.txt](../licenses/Microsoft.Data.Sqlite-8.0.21/LICENSE.txt) |
| MathNet.Numerics | 5.0.0 | Вычислительные функции и распределения для статистики | MIT — [LICENSE.md](../licenses/MathNet.Numerics-5.0.0/LICENSE.md) |
| ClosedXML | 0.104.2 | Чтение таблиц XLSX | MIT — [LICENSE](../licenses/ClosedXML-0.104.2/LICENSE) |

## Транзитивные зависимости

| Пакет | Версия | Основная цепочка использования | Лицензия и оригинальный текст |
|---|---|---|---|
| Microsoft.Data.Sqlite.Core | 8.0.21 | Microsoft.Data.Sqlite → управляемый провайдер | MIT — [LICENSE.txt](../licenses/Microsoft.Data.Sqlite.Core-8.0.21/LICENSE.txt) |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.6 | Microsoft.Data.Sqlite → конфигурация нативного движка | Apache-2.0 — [LICENSE.TXT](../licenses/SQLitePCLRaw.bundle_e_sqlite3-2.1.6/LICENSE.TXT), [NOTICE.TXT](../licenses/SQLitePCLRaw.bundle_e_sqlite3-2.1.6/NOTICE.TXT) |
| SQLitePCLRaw.core | 2.1.6 | SQLitePCLRaw → основной интерфейс | Apache-2.0 — [LICENSE.TXT](../licenses/SQLitePCLRaw.core-2.1.6/LICENSE.TXT), [NOTICE.TXT](../licenses/SQLitePCLRaw.core-2.1.6/NOTICE.TXT) |
| SQLitePCLRaw.lib.e_sqlite3 | 2.1.6 | SQLitePCLRaw → нативные файлы SQLite | Apache-2.0 для обвязки; код SQLite — public domain. [LICENSE.TXT](../licenses/SQLitePCLRaw.lib.e_sqlite3-2.1.6/LICENSE.TXT), [NOTICE.TXT](../licenses/SQLitePCLRaw.lib.e_sqlite3-2.1.6/NOTICE.TXT) |
| SQLitePCLRaw.provider.e_sqlite3 | 2.1.6 | SQLitePCLRaw → вызовы движка e_sqlite3 | Apache-2.0 — [LICENSE.TXT](../licenses/SQLitePCLRaw.provider.e_sqlite3-2.1.6/LICENSE.TXT), [NOTICE.TXT](../licenses/SQLitePCLRaw.provider.e_sqlite3-2.1.6/NOTICE.TXT) |
| ClosedXML.Parser | 1.2.0 | ClosedXML → разбор формул | MIT — [LICENSE.txt](../licenses/ClosedXML.Parser-1.2.0/LICENSE.txt) |
| DocumentFormat.OpenXml | 3.1.1 | ClosedXML → формат Open XML | MIT — [LICENSE](../licenses/DocumentFormat.OpenXml-3.1.1/LICENSE), [NOTICE](../licenses/DocumentFormat.OpenXml-3.1.1/NOTICE) |
| DocumentFormat.OpenXml.Framework | 3.1.1 | Open XML SDK → инфраструктура форматов | MIT — [LICENSE](../licenses/DocumentFormat.OpenXml.Framework-3.1.1/LICENSE), [NOTICE](../licenses/DocumentFormat.OpenXml.Framework-3.1.1/NOTICE) |
| ExcelNumberFormat | 1.1.0 | ClosedXML → числовые форматы Excel | MIT — [LICENSE](../licenses/ExcelNumberFormat-1.1.0/LICENSE) |
| RBush | 4.0.0 | ClosedXML → пространственный индекс диапазонов | MIT — [LICENSE](../licenses/RBush-4.0.0/LICENSE) |
| SixLabors.Fonts | 1.0.0 | ClosedXML → метрики шрифтов | Apache-2.0 — [LICENSE](../licenses/SixLabors.Fonts-1.0.0/LICENSE) |
| System.IO.Packaging | 8.0.1 | Open XML SDK → контейнеры OPC | MIT — [LICENSE.TXT](../licenses/System.IO.Packaging-8.0.1/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](../licenses/System.IO.Packaging-8.0.1/THIRD-PARTY-NOTICES.TXT) |
| System.Memory | 4.5.3 | SQLitePCLRaw → совместимые API памяти | MIT — [LICENSE.TXT](../licenses/System.Memory-4.5.3/LICENSE.TXT), [THIRD-PARTY-NOTICES.TXT](../licenses/System.Memory-4.5.3/THIRD-PARTY-NOTICES.TXT) |

## Происхождение текстов

Оригинальные `LICENSE.TXT` и `THIRD-PARTY-NOTICES.TXT` для **System.IO.Packaging 8.0.1** и **System.Memory 4.5.3** извлечены из соответствующих восстановленных NuGet-пакетов без изменения содержимого. Остальные оригинальные тексты скачаны из репозиториев разработчиков по коммиту из `.nuspec` либо тегу соответствующего выпуска:

| Компонент | Официальный источник зафиксированного выпуска |
|---|---|
| ClosedXML | [ClosedXML/ClosedXML, commit 3dfd3414…](https://github.com/ClosedXML/ClosedXML/tree/3dfd3414f3aa32fa8ffee7124478ca9ac5fe6945) |
| ClosedXML.Parser | [ClosedXML/ClosedXML.Parser, commit aaf2ade7…](https://github.com/ClosedXML/ClosedXML.Parser/tree/aaf2ade77aa160d7e929088320eab562b2df6851) |
| Open XML SDK | [dotnet/Open-XML-SDK, commit f9e6ad75…](https://github.com/dotnet/Open-XML-SDK/tree/f9e6ad7524e22b6b1f932deabc9055201b7870c0) |
| ExcelNumberFormat | [andersnm/ExcelNumberFormat, tag v1.1.0](https://github.com/andersnm/ExcelNumberFormat/tree/v1.1.0) |
| Math.NET Numerics | [mathnet/mathnet-numerics, tag v5.0.0](https://github.com/mathnet/mathnet-numerics/tree/v5.0.0) |
| Microsoft.Data.Sqlite | [dotnet/efcore, commit bd381fa6…](https://github.com/dotnet/efcore/tree/bd381fa6af1f80b3b6a52739729596cd68b6f5c8) |
| RBush | [viceroypenguin/RBush, commit b8690322…](https://github.com/viceroypenguin/RBush/tree/b8690322abac35d835baa2fdca4a3918ed77a910) |
| SixLabors.Fonts | [SixLabors/Fonts, commit 32bef429…](https://github.com/SixLabors/Fonts/tree/32bef42997adb10268369ca149777f00e4241ce9) |
| SQLitePCLRaw | [ericsink/SQLitePCL.raw, tag v2.1.6](https://github.com/ericsink/SQLitePCL.raw/tree/v2.1.6) |

**SixLabors.Fonts закреплён на версии 1.0.0.** В этой версии и указанном коммите применяется Apache License 2.0. Не следует переносить этот вывод на другие версии библиотеки: при обновлении проверяйте их собственные условия.

Math.NET используется с управляемым провайдером из `MathNet.Numerics`; Intel MKL и другие отдельно распространяемые нативные провайдеры в комплект не включены. `NOTICE`/`THIRD-PARTY-NOTICES` сохранены целиком: upstream-уведомления могут перечислять также инструменты разработки и альтернативные конфигурации самого стороннего проекта. Они не означают, что все перечисленные в них библиотеки добавлены в приложение. Точный состав разрешённых NuGet-зависимостей приведён в таблицах выше.

## Распространение и обновление

При распространении собранного приложения сохраняйте каталог `licenses`, этот документ, основной `LICENSE` и уведомления `NOTICE`/`THIRD-PARTY-NOTICES`. Сторонние компоненты используются в неизменённом виде. Их copyright-уведомления находятся в оригинальных текстах лицензий и `.nuspec`; они сохранены без подмены авторства проекта.

Текущий граф пакетов содержит MIT, Apache-2.0 и общественное достояние SQLite; GPL, AGPL и коммерческие лицензируемые пакеты в нём отсутствуют. Это описание относится к перечисленным версиям, а не к произвольным последующим обновлениям. При обновлении пакетов повторите `dotnet restore`, проверьте весь транзитивный граф, обновите таблицы и приложенные тексты.

Среда выполнения **.NET 8 / Windows Desktop Runtime** предоставляется Microsoft отдельно при обычной сборке. Если вы выпускаете автономную (`self-contained`) сборку, вместе с ней сохраняйте также оригинальные лицензии и уведомления именно той версии .NET Runtime, которую включила публикация. Лицензии установленной Windows и пользовательских шрифтов в этот проект не входят.

Пользовательские статьи, PDF-файлы, наборы данных и импортированные библиографические стили не становятся Apache-2.0 из-за помещения в дневник. Права на такие материалы сохраняются у их правообладателей. Встроенные в этот проект простые стили написаны для приложения; при импорте чужого `.csl` учитывайте лицензию этого файла.
