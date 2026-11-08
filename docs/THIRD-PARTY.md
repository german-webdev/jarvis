# Сторонние компоненты и модели

Исходные сведения для сборки прототипа. Точные версии библиотек находятся в lock-файлах. Этот документ не заменяет тексты лицензий поставщиков. Бинарные файлы, SDK и модели не публикуются в Git-репозитории.

| Компонент | Источник условий и документации |
|---|---|
| .NET / WPF | [dotnet/wpf](https://github.com/dotnet/wpf) |
| CommunityToolkit.Mvvm | [CommunityToolkit/dotnet](https://github.com/CommunityToolkit/dotnet) |
| NAudio 2.2.1 | [NAudio](https://github.com/naudio/NAudio/tree/v2.2.1) |
| faster-whisper / CTranslate2 | [SYSTRAN/faster-whisper](https://github.com/SYSTRAN/faster-whisper), [CTranslate2](https://github.com/OpenNMT/CTranslate2) |
| Whisper small, преобразованный формат | [Systran/faster-whisper-small](https://huggingface.co/Systran/faster-whisper-small) |
| Vosk и русская малая модель | [Vosk API](https://github.com/alphacep/vosk-api), [модели](https://alphacephei.com/vosk/models) |
| Silero VAD ONNX | [snakers4/silero-vad](https://github.com/snakers4/silero-vad) |
| Piper | [OHF-Voice/piper1-gpl](https://github.com/OHF-Voice/piper1-gpl); GPL-3.0-or-later |
| Русский голос Дмитрий | [карточка модели](https://huggingface.co/rhasspy/piper-voices/blob/main/ru/ru_RU/dmitri/medium/MODEL_CARD); копия загружается вместе с моделью |
| Русский голос Денис | [карточка закреплённой модели](https://huggingface.co/rhasspy/piper-voices/blob/e7229f7d33a10e145e109b886d661984201ac3c4/ru/ru_RU/denis/medium/MODEL_CARD); локальная копия `piper/denis-MODEL_CARD` |
| ONNX Runtime | [microsoft/onnxruntime](https://github.com/microsoft/onnxruntime) |
| uv | [astral-sh/uv](https://github.com/astral-sh/uv) |

Синтез Piper и использование моделей требуют проверки условий перед распространением готового установщика. Разделение процессов само по себе не определяет лицензионные обязанности. У собственного исходного кода проекта лицензия пока не объявлена; не считать его автоматически MIT из-за публичности репозитория.
