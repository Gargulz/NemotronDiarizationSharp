# Эталонные сценарии Nemotron 3

Геометрия модели и получение сегментов настраиваются независимо. Для готовой записи и live-потока точки входа SDK выбирают опубликованную геометрию NVIDIA Nemotron 3 и обработку временных меток, совместимую с NeMo. Это выбор сценария SDK на основе официальной карточки модели, а не утверждение, что NVIDIA рекомендует одну универсальную конфигурацию постобработки.

В [скомпилированном коде примеров](../../examples/NemotronDiarizationSharp.Console/Nemotron3ReferenceExamples.cs) есть методы для WAV, PCM, live-обновлений, переопределений, native и managed-режимов и экспорта RTTM.

## Готовая WAV-запись

```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording("nemotron3.gguf");
DiarizationSnapshot result = model.ProcessWaveFile("meeting.wav");

Console.WriteLine($"Финальный результат: {result.IsFinal}; кадров: {result.FrameCount}");
DiarizationPostprocessor.WriteRttm(Console.Out, result.Segments, "meeting");
```

`LoadForRecording` выбирает опубликованную потоковую геометрию offline-style: кэш говорящих — 264 кадра, FIFO — 40, размер блока — 340, правый контекст — 40, период обновления — 300. `ProcessWaveFile` декодирует WAV небольшими PCM-блоками и передаёт их в поток. Полная декодированная запись не загружается в один массив. Для повторного расчёта эталонных сегментов SDK хранит и обновляет вероятности от кадра 0; при разрыве истории native runtime финальный неполный результат не возвращается.

Лёгкий WAV-декодер принимает little-endian RIFF/WAVE с PCM 8/16/24/32 бит или IEEE float32, частотой 8–96 кГц и усредняет каналы до mono. Не поддерживаются WAVE_FORMAT_EXTENSIBLE, RIFX, RF64 и другие кодеки. Остальные форматы приложение декодирует самостоятельно и передаёт как mono float32 PCM.

## Готовая запись в виде PCM

Для коротких записей доступен расширенный режим полного внимания без потокового состояния:

```csharp
using var model = DiarizationModel.LoadForRecording("nemotron3.gguf");
DiarizationSnapshot result = model.ProcessOfflineWithResults(
    monoFloat32Pcm, sampleRate: 16_000,
    new DiarizationResultOptions
    {
        SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference
    });
```

Это stateless full-attention inference с ограничением позиционной ёмкости модели. Для длинной PCM-записи передавайте ограниченные блоки в `OpenStream()`, применяйте каждый `ReadResultUpdates()` к `DiarizationProbabilityHistory(null)`, вызовите `Finish()` и прочитайте финальное обновление перед обработкой `history.GetFrames()`. Разрыв истории означает, что входные данные неполные.

## Live- или имитируемый поток

```csharp
await Nemotron3ReferenceExamples.RunLiveWaveAsync(
    "nemotron3.gguf", "microphone-capture.wav",
    update => Console.WriteLine($"Финальный результат={update.IsFinal}, сегментов={update.Segments.Count}"),
    cancellationToken);
```

Геометрия потока использует опубликованную конфигурацию 1,04 секунды: кэш говорящих — 264 кадра, FIFO — 264, блок — 9, правый контекст — 4, период обновления — 222. Обновления могут менять вероятности и сегменты; предварительные сегменты не являются неизменяемыми. В режиме reference `GetSnapshot()` сообщает абсолютные границы сохранённых кадров. После native-компактизации snapshot содержит только сохранённое окно. Финальный reference-результат отклоняется, если потеряны кадры до frame 0.

## Независимые настройки геометрии и сегментов

Геометрию можно изменить независимо от алгоритма сегментации:

```csharp
using var model = DiarizationModel.LoadForStreaming("nemotron3.gguf",
    new DiarizationModelOptions { ChunkFrames = 12, RightContextFrames = 5 });
```

Настройки NeMo задаются отдельно:

```csharp
var reference = new DiarizationResultOptions
{
    SegmentationMode = DiarizationSegmentationMode.NvidiaNeMoReference,
    NeMoReferenceOptions = new DiarizationPostprocessingOptions
    {
        Onset = 0.55f,
        Offset = 0.45f,
        PadOnsetSeconds = 0.02,
        PadOffsetSeconds = 0.02,
        MinGapSeconds = 0.10,
        MinDurationSeconds = 0.08
    }
};
DiarizationSnapshot customReference = model.ProcessWaveFile("meeting.wav", reference);
```

