using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nwn.Hosting.Contracts.Generations;

/// <summary>The bounded file protocol between the local publisher and permanent host.</summary>
public static class DevelopmentReloadFiles
{
    public const int CurrentFormatVersion = 1;
    private const int MaximumPayloadBytes = 8192;
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<DevelopmentReloadStatus>(allowIntegerValues: false) },
    };

    public static async Task<DevelopmentReloadRequest?> ReadRequestAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var text = Encoding.UTF8.GetString(bytes).Trim();
        if (text == "initial" || DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _)) return null;
        var request = JsonSerializer.Deserialize<DevelopmentReloadRequest>(bytes, Options)
            ?? throw new InvalidDataException("The development reload request is missing.");
        Validate(request.FormatVersion, request.GenerationId);
        return request;
    }

    public static async Task<DevelopmentReloadResult> ReadResultAsync(string path, CancellationToken cancellationToken)
    {
        var result = JsonSerializer.Deserialize<DevelopmentReloadResult>(await ReadAsync(path, cancellationToken).ConfigureAwait(false), Options)
            ?? throw new InvalidDataException("The development reload result is missing.");
        Validate(result.FormatVersion, result.GenerationId);
        if (!Enum.IsDefined(result.Status) || result.Detail is null)
            throw new InvalidDataException("The development reload result is invalid.");
        return result;
    }

    public static Task WriteRequestAsync(string path, DevelopmentReloadRequest request, CancellationToken cancellationToken)
    {
        Validate(request.FormatVersion, request.GenerationId);
        return WriteAsync(path, request, cancellationToken);
    }

    public static Task WriteResultAsync(string path, DevelopmentReloadResult result, CancellationToken cancellationToken)
    {
        Validate(result.FormatVersion, result.GenerationId);
        if (!Enum.IsDefined(result.Status) || result.Detail is null)
            throw new InvalidDataException("The development reload result is invalid.");
        return WriteAsync(path, result, cancellationToken);
    }

    private static void Validate(int formatVersion, Guid generationId)
    {
        if (formatVersion != CurrentFormatVersion || generationId == Guid.Empty)
            throw new InvalidDataException("The development reload protocol version or generation identity is invalid.");
    }

    private static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length > MaximumPayloadBytes) throw new InvalidDataException("The development reload payload is too large.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return bytes;
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (bytes.Length > MaximumPayloadBytes) throw new InvalidDataException("The development reload payload is too large.");
        // Preserve the inode of a single-file Docker bind mount. Readers retry partial writes.
        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
    }
}
