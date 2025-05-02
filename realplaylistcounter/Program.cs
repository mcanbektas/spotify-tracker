using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;

class UserConfig
{
    public string Owner { get; set; }
    public string PlaylistId { get; set; }
    public string AuthorizationCode { get; set; }
}

class Program
{
    static string clientId;
    static string clientSecret;
    static string redirectUri;
    static List<UserConfig> users;

    static readonly HttpClient httpClient = new();

    static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", false)
            .Build();

        Log.Logger = new LoggerConfiguration()
            .ReadFrom.Configuration(config)
            .CreateLogger();

        clientId = config["Spotify:ClientId"];
        clientSecret = config["Spotify:ClientSecret"];
        redirectUri = config["Spotify:RedirectUri"];
        users = config.GetSection("Spotify:Users").Get<List<UserConfig>>();

        Log.Information("Application started.");

        foreach (var user in users)
        {
            _ = Task.Run(() => RunForUser(user));
        }

        await Task.Delay(-1); // Sonsuza kadar çalış
    }

    static async Task RunForUser(UserConfig user)
    {
        string tokenPath = $"{user.Owner.ToLower()}_refresh_token.txt";
        string refreshToken = File.Exists(tokenPath) ? await File.ReadAllTextAsync(tokenPath) : null;

        if (string.IsNullOrEmpty(refreshToken))
        {
            refreshToken = await GetRefreshToken(user);
            if (refreshToken != null)
            {
                await File.WriteAllTextAsync(tokenPath, refreshToken);
                Log.Information($"{user.Owner} için yeni refresh token kaydedildi.");
            }
            else
            {
                Log.Error($"{user.Owner} için refresh token alınamadı.");
                return;
            }
        }

        string lastName = "";

        while (true)
        {
            var accessToken = await GetAccessToken(refreshToken);
            if (accessToken == null)
            {
                Log.Error($"{user.Owner} için access token alınamadı.");
                return;
            }

            int trackCount = await GetTrackCount(accessToken, user.PlaylistId);
            Log.Information($"[{user.Owner}] Track count: {trackCount}");

            if (trackCount.ToString() != lastName)
            {
                await UpdatePlaylistName(accessToken, user.PlaylistId, trackCount, user.Owner);
                lastName = trackCount.ToString();
            }
            else
            {
                Log.Information($"[{user.Owner}] Track sayısı değişmedi, isim güncellenmedi.");
            }

            await Task.Delay(TimeSpan.FromMinutes(10));
        }
    }

    static async Task<string> GetRefreshToken(UserConfig user)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Add("Authorization", $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"))}");
        req.Content = new StringContent($"grant_type=authorization_code&code={user.AuthorizationCode}&redirect_uri={redirectUri}", Encoding.UTF8, "application/x-www-form-urlencoded");

        var res = await httpClient.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode) return null;

        return JObject.Parse(body)["refresh_token"]?.ToString();
    }

    static async Task<string> GetAccessToken(string refreshToken)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Add("Authorization", $"Basic {Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"))}");
        req.Content = new StringContent($"grant_type=refresh_token&refresh_token={refreshToken}", Encoding.UTF8, "application/x-www-form-urlencoded");

        var res = await httpClient.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode) return null;

        return JObject.Parse(body)["access_token"]?.ToString();
    }

    static async Task<int> GetTrackCount(string token, string playlistId)
    {
        int total = 0, offset = 0, limit = 100;

        while (true)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks?offset={offset}&limit={limit}");
            req.Headers.Add("Authorization", $"Bearer {token}");

            var res = await httpClient.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                Log.Error($"Parça sayısı alınamadı: {body}");
                return 0;
            }

            var json = JObject.Parse(body);
            int count = json["items"].Count();
            total += count;
            
            if (count < limit) break;
            offset += limit;
        }

        return total;
    }

    static async Task UpdatePlaylistName(string token, string playlistId, int trackCount, string owner)
    {
        var url = $"https://api.spotify.com/v1/playlists/{playlistId}";
        var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Add("Authorization", $"Bearer {token}");

        var data = new { name = trackCount.ToString() };
        req.Content = new StringContent(Newtonsoft.Json.JsonConvert.SerializeObject(data), Encoding.UTF8, "application/json");

        var res = await httpClient.SendAsync(req);
        var response = await res.Content.ReadAsStringAsync();

        if (res.IsSuccessStatusCode)
            Log.Information($"[{owner}] Playlist adı güncellendi: {trackCount}");
        else
            Log.Error($"[{owner}] Playlist adı güncellenemedi: {response}");
    }
}
