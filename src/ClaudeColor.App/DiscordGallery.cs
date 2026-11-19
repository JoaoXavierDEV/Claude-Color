using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeColor.Core;

namespace ClaudeColor.App;

/// <summary>
/// GIFs para a imagem grande do Discord: lista os de docs/discord/gallery pela API do GitHub (um GIF novo no
/// repositório aparece sem recompilar) e baixa imagens por URL, com cache em disco, para a galeria e a prévia.
/// </summary>
internal static class DiscordGallery
{
    public sealed record Item(string Name, string Url);

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, Task<byte[]?>> Bytes = new(StringComparer.Ordinal);
    private static Task<IReadOnlyList<Item>>? _list;

    private static string CacheDir => Path.Combine(Storage.Directory, "cache");

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ClaudeColor"); // a API do GitHub exige um User-Agent
        return http;
    }

    /// <summary>A lista é buscada uma vez por execução (a API sem login permite 60 pedidos por hora).</summary>
    public static Task<IReadOnlyList<Item>> ListAsync() => _list ??= FetchListAsync();

    private static async Task<IReadOnlyList<Item>> FetchListAsync()
    {
        try
        {
            var json = JsonNode.Parse(await Http.GetStringAsync(DiscordConfig.GalleryApi)) as JsonArray;
            var items = (json ?? [])
                .Select(n => new Item((string?)n?["name"] ?? "", (string?)n?["download_url"] ?? ""))
                .Where(i => i.Name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) && DiscordConfig.IsValidImageUrl(i.Url))
                .ToList();
            if (items.Count > 0) return items;
        }
        catch (Exception ex) { Storage.Log($"discord: galeria: {ex.Message}"); }
        return DiscordConfig.BuiltInGallery.Select(n => new Item(n, DiscordConfig.GalleryBase + n)).ToList();
    }

    /// <summary>
    /// Imagem da URL (GIF animado preservado), ou null se não deu para baixar. Cada chamada devolve uma imagem nova
    /// (o mesmo GIF animado em dois controles disputa o quadro atual): quem recebe descarta.
    /// </summary>
    public static async Task<Image?> LoadAsync(string url)
    {
        Task<byte[]?> task;
        lock (Bytes)
        {
            if (!Bytes.TryGetValue(url, out task!) || task is { IsCompleted: true, Result: null })
                Bytes[url] = task = FetchAsync(url);
        }
        var bytes = await task;
        try
        {
            // O stream precisa ficar aberto enquanto a imagem existir (os quadros do GIF são lidos dele).
            return bytes is null ? null : Image.FromStream(new MemoryStream(bytes));
        }
        catch (Exception ex)
        {
            Storage.Log($"discord: imagem {url}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> FetchAsync(string url)
    {
        var file = Path.Combine(CacheDir, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16] + ".img");
        try
        {
            if (File.Exists(file) && DateTime.Now - File.GetLastWriteTime(file) < TimeSpan.FromDays(1))
                return await File.ReadAllBytesAsync(file);
            var bytes = await Http.GetByteArrayAsync(url);
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllBytesAsync(file, bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            Storage.Log($"discord: imagem {url}: {ex.Message}");
            return File.Exists(file) ? await File.ReadAllBytesAsync(file) : null; // sem internet: cache antigo
        }
    }
}
