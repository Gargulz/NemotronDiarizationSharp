# Руководство SDK

Подробные примеры API, CLI и необязательная диагностика. Команды выполняются из корня репозитория; runtime и модель предоставляются отдельно. Простой сценарий записи приведён в [быстром старте](../../README.ru.md#быстрый-старт).

## Пример с декодированным PCM

SDK принимает **моно PCM float32** и явную частоту дискретизации от 8 000 до 96 000 Гц. Значения должны быть конечными; их следует нормализовать в диапазон `[-1, 1]`. SDK также читает поддерживаемые RIFF/WAVE через DiarizationWaveReader и ProcessWaveFile. Для других кодеков и контейнеров требуется внешнее декодирование. Native runtime преобразует поданную PCM-частоту к частоте модели.

Для полной записи используйте отдельный полно-контекстный режим:

<!-- sdk-example: consumer-api -->
```csharp
static DiarizationSnapshot ProcessConsumerPcm(
    string modelPath,
    string nativeRuntimeDirectory,
    ReadOnlySpan<float> monoPcm,
    int sampleRate,
    string? rttmPath = null)
{
    Environment.SetEnvironmentVariable("NEMOTONDIARIZATIONSHARP_NATIVE_DIR",
        Path.GetFullPath(nativeRuntimeDirectory));
    using var model = DiarizationModel.Load(Path.GetFullPath(modelPath),
        new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
    DiarizationSnapshot result = model.ProcessOffline(monoPcm, sampleRate);

    if (result.FrameCount > 0)
        Console.WriteLine($"Frame 0 at {result.GetFrameTimestamp(0):F2}s: " +
            $"speaker channel 0 probability {result.GetProbability(0, 0):F3} of {result.SpeakerCount}");
    foreach (DiarizationSegment segment in result.Segments)
        Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}: speaker {segment.Speaker}");

    if (rttmPath is not null)
    {
        using var writer = File.CreateText(rttmPath);
        DiarizationPostprocessor.WriteRttm(writer, result.Segments, recordingId: "recording");
    }
    return result;
}
```

`DiarizationSnapshot` предоставляет вероятности по нулевым индексам кадров и каналов говорящих, а также метки времени кадров; готовые сегменты содержат ID говорящего, начинающиеся с единицы. Объявление `using` освобождает нативную модель. `ProcessOffline` обрабатывает вход целиком и ограничен позиционной ёмкостью модели. Для длинных записей используйте streaming API ниже и явно завершайте поток. Управляемый пакет SDK не содержит native DLL и весов GGUF; разместите их отдельно. CPU и CUDA инференс проверены на Windows x64 с NeMo-Speech.cpp v0.2.0, но это не подтверждает совместимость с другими GPU и драйверами. Linux не проверен, права на распространение не подтверждены.
<!-- compile-example: ru-streaming-processing; scaffold=streaming -->
```csharp
using var model = DiarizationModel.Load(modelPath);
using var stream = model.OpenStream();

foreach (ReadOnlyMemory<float> chunk in GetMonoPcmChunks())
    stream.Push(chunk.Span, sampleRate);

stream.Finish();
DiarizationSnapshot final = stream.GetSnapshot();
foreach (DiarizationSegment segment in final.Segments)
    Console.WriteLine($"{segment.StartTime:F2}-{segment.EndTime:F2}: speaker {segment.Speaker}");
```

`GetSnapshot()` не сдвигает курсор инкрементального чтения вероятностей. Вызывайте `ReadNewFrames()` по мере обработки и читайте достаточно часто, чтобы после уплотнения нативной истории не получить `DiarizationFrameHistoryGapException`. Индексы каналов вероятностей начинаются с нуля; `DiarizationSegment.Speaker` содержит нативный ID говорящего, начинающийся с единицы. `Backend` по умолчанию равен `Auto`; `GpuDeviceIndex` выбирает CUDA-устройство, но не backend. При выборе CUDA значение null означает устройство 0.

Модель и поток владеют нативными ресурсами и должны освобождаться, как в примерах. Поток удерживает нативную модель до собственного освобождения, поэтому ранний вызов `Dispose()` у managed-модели не закрывает активный поток. Не используйте и не освобождайте экземпляр одновременно с другой выполняющейся операцией: API не обещает общей потокобезопасности. Отсутствующая модель приводит к `FileNotFoundException`; неверная частота дискретизации и не конечные значения — к исключениям аргументов; ошибки нативного вызова передаются как `InvalidOperationException` с сообщением native runtime. При инкрементальном чтении перехватывайте `DiarizationFrameHistoryGapException` до базового `InvalidOperationException`. Отсутствующие или несовместимые DLL могут привести к `DllNotFoundException` или `BadImageFormatException`.

## Выбор CPU и CUDA

Вызов `DiarizationModel.Load(modelPath)` по умолчанию использует Auto для backend и геометрию потоковой обработки native runtime. Auto предпочитает CUDA, если установлен CUDA runtime и `nvidia-smi` сообщает об устройстве; иначе выбирает установленный CPU runtime. Чтобы потребовать CPU, задайте backend явно:

<!-- compile-example: ru-cpu-selection; scaffold=model-path -->
```csharp
using var model = DiarizationModel.Load(modelPath,
    new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
```

Чтобы явно выбрать CUDA, используйте такой вариант в процессе с CUDA-enabled native runtime:

<!-- compile-example: ru-cuda-selection; scaffold=model-path -->
```csharp
using var model = DiarizationModel.Load(modelPath,
    new DiarizationModelOptions { Backend = DiarizationBackend.Cuda, GpuDeviceIndex = 0 });
```

Неотрицательный индекс выбирает CUDA-устройство, но сам по себе не включает CUDA. `Backend = Cuda` требует CUDA без перехода на CPU; `Backend = Cpu` требует CPU. Auto проверяет устройство через `nvidia-smi` до загрузки runtime; эта проверка не гарантирует совместимость драйвера или выполнения kernel. После начала нативной загрузки backend не меняется. Первый native-вызов фиксирует backend процесса; для разных backend запускайте отдельные процессы. Значения `Preset` (`streaming`, `offline`, `v3-streaming` и `v3-offline`) настраивают геометрию потоковой обработки; названия `offline` и `v3-offline` не выбирают `ProcessOffline`.

Для модели NVIDIA Nemotron 3 доступны четыре профиля, соответствующие [официальной таблице model card](https://huggingface.co/nvidia/Nemotron-3-Diarization). NVIDIA называет первую конфигурацию “Very high latency (offline)”; имя `Nemotron3OfflineStyle` подчёркивает, что это именно потоковая геометрия. Все значения ниже заданы в грубых кадрах encoder длительностью 80 мс. Задержка рассчитывается как `(ChunkFrames + RightContextFrames) × 80 мс`.

| Профиль SDK | Метка NVIDIA | Кэш говорящих | FIFO | Chunk | Правый контекст | Период обновления |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| `Nemotron3OfflineStyle` | Very high latency (offline), 30,4 с | 264 | 40 | 340 | 40 | 300 |
| `Nemotron3LowLatency` | Low latency, 1,04 с | 264 | 264 | 9 | 4 | 222 |
| `Nemotron3VeryLowLatency` | Very low latency, 0,64 с | 264 | 264 | 6 | 2 | 222 |
| `Nemotron3UltraLowLatency` | Ultra-low latency, 0,32 с | 264 | 264 | 3 | 1 | 222 |

Используйте, например, `Preset = DiarizationPresets.Nemotron3LowLatency`. Каждый профиль выбирает поддерживаемый native preset `v3-streaming` в NeMo-Speech.cpp v0.2.0 и задаёт таблицу через имеющиеся managed-переопределения геометрии; ни один профиль не совпадает полностью с native preset. Левый контекст остаётся равным нулю по умолчанию для V3 streaming. Профили предназначены для совместимых моделей Nemotron-3 V3 и runtime v0.2.0; native runtime продолжает проверять модель. Существующие параметры геометрии позволяют изменить профили в C# и в JSON `modelOptions`. Неположительные значения наследуют значения профиля для всех полей, кроме `LeftContextFrames`: ноль задаётся явно, отрицательное или null наследует профиль. В таблице NVIDIA нет рекомендаций по порогам сегментации V3, поэтому native defaults сегментации не меняются.

`NEMOTONDIARIZATIONSHARP_NATIVE_DIR`, если задана, должна содержать абсолютный путь к папке выбранного runtime с entry DLL и зависимостями. Иначе loader проверяет стандартные каталоги CPU и CUDA в папке приложения; файлы туда должно поместить само приложение, поскольку native runtime NuGet-пакеты недоступны. Затем проверяются совместимые legacy-каталоги CPU. C ABI entry DLL загружается по абсолютному пути с поиском зависимостей в этой папке и System32. Backend фиксируется для процесса; после начала нативной загрузки переключение не выполняется. Загрузка проверена на Windows x64; поддержка Linux не подтверждена. Нативные бинарные файлы не включены в managed NuGet-пакет или репозиторий, права на их распространение не проверены.

Тест нативного инференса для Windows x64 использует локальную модель GGUF и моно PCM в формате float32 little-endian. Укажите пути в переменных `NEMOTONDIARIZATIONSHARP_MODEL` и `NEMOTONDIARIZATIONSHARP_PCM_F32LE`. Старые имена `NEMOTONSHARP_MODEL` и `NEMOTONSHARP_PCM_F32LE` остаются резервными. Для загрузки нативных библиотек из другой папки задайте `NEMOTONDIARIZATIONSHARP_NATIVE_DIR`; старое имя `NEMOTONSHARP_NATIVE_DIR` также поддерживается. Консольный пример поддерживает описанный ниже WAV-to-RTTM сценарий и сохраняет позиционный вызов для raw PCM.

## Консоль WAV в RTTM

После сборки консольного проекта в Release укажите существующие модель GGUF, WAV и совместимый native runtime со всеми зависимостями. Папка результата должна существовать.

```powershell
dotnet run --project examples/NemotronDiarizationSharp.Console -c Release --no-build --no-restore -- --model '<model.gguf>' --input '<audio.wav>' --output '.\result.rttm' --backend cpu --native-dir '<absolute-cpu-runtime-directory>'
dotnet run --project examples/NemotronDiarizationSharp.Console -c Release --no-build --no-restore -- --model '<model.gguf>' --input '<audio.wav>' --output '.\result.rttm' --backend cuda --gpu-device 0 --native-dir '<absolute-cuda-runtime-directory>'
```

`--native-dir` преобразуется в абсолютный путь существующей папки и задаёт `NEMOTONDIARIZATIONSHARP_NATIVE_DIR` до загрузки модели. Вместо параметра можно заранее задать эту переменную процесса. Для CPU нужен совместимый CPU runtime, для CUDA - отдельно подготовленный CUDA runtime; автоматического перехода на другой backend нет. Выбор runtime кешируется при первом нативном обращении, как описано выше. Консоль не скачивает runtime или модель.

| Параметр | Поведение |
| --- | --- |
| `--model`, `--input`, `--output` | Обязательные пути модели, WAV и RTTM. До загрузки проверяются наличие и непустой размер модели; содержимое и совместимость проверяет нативный SDK. |
| `--backend cpu\|cuda` | По умолчанию `cpu`; CUDA использует устройство 0, если индекс не указан. |
| `--gpu-device index` | Неотрицательное целое число, только для CUDA. |
| `--native-dir directory` | Необязательный явный путь к папке native runtime. |
| `--segmentation native\|managed` | По умолчанию `native`. |
| `--recording-id token` | По умолчанию имя входного файла без расширения; пробельные/управляющие символы заменяются на `_`, пустое имя - на `recording`. Явный токен должен быть непустым, без пробельных/управляющих символов. |
| `--chunk-frames count` | Число декодированных моно аудиокадров на push, целое 1..96000; по умолчанию одна секунда при исходной частоте. Это не настройка геометрии кадров энкодера SDK. |
| `--help` | Отдельный вызов справки, без чтения файлов и загрузки native-кода. |

Значения передаются отдельно от параметров; неизвестные/повторяющиеся параметры и неверные значения отклоняются. Код завершения `0` означает успех/справку, `2` - ошибку параметров или предварительной проверки, `1` - ошибку обработки/runtime/экспорта. Сообщение сохраняет исходную диагностику, включая отсутствующие нативные зависимости, несовместимые образы, точки входа и версии runtime.

WAV reader SDK читает обычные файлы с возможностью seek в формате little-endian RIFF/WAVE: unsigned PCM8, signed PCM16/24/32 или IEEE float32, минимум один канал, частота 8000-96000 Гц. Аудио должно быть непустым и состоять из полных кадров; нужны ровно один chunk `fmt ` и один `data`. Проверяются объявленные границы RIFF/chunk/padding, выравнивание и byte rate. Неизвестные chunks пропускаются, байты за объявленной границей RIFF игнорируются. Сжатое аудио, WAVE_FORMAT_EXTENSIBLE, RF64 и RIFX не поддерживаются. Каналы сводятся арифметическим средним с накоплением в double. NaN/infinity в WAV float отклоняются; конечные значения каждого канала ограничиваются `[-1,1]` до усреднения. Консоль передаёт исходную частоту без managed-ресемплинга; частоту модели обрабатывает нативный frontend.

Предварительная проверка проходит всё аудио с ограниченным буфером до `DiarizationModel.Load`, затем файл открывается повторно для последовательного декодирования и потоковой обработки. Полная PCM-запись не загружается в память. Режим native экспортирует сегменты финального нативного snapshot после `Finish()`. Режим managed вызывает строгий `ReadResultUpdates()` после каждого push и finish, применяет наблюдаемые ревизии к полной `DiarizationProbabilityHistory(null)`, проверяет покрытие от нулевого до финального кадра и вызывает managed `Segment`. Разрыв истории приводит к ошибке вместо экспорта неполной истории. Полная история вероятностей и её финальная копия растут с длительностью записи; ограниченный PCM-буфер не означает постоянную общую память. Нативное уплотнение может скрыть старые ревизии, а полное захваченное покрытие не гарантирует стабильность. Native и managed сегментация могут различаться.

Экспорт использует SDK `WriteRttm`, сохраняет перекрытия и абсолютное время кадров, записывает канал 1 и идентификаторы `speaker_N` с нумерацией от единицы. Из-за шага кадров модели финальные временные границы могут выходить за длительность аудио; консоль не обрезает и не сдвигает их. RTTM записывается во временный файл в папке назначения и атомарно заменяет результат только после успешной обработки/экспорта; при ошибке прежний результат сохраняется. Выходной путь должен отличаться от модели и аудио. Пути модели, аудио и результата, включая все родительские папки, не должны содержать символические ссылки, junctions или reparse points. Не меняйте пути во время работы: проверки не изолируют от конкурентных изменений файловой системы.

Прежний вызов сохранён: `NemotronDiarizationSharp.Console <model.gguf> <mono-pcm-f32le.raw> <sample-rate>`. Он использует ограниченные последовательные чтения и печатает финальные native-сегменты в stdout. Raw float32 little-endian должен быть непустым, выровненным по четырём байтам и содержать только конечные значения; конечные амплитуды передаются без изменений. Допустимая частота также 8000-96000 Гц.

## Необязательная длительная диагностика

`tests/NemotronDiarizationSharp.RealSpeech` — отдельная opt-in консольная программа для короткого CPU-пилота на реальной речи. Она принимает внешние WAV и RTTM, обрезает их к одному временному интервалу, передаёт моно PCM float32 через публичный API SDK, проверяет временные метки, сегменты и вероятности, а также сохраняет RTTM модели, обрезанную эталонную разметку и JSON-отчёт. Программа не входит в обычный набор тестов и не реализует расчёт DER. Используйте проверенный внешний scorer с collar 0 и overlap включённым либо укажите `DER=NOT_MEASURED`. Поддерживаются RIFF/WAVE PCM 16/24/32 бит и IEEE float32 с частотой 8–96 кГц; многоканальный звук сводится усреднением каналов. Конфигурация `v3-streaming` использует геометрию задержки 30,4 с: speaker cache 264, FIFO 40, chunk 340, right context 40, update period 300. Аудио подаётся последовательными блоками по одной секунде; программа пытается установить приоритет процесса ниже обычного. Пример команд приведён в английском [README](../en/sdk-guide.md#opt-in-long-run-diagnostics).

`tests/NemotronDiarizationSharp.LongRun` — отдельный консольный harness, который не запускается командой `dotnet test`. Он проверяет размер и SHA-256 локальных модели и 8-секундного PCM-файла, циклически подаёт его ограниченными фрагментами, проверяет непрерывность инкрементальных кадров, состояние сохранённого окна и сегментов, а также выводит измерения памяти процесса и .NET и пропускной способности. Повторяющийся звук проверяет механику runtime, но не точность определения говорящих.

В Windows PowerShell задайте переменные только для текущего процесса и сначала выполните короткую проверку:

```powershell
$env:NEMOTONDIARIZATIONSHARP_MODEL = '<путь-к-проверенной-модели.gguf>'
$env:NEMOTONDIARIZATIONSHARP_PCM_F32LE = '<путь-к-проверенному-моно-f32le.raw>'
$env:NEMOTONDIARIZATIONSHARP_NATIVE_DIR = '<путь-к-папке-NeMo-Speech.cpp-v0.2.0-bin>'
dotnet run --project tests/NemotronDiarizationSharp.LongRun -c Release -- --duration-minutes 0.25
```

Затем запросите не менее 25 минут логического аудио:

```powershell
dotnet run --project tests/NemotronDiarizationSharp.LongRun -c Release -- --duration-minutes 25 --require-compaction
```

Параметры `--chunk-seconds`, `--poll-seconds` и `--diagnostic-interval-seconds` настраивают размер фрагментов, частоту инкрементального чтения и период замеров памяти/снимков. По умолчанию подаются фрагменты по 1 секунде, кадры читаются каждые 5 секунд, снимки и замеры выполняются каждую минуту логического аудио; лимит реального времени — 120 минут. Короткая проверка без `--require-compaction` проверяет выполнение и освобождение ресурсов, не заявляя об уплотнении. Остановка из-за давления памяти, недостижение длительности, разрыв кадров, некорректные данные или отсутствие ожидаемого уплотнения не засчитываются как успех. Каждый `ReadNewFrames()` копирует всё сохранённое нативное окно вероятностей, поэтому более частое чтение увеличивает копирование и выделение памяти. Для оценки частоты опроса сравнивайте отдельные запуски. Показатели рабочей/закрытой памяти процесса и оценки управляемой кучи являются наблюдениями, а не фиксированным порогом утечки.

## Параметры модели и сегментации

`DiarizationModel.Load(modelPath)` использует Auto для выбора backend и геометрию native runtime по умолчанию. Существующая перегрузка принимает `DiarizationModelOptions`. `Backend` позволяет потребовать CPU или CUDA; `GpuDeviceIndex` выбирает CUDA-устройство только при выборе CUDA, а null означает устройство 0.

Константы `DiarizationPresets` присваиваются существующему строковому свойству `Preset`:

| Константа | Имя native preset |
| --- | --- |
| `DiarizationPresets.Streaming` | `streaming` |
| `DiarizationPresets.Offline` | `offline` |
| `DiarizationPresets.V3Streaming` | `v3-streaming` |
| `DiarizationPresets.V3Offline` | `v3-offline` |

Каждый preset настраивает потоковую обработку с состоянием. Имена offline выбирают увеличенную потоковую геометрию. Отдельный `ProcessOffline(samples, sampleRate, options)` выполняет stateless full-attention inference и ограничен позиционной ёмкостью модели.

Все значения геометрии задаются в крупных **кадрах энкодера по 80 мс**, независимо от периода выходных вероятностей; native вероятности V3 имеют кадры по 10 мс. Пропущенное значение или null наследует preset. Для `ChunkFrames`, `RightContextFrames`, `FifoFrames`, `SpeakerCacheFrames` и `UpdatePeriodFrames` ноль и отрицательные значения также означают наследование. Для `LeftContextFrames` отрицательное значение наследует preset, а **ноль является явным переопределением**. Поэтому нулевым override нельзя отключить правый контекст или FIFO, хотя сам preset может содержать ноль.

До нативного выделения ресурсов управляемые вычисления в `long` отклоняют эффективную геометрию, вызывающую переполнение знаковых нативных сумм и произведений кадров. Границы выведены из этих выражений; произвольного максимума геометрии нет. Доказательство безопасности относится к поддерживаемой закреплённой модели `Nemotron-3-Diarization.q8_0.gguf` (SHA-256 `08456d9e22cd9a323c0364d98375f3746d6e68507ebb705cd46438c534c7a3a1`) с NeMo-Speech.cpp v0.2.0. Коэффициент subsampling энкодера равен 8; поддерживаемый контракт V3 использует output subsampling 1 и upsampling 8, а значения по умолчанию V2 — output subsampling 8 и upsampling 1. Silence padding равен 3. Произвольные метаданные GGUF этим доказательством не покрываются. Native runtime продолжает проверять имена preset, минимальный speaker cache и позиционную ёмкость модели.

Добавьте `using NemotronDiarizationSharp;` и создайте параметры следующим образом; передайте результат в `DiarizationModel.Load(modelPath, CreateStreamingOptions())`:

<!-- sdk-example: preset-options -->
```csharp
static DiarizationModelOptions CreateStreamingOptions()
{
    return new DiarizationModelOptions
    {
        GpuDeviceIndex = null,
        Preset = DiarizationPresets.V3Streaming,
        LeftContextFrames = 0,
        ChunkFrames = null
    };
}
```

### Необязательная JSON-конфигурация

`DiarizationConfiguration.Parse(json)` использует встроенный `System.Text.Json` и не добавляет пакетных зависимостей. Парсинг выполняет управляемую проверку без загрузки модели или запуска inference. Корень должен быть объектом с необязательными секциями `modelOptions` и `segmentationOptions`; имена используют точный camelCase и учитывают регистр.

Поддерживаются все существующие параметры модели: `gpuDeviceIndex`, `preset`, `chunkFrames`, `rightContextFrames`, `leftContextFrames`, `fifoFrames`, `speakerCacheFrames` и `updatePeriodFrames`. Для сегментации доступны `onset`, `offset`, `padOnsetSeconds`, `padOffsetSeconds`, `minGapSeconds` и `minDurationSeconds`. Пропущенные или null значения сохраняют существующую семантику sentinel. Пропущенная/null секция `modelOptions` создаёт параметры модели по умолчанию; пропущенная/null `segmentationOptions` остаётся null, а `{}` создаёт явный объект параметров сегментации по умолчанию.

Неизвестные или повторные поля (включая эквивалентные имена с escape), неверный регистр/тип, комментарии, завершающие запятые/лишние данные, некорректный JSON и неконечные либо переполняющиеся числа отклоняются с `JsonException`. Ошибки управляемой проверки GPU, геометрии и сегментации также сообщаются через `JsonException`. Проверка preset и ограничений конкретной модели остаётся в native runtime.

Следующие функции требуют `using NemotronDiarizationSharp;`. Передайте внешний GGUF `modelPath`, разместите совместимый native runtime, подготовьте конечные моно PCM float32 `samples` и `sampleRate` от 8 000 до 96 000 Гц. Декодирование аудио выполняет приложение. Нативный inference запускается при вызове функций обработки; создание параметров и парсинг JSON выполняются управляемым кодом. Для длинных записей подавайте ограниченные фрагменты последовательно до завершения потока.

<!-- sdk-example: json-configuration -->
```csharp
static DiarizationConfiguration CreateConfiguration()
{
    return DiarizationConfiguration.Parse("""
        {
          "modelOptions": {
            "gpuDeviceIndex": null,
            "preset": "v3-streaming",
            "leftContextFrames": 0,
            "chunkFrames": null
          },
          "segmentationOptions": {
            "onset": 0.64,
            "offset": 0.4,
            "padOnsetSeconds": null,
            "padOffsetSeconds": 0.1,
            "minGapSeconds": 0,
            "minDurationSeconds": null
          }
        }
        """);
}

static DiarizationSnapshot ProcessConfiguredStream(string modelPath, float[] samples, int sampleRate)
{
    DiarizationConfiguration configuration = CreateConfiguration();
    using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
    using var stream = model.OpenStream();
    stream.Push(samples, sampleRate);
    stream.Finish();
    return stream.GetSnapshot(configuration.SegmentationOptions);
}

static DiarizationSnapshot ProcessConfiguredOffline(string modelPath, float[] samples, int sampleRate)
{
    DiarizationConfiguration configuration = CreateConfiguration();
    using var model = DiarizationModel.Load(modelPath, configuration.ModelOptions);
    return model.ProcessOffline(samples, sampleRate, configuration.SegmentationOptions);
}
```

`DiarizationSegmentationOptions` можно передать в `DiarizationStream.GetSnapshot(options)` или `DiarizationModel.ProcessOffline(samples, sampleRate, options)`. `Onset` и `Offset` задают пороги входа и выхода из речи с гистерезисом; padding расширяет границы сегментов, минимальный промежуток заполняет короткие паузы, а минимальная длительность отбрасывает короткие сегменты. Пропущенные значения используют настройки native runtime. ABI также трактует нулевые и отрицательные значения как native defaults, поэтому задать ими явное нулевое или отрицательное переопределение нельзя. Вызовы без объекта параметров передают нативной функции нулевой указатель и сохраняют её поведение по умолчанию.

Native runtime уплотняет историю вероятностей только при обнаружении подходящего интервала, когда все говорящие молчат. Уплотнение не гарантировано через фиксированную длительность записи и не задаёт жёсткий предел нативной памяти. Ранее завершённые сегменты остаются доступны, но пользовательская сегментация пересчитывает только сохранённый хвост; старые сегменты могли быть рассчитаны с native defaults. Изменение параметров не пересчитывает удалённую историю задним числом. Нативное уплотнение независимо от лимитов хранения в управляемом `DiarizationProbabilityHistory`.

## Инкрементальное чтение кадров

Вызов `DiarizationStream.ReadNewFrames()` возвращает `DiarizationFrameBatch` только с кадрами, ещё не выданными предыдущими инкрементальными чтениями этого потока. В пакете есть абсолютный `FirstFrameIndex`, число кадров, число говорящих, период кадра, вероятности в порядке «кадр — говорящие» только для чтения и метод `GetProbability(absoluteFrameIndex, speakerIndex)`. Пустое чтение не сдвигает курсор. `GetSnapshot()` остаётся независимым и не потребляет кадры; после `Finish()` следующему чтению доступны оставшиеся кадры. Пакет владеет своими данными и остаётся доступен после освобождения потока.

Если native runtime уплотнил историю и удалил ещё не прочитанные кадры, `ReadNewFrames()` выбрасывает `DiarizationFrameHistoryGapException` с пропущенным диапазоном и первым сохранённым кадром. Читайте данные достаточно часто, чтобы избежать разрывов; завершённые сегменты — отдельный результат и не восстанавливают удалённые вероятности кадров. API native v0.2.0 копирует всё сохранённое окно вероятностей при каждом чтении, даже если возвращается лишь небольшой новый фрагмент. Пакет фиксирует значения, полученные во время чтения; native ABI не гарантирует неизменность ранее опубликованных вероятностей.

## Асинхронная подача PCM

`await stream.PushChunksAsync(chunks, sampleRate, cancellationToken)` последовательно читает `IAsyncEnumerable<ReadOnlyMemory<float>>`: ожидает очередной фрагмент, передаёт его в обработку и только затем запрашивает следующий. Запись целиком в память и очередь с неограниченным буфером не создаются. Нативный `Push` остаётся синхронным и выполняется в рабочем потоке; отмена не прерывает уже начавшийся нативный вызов. Источник получает токен при перечислении, поэтому источник, игнорирующий отмену, может задержать её до следующего фрагмента. Уже выданный источником фрагмент полностью обрабатывается до сообщения об отмене. `Finish()` автоматически не вызывается.

Не изменяйте и не переиспользуйте память фрагментов до завершения возвращённой задачи. При отмене успешно обработанные фрагменты остаются в открытом потоке, который можно продолжить или явно завершить. Ошибки источника и проверки входных данных также оставляют пригодный обработанный префикс; после ошибки нативного `Push` состояние потока неопределённо, поэтому его можно только освободить. Пока асинхронная обработка активна, синхронная подача, `Finish`, снимки, инкрементальные чтения, освобождение и второй асинхронный вызов отклоняются. Дождитесь завершения задачи перед использованием или освобождением потока. Модель можно освободить во время работы потока: safe handle потока удерживает нативную модель до закрытия потока.

## Ограничения проекта

- Целевая платформа управляемых проектов — .NET 10 (`net10.0`); локально проверены Windows x64 CPU и CUDA; широкая совместимость не подтверждена.
- Веса модели не включаются в NuGet-пакеты.
- Сохраняются вероятности говорящих, временные метки кадров, сегменты, поведение потоковой обработки и явное освобождение ресурсов.
- Перед распространением нативных зависимостей необходимо проверить их лицензии.
- Для работы управляемого runtime не требуются Python, PyTorch и FFmpeg.

## Результаты с ревизиями и управляемая постобработка

`ReadNewFrames()` использует добавочный курсор: он возвращает только кадры, не выданные предыдущими вызовами. У `ReadResultUpdates()` отдельный курсор: он сравнивает текущее сохранённое окно вероятностей с предыдущим наблюдением. Каждый принадлежащий вызывающему коду update содержит `NewFrames`, ноль или несколько изменённых диапазонов `RevisedFrames` и `Segments` — полный актуальный список сегментов, заменяющий предыдущий. `SourceId` и последовательные значения `Sequence` идентифицируют управляемый курсор, а не native-поток. `FirstRetainedFrame`, `FrameCount`, `ComparedFrames` и `IsFinal` описывают наблюдаемое окно и покрытие сравнения. Чтение snapshot не сдвигает ни один из этих курсоров.

Если уплотнение удалило ещё не прочитанные кадры, обычное чтение выбрасывает `DiarizationFrameHistoryGapException`, не закрывая возможность восстановления курсора. Явный вызов `ReadResultUpdates(allowHistoryGap: true)` указывает потерянный диапазон в `HistoryGap` и возвращает доступный хвост. Считайте такой результат неполным: не применяйте его к истории, которая до разрыва заявляла непрерывное покрытие. В native ABI нет маркера стабильного кадра, поэтому следующий update может изменить ранее наблюдавшиеся вероятности. Список сегментов до финального результата предварительный и при каждом update заменяется новым полным списком. Уплотнение может скрыть последнюю ревизию. Управляемые scratch/cache-буферы повторно используются после прогрева, но native ABI всё равно копирует и сравнивает всё сохранённое окно; снимки сегментов и максимальный объём выделенного кэша требуют отдельной памяти.

### Синтетическая постобработка и история вероятностей

`DiarizationPostprocessingOptions` — конкретная управляемая конфигурация, где числовой ноль задаётся явно. По умолчанию onset/offset равны `0.5`, а padding, минимальный промежуток и минимальная длительность — нулю. Равенство порогу onset не начинает речь, равенство offset не завершает её. Алгоритм находит активные диапазоны, расширяет padding и обрезает границы по переданному окну кадров, объединяет сегменты одного говорящего, если промежуток меньше или равен `MinGapSeconds`, затем отбрасывает сегменты короче `MinDurationSeconds`. Говорящие обрабатываются независимо, поэтому перекрытия сохраняются. Этот отдельный управляемый алгоритм не обещает те же результаты, что нативная сегментация. Существующие `DiarizationSegmentationOptions` и JSON-конфигурация сохраняет семантику нативных sentinel-значений.

Для `DiarizationProbabilityHistory` нужно выбрать положительный лимит `maxRetainedFrames` либо явно передать `null`, чтобы хранить всю историю в памяти. `Apply` принимает последовательные обновления одного источника, применяет ревизии и добавляет новые кадры атомарно; разрывы и несмежные диапазоны отклоняются. Намеренное удаление старых кадров видно по `FirstRetainedFrame` и `IsCompleteFromFrameZero`; последнее означает покрытие кадров, но не гарантирует стабильность ревизий. `GetFrames()` возвращает принадлежащую вызывающему копию. До привязки обновлений live-потока `Append` может восстановить сохранённые извне значения `DiarizationFrameBatch`, если пакеты непрерывны и имеют одинаковые метаданные. Первый live update должен продолжаться ровно с конца восстановленных данных. Сохранение данных и возобновление native-потока остаются задачей приложения. Удаление из ограниченной истории удаляет из неё ранее захваченные вероятности. Native-уплотнение удаляет незахваченные native-значения и может скрыть последующие ревизии, но уже захваченные managed-историей значения остаются доступными согласно её политике хранения.

Синтетический пример ниже не загружает модель и не вызывает native runtime. RTTM записывается в `TextWriter`, переданный вызывающим кодом; ID записи не должен содержать пробелы, канал должен быть положительным, номера говорящих начинаются с единицы. Перекрывающиеся говорящие сохраняются. Некорректные данные или ошибка writer/enumeration могут оставить частичный вывод, поэтому атомарное сохранение не гарантируется.

<!-- sdk-example: synthetic-postprocessing -->
```csharp
static string CreateSyntheticRttm()
{
    var history = new DiarizationProbabilityHistory(maxRetainedFrames: null);
    history.Append(DiarizationFrameBatch.FromProbabilities(0, 1, 1.0, [0.8f, 0.1f]));
    history.Append(DiarizationFrameBatch.FromProbabilities(2, 1, 1.0, [0.8f]));

    var options = new DiarizationPostprocessingOptions
    {
        Onset = 0.5f,
        Offset = 0.5f,
        PadOffsetSeconds = 0.5,
        MinGapSeconds = 0.5,
        MinDurationSeconds = 0
    };
    IReadOnlyList<DiarizationSegment> segments = DiarizationPostprocessor.Segment(history.GetFrames(), options);
    using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
    DiarizationPostprocessor.WriteRttm(writer, segments, recordingId: "synthetic");
    return writer.ToString();
}
```

### Асинхронные потоковые результаты

`PushChunksAsync()` последовательно принимает PCM, а `Finish()` остаётся обязанностью вызывающего кода. `ProcessStreamAsync()` вместо этого выдаёт update после каждого полученного фрагмента, включая пустые, затем при нормальном завершении источника вызывает `Finish()` и читает финальный update. Перечисление управляется потребителем: следующий фрагмент не запрашивается, пока потребитель не запросит следующий update. Поток зарезервирован до финального yield или освобождения перечислителя. Источник получает токен отмены. Отмена при ожидании кооперативна; уже полученный фрагмент полностью проходит native push и чтение результата до проверки отмены. Нативные вызовы нельзя прервать. Отмена, досрочный выход из цикла, ошибка источника или проверки входа не вызывают `Finish()`. После ошибки чтения, возникшей уже после Push, PCM записан: повторите чтение результата, не отправляя тот же фрагмент ещё раз. Если `Finish()` завершился успешно, но чтение финального результата завершилось ошибкой, поток остаётся завершённым; `ReadResultUpdates()` позволяет повторить чтение, но `ProcessStreamAsync()` нельзя запустить снова. Ошибки чтения/разрыва можно исправить повторным чтением; после ошибок native `Push` или `Finish` освободите поток после очистки перечислителя.

Этот компилируемый пример принимает открытый поток и декодированные приложением фрагменты mono float32 PCM с одной явной согласованной частотой дискретизации. Показанный лимит 12 000 кадров зависит от частоты кадров модели и не ограничивает всю native-память или память процесса. Пример возвращает сегменты текущего сохранённого окна; запись целиком не буферизуется, декодирование и пересэмплирование не выполняются.

<!-- sdk-example: async-results -->
```csharp
static async Task<IReadOnlyList<DiarizationSegment>> ProcessAsyncResults(
    DiarizationStream stream,
    IAsyncEnumerable<ReadOnlyMemory<float>> monoPcmChunks,
    int sampleRate,
    CancellationToken cancellationToken)
{
    var history = new DiarizationProbabilityHistory(maxRetainedFrames: 12_000);
    await foreach (DiarizationResultUpdate update in
        stream.ProcessStreamAsync(monoPcmChunks, sampleRate, cancellationToken))
    {
        history.Apply(update);
        IReadOnlyList<DiarizationSegment> provisionalSegments = DiarizationPostprocessor.Segment(
            history.GetFrames(), new DiarizationPostprocessingOptions());
        if (update.IsFinal)
            return provisionalSegments;
    }

    return Array.Empty<DiarizationSegment>();
}
```

Пример чтения с обработкой разрыва и заменой полного списка сегментов приведён ниже. Примеры компилируются в управляемых тестах; там выполняется только синтетический пример создания и восстановления вероятностей, сегментации и RTTM. Native inference и реальное поведение native-ревизий/отмены не проверяются. В приложении добавьте `using NemotronDiarizationSharp;` и используемые в сигнатурах стандартные пространства имён `System`; фрагменты показывают методы из файла тестов, где они обычно компилируются.

<!-- sdk-example: revision-polling -->
```csharp
static (DiarizationResultUpdate Update, bool IsIncomplete) PollRevisionAwareUpdate(DiarizationStream stream)
{
    try
    {
        return (stream.ReadResultUpdates(), false);
    }
    catch (DiarizationFrameHistoryGapException)
    {
        DiarizationResultUpdate recovered = stream.ReadResultUpdates(allowHistoryGap: true);
        return (recovered, recovered.HistoryGap.HasValue);
    }
}

static void DisplayLatestResult(DiarizationStream stream)
{
    (DiarizationResultUpdate update, bool incomplete) = PollRevisionAwareUpdate(stream);
    Console.WriteLine($"Update {update.Sequence}, frames [{update.FirstRetainedFrame}, {update.FrameCount}), final={update.IsFinal}");
    if (update.HistoryGap is { } gap)
        Console.WriteLine($"Missing frames [{gap.StartFrame}, {gap.EndFrameExclusive})");
    foreach (DiarizationFrameBatch revision in update.RevisedFrames)
        Console.WriteLine($"Revised [{revision.FirstFrameIndex}, {revision.FirstFrameIndex + revision.FrameCount})");
    Console.WriteLine(incomplete ? "Probability history is incomplete after a reported gap." : "No unread history gap.");
    IReadOnlyList<DiarizationSegment> latestSegments = update.Segments;
    Console.WriteLine($"Latest replacement segment count: {latestSegments.Count}");
}
```

