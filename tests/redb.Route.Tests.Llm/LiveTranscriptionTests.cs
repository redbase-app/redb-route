using System.Text;

using FluentAssertions;

using redb.Route.Llm;
using redb.Route.Llm.Providers;
using redb.Route.Tests.Llm.TestHelpers;

namespace redb.Route.Tests.Llm;

/// <summary>
/// Живая проверка транспорта распознавания против настоящего сервера.
///
/// <para>Стабовые тесты (<see cref="TranscriptionProviderTests"/>) доказывают форму запроса
/// так, как её задумал автор транспорта. Чего они не доказывают — что ИМЕННО такую форму
/// принимает настоящий сервер: имена полей формы, имя файла, тип содержимого и то, как он
/// поймёт <c>response_format</c>. Ровно здесь и ломаются multipart-транспорты, и увидеть это
/// стабом нельзя в принципе.</para>
///
/// <para>Включается переменной <c>REDB_STT_BASE_URL</c> — например
/// <c>http://127.0.0.1:8083/v1/</c> (whisper.cpp server, Speaches, LM Studio или
/// <c>TGChatLmm/deploy/stt</c>). Не задана — факт пропускается, как и остальные живые.</para>
/// </summary>
public sealed class LiveTranscriptionTests
{
    private const string BaseUrlVariable = "REDB_STT_BASE_URL";
    private const string ModelVariable = "REDB_STT_MODEL";

    private static OpenAiTranscriptionProvider Provider()
    {
        var baseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable)!;

        return OpenAiTranscriptionProvider.Create(new LlmConnectionFactory
        {
            Name = "live-stt",
            Provider = "whisper",
            ModelId = Environment.GetEnvironmentVariable(ModelVariable) ?? "medium",
            BaseUrl = new Uri(baseUrl),
            ApiKey = Environment.GetEnvironmentVariable("REDB_STT_KEY"),
            RequestTimeoutMs = 300_000,
        });
    }

    /// <summary>
    /// Валидный WAV: секунда тона, секунда тишины. Речи в нём нет, и это здесь не помеха, а
    /// удобство — проверяется транспорт, а не слух модели.
    /// </summary>
    private static byte[] ToneWav(int seconds = 2, int rate = 16000, double hz = 220)
    {
        var samples = rate * seconds;
        var data = new byte[samples * 2];

        for (var i = 0; i < samples; i++)
        {
            short value = i < samples / 2
                ? (short)(12000 * Math.Sin(2 * Math.PI * hz * i / rate))
                : (short)0;
            data[i * 2] = (byte)(value & 0xFF);
            data[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + data.Length);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);                       // размер блока формата
        writer.Write((short)1);                 // PCM
        writer.Write((short)1);                 // моно
        writer.Write(rate);
        writer.Write(rate * 2);                 // байт в секунду
        writer.Write((short)2);                 // выравнивание блока
        writer.Write((short)16);                // бит на отсчёт
        writer.Write("data"u8.ToArray());
        writer.Write(data.Length);
        writer.Write(data);
        writer.Flush();

        return stream.ToArray();
    }

    [EnvFact(BaseUrlVariable)]
    public async Task ЖивойСервер_ПринимаетНашуФормуЗагрузки()
    {
        var provider = Provider();

        var result = await provider.TranscribeAsync(
            new TranscriptionRequest(ToneWav(), "probe.wav", Language: "ru"));

        // Текст может быть любым, включая пустой — сервер вправе не услышать в тоне речи.
        // Доказывается другое: запрос дошёл, был понят и разобран без исключения.
        result.Text.Should().NotBeNull();
        provider.ModelId.Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// ⚠️ Запись без речи обязана вернуться ПУСТОЙ. Whisper на тишине не молчит, а
    /// досочиняет — на первом же прогоне синтетического тона он выдал «С вами был Игорь
    /// Негода» (типовая галлюцинация из ютубовских титров). Для маршрута это не курьёз:
    /// выдуманная фраза уехала бы в модель как слова человека.
    ///
    /// <para>Гасит её сам сервер (порог <c>no_speech_prob</c>), а не транспорт, — поэтому
    /// факт живой: проверяется стенд, а не наш код.</para>
    /// </summary>
    [EnvFact(BaseUrlVariable)]
    public async Task ЖивойСервер_НаЗаписиБезРечи_НеВыдумывает()
    {
        var result = await Provider().TranscribeAsync(
            new TranscriptionRequest(ToneWav(), "probe.wav", Language: "ru"));

        result.Text.Should().BeEmpty(
            "запись без речи обязана возвращаться пустой строкой: досочинённая фраза уехала бы "
            + "в разговор как слова человека");
    }
}