Чтобы явно выбрать исходную сегментацию native runtime:

```csharp
DiarizationSnapshot nativeResult =
    Nemotron3ReferenceExamples.RunNativeWave("nemotron3.gguf", "meeting.wav");
```

Проектный managed-алгоритм и прикладная обработка вероятностей:

```csharp
IReadOnlyList<DiarizationSegment> managedSegments =
    Nemotron3ReferenceExamples.RunApplicationPostprocessor(probabilityBatch,
        new DiarizationPostprocessingOptions { Onset = 0.6f, Offset = 0.4f });
```

`DiarizationSnapshot.GetProbability` и `ToFrameBatch()` открывают принадлежащие snapshot вероятности независимо от списка сегментов. `DiarizationPostprocessor.WriteRttm` записывает сегменты положительной длительности с нумерацией SDK от 1. Внутренние каналы NeMo называются `speaker_0`, `speaker_1`, …; SDK преобразует их в публичные идентификаторы `Speaker` с нумерацией от 1.

```csharp
Nemotron3ReferenceExamples.ExportRttm(Console.Out, "meeting", result);
```

## Источник и точное поведение reference-обработки

Выбранная первичная реализация — NVIDIA NeMo Speech **v3.0.0**, commit [`fd6a877539710e2b98f28c43272ff81312f83417`](https://github.com/NVIDIA-NeMo/Speech/commit/fd6a877539710e2b98f28c43272ff81312f83417): `SortformerEncLabelModel._diarize_output_processing` и `ts_vad_post_processing` в [`sortformer_diar_models.py`](https://github.com/NVIDIA-NeMo/Speech/blob/fd6a877539710e2b98f28c43272ff81312f83417/nemo/collections/asr/models/sortformer_diar_models.py) и [`vad_utils.py`](https://github.com/NVIDIA-NeMo/Speech/blob/fd6a877539710e2b98f28c43272ff81312f83417/nemo/collections/asr/parts/utils/vad_utils.py). Геометрию offline-style и низкой задержки описывает официальная [карточка Nemotron 3](https://huggingface.co/nvidia/Nemotron-3-Diarization).

Если YAML постобработки не передан, NeMo mixin использует пороги onset/offset 0,5, нулевые отступы и нулевые минимальные длительности речи и тишины. `ts_vad_post_processing` повторяет каждую вероятность на коэффициент subsampling encoder, затем выполняет бинаризацию и фильтрацию. Сегмент начинается только при вероятности строго выше onset; активный сегмент закрывается только при значении строго ниже offset, поэтому равенство сохраняет текущее состояние. Сначала применяются отступы, затем объединяются пересекающиеся интервалы. Фильтр сначала удаляет короткую речь, затем заполняет разрывы строго короче порога тишины. Последний активный сегмент заканчивается индексом последнего кадра 10 мс, а не исключительной границей следующего кадра. Sortformer добавляет смещение записи и округляет временные метки до двух знаков. Каналы говорящих обрабатываются независимо, поэтому пересечения сохраняются.

Адаптация SDK переводит cadence вероятностей native runtime в единицы 10 мс и преобразует публичные индексы говорящих к нумерации от 1. Тесты без модели проверяют случаи, выведенные из исходного кода; они не запускают реализацию NVIDIA на Python. В исходнике Sortformer для NeMo helper указано TODO: покрытие тестом mixin пока отсутствует. Поэтому заявляется совместимость с закреплённым исходником, а не независимо выполненная parity-проверка между runtime. Hugging Face Transformers использует другой путь обработки и здесь не подменяет первичный reference. Сегментация NeMo-Speech.cpp — отдельный вариант, доступный явно; её CALLHOME-настроенные значения не используются сценариями этих профилей.

## Offline-style streaming и full-attention inference

Offline-style означает потоковую геометрию с длинным контекстом, а не stateless full-attention inference. `LoadForRecording` использует эту геометрию, а `ProcessWaveFile` передаёт звук блоками через поток. `ProcessOffline` и `ProcessOfflineWithResults` — отдельные full-attention API с ограничением позиционной ёмкости.

GGUF-модель и native runtime являются адаптациями. SDK не заявляет численную эквивалентность исходному checkpoint NVIDIA, подтверждённое выполнение CUDA или идентичность внутренних runtime-путей.
