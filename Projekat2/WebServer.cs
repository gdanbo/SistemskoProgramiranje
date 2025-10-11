using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public class WebServer
{
    private readonly HttpListener _listener = new HttpListener();
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private readonly HttpClient _client = new HttpClient();
    private bool _running;

    public WebServer(string prefix)
    {
        _listener.Prefixes.Add(prefix);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _running = true;

        while (_running)
        {
            try
            {
                HttpListenerContext context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception: {ex.Message}");
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var resp = ctx.Response;

        //if (!req.Url.ToString().Contains("favicon"))
        //{
            Console.WriteLine($"[{DateTime.Now:dd.MM.yyyy HH:mm}] {req.HttpMethod} {req.Url}");
        //}

        try
        {
            if (req.HttpMethod != "GET" || req.Url.AbsolutePath != "/search")
            {
                await WriteJsonAsync(resp, 405, "{\"error\": \"Koristiti GET metodu sa /search?q=...\"}");
                return;
            }

            string query = req.QueryString["q"];
            if (string.IsNullOrWhiteSpace(query))
            {
                await WriteJsonAsync(resp, 400, "{\"error\":\"Nedostaje parametar q.\"}");
                return;
            }

            string googleUrl = $"https://www.googleapis.com/books/v1/volumes?q={Uri.EscapeDataString(query)}";

            if (_cache.TryGetValue(googleUrl, out string? cachedResponse))
            {
                await WriteJsonAsync(resp, 200, cachedResponse); // vracamo kesiran odgovor
                return;
            }

            string responseBody;
            try
            {
                HttpResponseMessage response = await _client.GetAsync(googleUrl);
                responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    string err = $"{{\"error\":\"Google Books API status: {(int)response.StatusCode}\"}}";
                    await WriteJsonAsync(resp, (int)response.StatusCode, err);
                    return;
                }
            }
            catch (Exception ex)
            {
                string err = $"{{\"error\":\"Google Books API greška: {ex.Message}\"}}";
                await WriteJsonAsync(resp, 502, err);
                return;
            }

            if (!responseBody.Contains("\"items\""))
            {
                await WriteJsonAsync(resp, 404, "{\"error\":\"Nije pronađena nijedna knjiga.\"}");
                return;
            }

            await _cacheLock.WaitAsync(); // cekamo semafor
            try
            {
                _cache.TryAdd(googleUrl, responseBody);
            }
            finally
            {
                _cacheLock.Release();
            }

            await WriteJsonAsync(resp, 200, responseBody);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(resp, 500, $"{{\"error\":\"Greška: {ex.Message}\"}}");
        }
        finally
        {
            resp.OutputStream.Close();
        }
    }

    private async Task WriteJsonAsync(HttpListenerResponse resp, int statusCode, string json)
    {
        byte[] data = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = statusCode;
        resp.ContentType = "application/json; charset=utf-8";
        resp.ContentLength64 = data.Length;
        await resp.OutputStream.WriteAsync(data, 0, data.Length);
    }
}

