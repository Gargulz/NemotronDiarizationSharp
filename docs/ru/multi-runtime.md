# Несколько native runtime

NemotronDiarizationSharp использует native C API NeMo-Speech.cpp. Соберите managed SDK из исходников и отдельно предоставьте совместимые native runtime и модель GGUF. NuGet-пакеты не опубликованы.

## Backend по умолчанию

Свойство `DiarizationModelOptions.Backend` по умолчанию равно `DiarizationBackend.Auto`. На Windows x64 режим Auto выбирает CUDA runtime, если `nvidia-smi` сообщает об устройстве NVIDIA. Иначе он использует CPU runtime, если тот установлен. Если установлен только CUDA runtime, а устройство не найдено, возвращается понятная ошибка.

Проверка `nvidia-smi` выполняется до загрузки native runtime. Она подтверждает наличие устройства в списке, но не доказывает, что драйвер поддерживает каждую CUDA-библиотеку или kernel. Если CUDA выбрана, а последующая загрузка native-библиотеки, создание модели или инференс завершаются ошибкой, процесс возвращает ошибку и не переключается на CPU: native-библиотеки уже могли загрузиться. Если `nvidia-smi` не находит устройство и установлен CPU runtime, Auto выбирает CPU до загрузки любой native-группы. Политика проверяется managed-тестами; реальный переход на CPU на машине без GPU в этой среде не запускался.

`Backend = DiarizationBackend.Cpu` требует CPU и не проверяет и не загружает CUDA. `Backend = DiarizationBackend.Cuda` требует CUDA-пакет и устройство, которое сообщает `nvidia-smi`; перехода на CPU нет. Совместимость CUDA на конкретном компьютере подтверждается только успешным инференсом.

Первый native-вызов фиксирует один backend для процесса. Параллельная инициализация сериализуется. Следующие модели могут использовать тот же backend; Auto после явного выбора принимает уже установленный выбор. Запрос переключить CPU на CUDA или обратно вызывает ошибку. Для сравнения backend запускайте отдельные процессы. До инициализации `DiarizationModel.SelectedBackend` равен `null`, после неё показывает выбор процесса.

`GpuDeviceIndex` — логический ordinal CUDA; он не выбирает backend. Если выбрана CUDA, `null` означает ordinal 0. `nvidia-smi` используется только для предварительной проверки наличия NVIDIA-устройства; CUDA проверяет ordinal при создании модели. Это не смешивает физические индексы `nvidia-smi` с логическими ordinal CUDA при масках видимости и переназначении. Режимы с маской и несколькими GPU пока не проверялись. Индекс нельзя сочетать с принудительным CPU или с Auto, если Auto выбрал CPU.

## Простой пример

```csharp
using NemotronDiarizationSharp;

using var model = DiarizationModel.LoadForRecording(
    "models/Nemotron-3-Diarization.q8_0.gguf");

using var stream = model.OpenStream();
// Передайте массивы float32 PCM, завершите поток и прочитайте результат.
```

Для первого запуска используйте подключение SDK из исходников и явный CPU runtime, как показано в README репозитория. AllRuntimes описывает отдельный локальный пакет агрегации, а не доступную публичную установку. Для CUDA нужны совместимые NVIDIA GPU и драйвер; CPU требует совместимый Visual C++ x64 runtime с OpenMP. SDK не скачивает native-файлы и модель GGUF.

Для выбора backend передайте DiarizationBackend.Cpu или DiarizationBackend.Cuda в DiarizationModelOptions. LoadForRecording загружает профиль записи; для декодирования и обработки WAV вызовите ProcessWaveFile. LoadForStreaming загружает профиль живого потока; откройте поток, последовательно подавайте PCM и вызовите Finish. Оба профиля по умолчанию используют NVIDIA Reference; выбор backend не меняет сегментацию.

## Runtime-пакеты

- `NemotronDiarizationSharp` содержит managed API без native-бинарников.
- `NemotronDiarizationSharp.Runtime.Cpu` содержит CPU native closure.
- `NemotronDiarizationSharp.Runtime.Cuda` содержит CUDA native closure.
- `NemotronDiarizationSharp.AllRuntimes` — convenience metapackage, зависящий от всех трёх пакетов.

Runtime-пакеты сохраняют файлы в отдельных каталогах:

```text
runtimes/nemotron/cpu/win-x64/native/
runtimes/nemotron/cuda/win-x64/native/
```

Независимые .NET 10-потребители были восстановлены и опубликованы из локального feed для CPU, CUDA, обоих runtime-пакетов и AllRuntimes. Проверка модулей подтвердила, что совпадающие имена DLL загружаются из своих каталогов. Проверенная платформа — Windows x64; Linux x64 пока не проверялся.

Для CUDA нужны совместимые NVIDIA GPU и драйвер, а также зависимости CUDA выбранного runtime. Записи и потоковая обработка завершились на NVIDIA GPU. Native-диагностика сообщила о compute buffer `CUDA0`, но супервизор процесса оставляет прямое выполнение kernel в статусе unknown. Эти результаты не гарантируют работу на других GPU и драйверах.

Веса модели предоставляются отдельно. Native runtime-пакеты не опубликованы; проверка условий распространения сторонних бинарных файлов и их происхождения не завершена. Успешное локальное восстановление пакетов не предоставляет прав на распространение.

См. [готовность к выпуску](release-readiness.md): там приведены аудит метаданных пакетов, перечень native-компонентов, классификация подтверждений и точные блокеры публичного выпуска.
