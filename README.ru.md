# NemotronDiarizationSharp

[English](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/README.md)

[Репозиторий на GitHub](https://github.com/Gargulz/NemotronDiarizationSharp)

Независимый неофициальный .NET SDK для диаризации: определяет, кто и когда говорил в записи или потоке, сохраняет перекрывающуюся речь и экспортирует RTTM. Нейросетевой инференс выполняется через native C API [NVIDIA NeMo-Speech.cpp](https://github.com/NVIDIA/NeMo-Speech.cpp). Проект не связан с NVIDIA и не одобрен ею.

## Требования и доступность

- .NET 10; локально проверены Windows x64 CPU и CUDA с runtime NeMo-Speech.cpp v0.2.0. Linux и Vulkan не проверены; широкая совместимость с GPU и драйверами не подтверждена.
- Совместимые native runtime со всеми зависимостями и модель GGUF предоставляются отдельно. Managed SDK их не содержит и не скачивает. Для CPU нужен совместимый Visual C++ x64 runtime с OpenMP; CUDA также требует совместимые NVIDIA GPU и драйвер.
- Кандидат `0.1.0-preview.1` — пакет только с managed SDK. Проекты пакетов native runtime не входят в этот выпуск и недоступны в NuGet. Проверка прав на распространение native runtime не завершена; MIT-лицензия SDK не предоставляет прав на сторонние бинарные файлы и веса модели.

После публикации prerelease добавьте его в приложение .NET 10:

```powershell
dotnet add package NemotronDiarizationSharp --version 0.1.0-preview.1
```

Пакет не содержит native runtime или модель: приложение должно получить их отдельно. См. [подготовку и выбор runtime](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/multi-runtime.md) и [условия распространения](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/DISTRIBUTION_STATUS.md).

## Быстрый старт

Чтобы собрать пример напрямую из checkout репозитория, создайте приложение и подключите managed SDK из исходников:

```powershell
dotnet new console -n DiarizationDemo -o ../DiarizationDemo -f net10.0
dotnet add ../DiarizationDemo/DiarizationDemo.csproj reference src/NemotronDiarizationSharp/NemotronDiarizationSharp.csproj
$env:NEMOTONDIARIZATIONSHARP_NATIVE_DIR = '<absolute-cpu-runtime-directory>'
```

Замените созданный `Program.cs` этим примером и укажите существующие файлы модели и аудио:

<!-- compile-example: ru-recording; scaffold=recording -->
```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording("model.gguf",
    new DiarizationModelOptions { Backend = DiarizationBackend.Cpu });
DiarizationSnapshot result = model.ProcessWaveFile("meeting.wav");
DiarizationPostprocessor.WriteRttm(Console.Out, result.Segments, "meeting");
```

```powershell
dotnet run --project ../DiarizationDemo/DiarizationDemo.csproj -c Release
```

SDK читает little-endian RIFF/WAVE PCM 8/16/24/32 бит и IEEE float32 с частотой 8–96 кГц, сводя каналы в моно усреднением. Для MP3, сжатого WAV, WAVE_FORMAT_EXTENSIBLE, RF64 и RIFX требуется внешнее преобразование. Можно также передавать готовый моно PCM float32. Сценарий записи сохраняет историю вероятностей для финальной постобработки, поэтому расход памяти растёт с длительностью записи.

## Потоковая обработка и backend

Для живого потока используйте `LoadForStreaming`, `OpenStream`, последовательную подачу PCM и `Finish`. Обновления могут пересматривать прежние результаты; освобождайте модель и поток. См. [сценарии записи и потока](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/nemotron3-reference-workflows.md) и [руководство SDK](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/sdk-guide.md).

По умолчанию `Backend` равен `Auto`: выбирается подготовленный CUDA runtime, если `nvidia-smi` сообщает об устройстве; иначе используется доступный CPU runtime. Принудительный выбор CPU/CUDA требует соответствующий backend. Первая native-инициализация фиксирует backend для процесса; после ошибки native-загрузки переключение не выполняется.

## Сборка и проверка

Для сборки managed SDK runtime и модель не нужны:

```powershell
dotnet build src/NemotronDiarizationSharp/NemotronDiarizationSharp.csproj -c Release -m:1
```

Команды сборки решения и managed-тестов приведены в [CONTRIBUTING](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/CONTRIBUTING.md#validation). Managed-тесты не подтверждают native inference или готовность к распространению бинарных пакетов.

## Документация

- [Примеры SDK и контракты API](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/sdk-guide.md)
- [Сценарии записи и живого потока](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/nemotron3-reference-workflows.md)
- [Выбор и подготовка CPU/CUDA](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/multi-runtime.md)
- [Обзор проекта](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/overview.md) и [архитектура](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/ru/architecture.md)
- [Доступность пакетов и сторонние условия](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/docs/distribution/DISTRIBUTION_STATUS.md)

## Лицензия и благодарности

Оригинальные исходники проекта распространяются по [MIT](https://github.com/Gargulz/NemotronDiarizationSharp/blob/main/LICENSE), copyright Gargulz. Сторонние исходники, native-компоненты и веса модели сохраняют собственные лицензии и уведомления.

Одним из источников вдохновения стал [Whisper.net](https://github.com/sandrohanea/whisper.net). При разработке также использовался [OpenAI Codex](https://openai.com/codex/).
